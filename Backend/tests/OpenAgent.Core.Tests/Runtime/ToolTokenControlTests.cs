using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Runtime.Agent;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

public class ToolTokenControlTests
{
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

    // ---- search_tools：检索登记 + 注入器逐轮合并 ----

    [Fact]
    public async Task SearchTools_InvokeRegistersActivationAndReturnsMatches()
    {
        var catalog = new DeferredToolCatalog([
            Stub("mcp__cal__create_event", "Creates a calendar event")
        ]);
        var search = new ToolSearchFunction(catalog);

        string result = await InvokeAsync(search, new Dictionary<string, object?>
        {
            ["query"] = "calendar"
        });

        using JsonDocument document = JsonDocument.Parse(result);
        Assert.Equal("mcp__cal__create_event",
            document.RootElement.GetProperty("newlyActivated")[0].GetString());
        // 目录登记激活（幂等），注入由 DeferredToolInjector 在请求边界完成。
        await InvokeAsync(search, new Dictionary<string, object?> { ["query"] = "calendar" });
        Assert.Equal(["mcp__cal__create_event"], catalog.Activated);
    }

    [Fact]
    public async Task SearchTools_NoMatch_ReturnsEmptyListWithHint()
    {
        var search = new ToolSearchFunction(new DeferredToolCatalog([Stub("mcp__a__x", "irrelevant")]));

        string result = await InvokeAsync(search, new Dictionary<string, object?> { ["query"] = "nothing" });

        using JsonDocument document = JsonDocument.Parse(result);
        Assert.Equal(0, document.RootElement.GetProperty("tools").GetArrayLength());
        Assert.Contains("No deferred tool matched",
            document.RootElement.GetProperty("hint").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchTools_MissingQuery_ReturnsInvalidArgumentsEnvelope()
    {
        var search = new ToolSearchFunction(new DeferredToolCatalog([]));

        string result = await InvokeAsync(search, new Dictionary<string, object?>());

        using JsonDocument document = JsonDocument.Parse(result);
        Assert.Equal("invalid_arguments", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Injector_MergesActivatedToolsIntoEachRequestOptions()
    {
        AITool deferred = Stub("mcp__cal__create_event", "Creates a calendar event");
        var catalog = new DeferredToolCatalog([deferred]);
        var injector = new DeferredToolInjector(new CapturingChatClient(), catalog, tool => new TaggedTool(tool.Name));
        var options = new ChatOptions();
        options.Tools = [Stub("read_file", "inline")];

        // 未激活时不改写。
        await injector.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None);
        ChatOptions first = ((CapturingChatClient)injector.GetService(typeof(CapturingChatClient))!).LastOptions!;
        Assert.Single(first.Tools!);

        // 激活后每轮请求都带上（经 wrap 工厂），且幂等不重复。
        catalog.Activate(["mcp__cal__create_event"]);
        await injector.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None);
        await injector.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None);
        Assert.Equal(2, first.Tools!.Count);
        Assert.Contains(first.Tools!, tool => tool is TaggedTool && tool.Name == "mcp__cal__create_event");
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => this;

        public void Dispose() { }
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
