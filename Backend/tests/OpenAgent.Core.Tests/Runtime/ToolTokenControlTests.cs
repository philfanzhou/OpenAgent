using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Runtime.Agent;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

public class ToolTokenControlTests
{
    // ---- 每代理工具裁剪 ----

    [Theory]
    [InlineData("write_file", true)]
    [InlineData("WRITE_FILE", true)]
    [InlineData("mcp__github__create_issue", true)]
    [InlineData("mcp__slack__post", false)]
    [InlineData("read_file", false)]
    public void IsDisabled_MatchesExactNameOrPrefixWildcard(string name, bool expected)
    {
        IReadOnlyList<string> patterns = ["write_file", "mcp__github__*"];

        Assert.Equal(expected, ToolSelection.IsDisabled(patterns, name));
    }

    [Fact]
    public void IsDisabled_EmptyOrBlankPatterns_NeverDisable()
    {
        Assert.False(ToolSelection.IsDisabled(["", "  "], "anything"));
    }

    // ---- 延迟目录：检索与激活 ----

    [Fact]
    public void Search_RanksNameHitsAboveDescriptionHits()
    {
        var catalog = new DeferredToolCatalog([
            Stub("mcp__cal__create_event", "Creates a calendar event"),
            Stub("mcp__hr__lookup", "look up calendar holidays"),
            Stub("mcp__wiki__search", "Search the wiki")
        ]);

        IReadOnlyList<(string Name, string Description)> results = catalog.Search("calendar", 10);

        Assert.Equal("mcp__cal__create_event", results[0].Name);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, item => item.Name == "mcp__hr__lookup");
    }

    [Fact]
    public void Search_MultiWordQuery_SumsTermScores()
    {
        var catalog = new DeferredToolCatalog([
            Stub("mcp__issue__create", "Create an issue tracker ticket"),
            Stub("mcp__doc__read", "Read documentation")
        ]);

        IReadOnlyList<(string Name, string Description)> results = catalog.Search("issue tracker", 10);

        Assert.Single(results, item => item.Name == "mcp__issue__create");
    }

    [Fact]
    public void Search_RespectsLimit()
    {
        var catalog = new DeferredToolCatalog([
            Stub("a_search", ""), Stub("b_search", ""), Stub("c_search", "")
        ]);

        Assert.Equal(2, catalog.Search("search", 2).Count);
    }

    [Fact]
    public void Activate_IsIdempotentAndReturnsOnlyNewTools()
    {
        AITool first = Stub("mcp__a__x", "");
        AITool second = Stub("mcp__b__y", "");
        var catalog = new DeferredToolCatalog([first, second]);

        IReadOnlyList<AITool> firstRound = catalog.Activate(["mcp__a__x"]);
        IReadOnlyList<AITool> secondRound = catalog.Activate(["mcp__a__x", "mcp__b__y"]);

        Assert.Equal([first], firstRound);
        Assert.Equal([second], secondRound);
        Assert.Equal(["mcp__a__x", "mcp__b__y"], catalog.Activated);
    }

    // ---- search_tools：激活即注入 + 信封语义 ----

    [Fact]
    public async Task SearchTools_InvokeActivatesWrappedToolsIntoTargetList()
    {
        var catalog = new DeferredToolCatalog([
            Stub("mcp__cal__create_event", "Creates a calendar event")
        ]);
        List<AITool> target = [];
        // wrap 用可识别的标记类型，验证激活走了包装工厂（生产中是 IsolatedToolFunction）。
        var search = new ToolSearchFunction(catalog, target, tool => new TaggedTool(tool.Name));

        string result = await InvokeAsync(search, new Dictionary<string, object?>
        {
            ["query"] = "calendar"
        });

        using JsonDocument document = JsonDocument.Parse(result);
        Assert.Equal("mcp__cal__create_event",
            document.RootElement.GetProperty("newlyActivated")[0].GetString());
        AITool injected = Assert.Single(target);
        Assert.IsType<TaggedTool>(injected);
        Assert.Equal("mcp__cal__create_event", injected.Name);

        // 幂等：重复检索不重复注入。
        await InvokeAsync(search, new Dictionary<string, object?> { ["query"] = "calendar" });
        Assert.Single(target);
    }

    [Fact]
    public async Task SearchTools_NoMatch_ReturnsEmptyListWithHint()
    {
        var catalog = new DeferredToolCatalog([Stub("mcp__a__x", "irrelevant")]);
        var search = new ToolSearchFunction(catalog, [], _ => throw new InvalidOperationException());

        string result = await InvokeAsync(search, new Dictionary<string, object?> { ["query"] = "nothing" });

        using JsonDocument document = JsonDocument.Parse(result);
        Assert.Equal(0, document.RootElement.GetProperty("tools").GetArrayLength());
        Assert.Contains("No deferred tool matched",
            document.RootElement.GetProperty("hint").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchTools_MissingQuery_ReturnsInvalidArgumentsEnvelope()
    {
        var search = new ToolSearchFunction(new DeferredToolCatalog([]), [], _ => throw new InvalidOperationException());

        string result = await InvokeAsync(search, new Dictionary<string, object?>());

        using JsonDocument document = JsonDocument.Parse(result);
        Assert.Equal("invalid_arguments", document.RootElement.GetProperty("code").GetString());
    }

    private static async Task<string> InvokeAsync(AIFunction function, IReadOnlyDictionary<string, object?> arguments)
    {
        object? result = await function.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>(arguments)),
            CancellationToken.None);
        return Assert.IsType<string>(result);
    }

    private static AITool Stub(string name, string description) => new TaggedTool(name, description);

    private sealed class TaggedTool(string name, string description = "") : AIFunction
    {
        public override string Name => name;
        public override string Description => description;
        public override JsonElement JsonSchema =>
            JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>("ok");
    }
}
