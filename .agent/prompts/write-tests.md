# 测试编写指南

OpenAgent 项目的测试规范和最佳实践。

---

## 测试类型与位置

测试类型、框架与位置 → `.agent/rules/coding-conventions.md` §9.1

---

## xunit 单元测试（Core / Engine）

### 测试模板

```csharp
[Fact]
public async Task MethodName_Scenario_ExpectedBehavior()
{
    // Arrange — 准备测试数据和 Mock
    var mockEngine = new MockEngine();
    var pipeline = new Pipeline(mockEngine);

    // Act — 执行被测方法
    var result = await pipeline.ExecuteAsync(request);

    // Assert — 验证结果
    Assert.NotNull(result);
    Assert.Equal(expected, result.Status);
}

[Theory]
[InlineData("input1", "expected1")]
[InlineData("input2", "expected2")]
public async Task MethodName_WithDifferentInputs_ReturnsExpected(
    string input, string expected)
{
    // 同上 Arrange/Act/Assert 模式
}
```

### 命名规范
`方法名_场景_预期行为`

### 关键规则
- Mock 通过 `InternalsVisibleTo` 访问内部类型
- 优先用已有的 `MockEngine`（Agent.Core 内），不造新轮子
- 每个公共接口方法至少一个测试
- 必须覆盖错误路径（不仅仅是 happy path）

### 参数化测试（减少重复代码）

当多个测试用例仅输入和预期输出不同时，**必须**使用参数化测试，避免复制粘贴：

```csharp
// ✅ Correct: 使用 Theory + MemberData 合并同类用例
public static TheoryData<string, ApiFormat, string> UriResolutionData => new()
{
    { "https://api.openai.com/v1", ApiFormat.OpenAICompatible, "https://api.openai.com/v1/chat/completions" },
    { "http://localhost:11434", ApiFormat.Ollama, "http://localhost:11434/v1/chat/completions" },
    { "https://custom.example.com", ApiFormat.Custom, "https://custom.example.com/v1/chat/completions" },
};

[Theory]
[MemberData(nameof(UriResolutionData))]
public async Task ChatCompletionAsync_ResolvesCorrectUri(string endpoint, ApiFormat format, string expectedUri)
{
    // Arrange + Act + Assert
}

// ✅ Correct: 简单标量参数用 InlineData
[Theory]
[InlineData("")]
[InlineData("   ")]
public async Task ChatCompletionAsync_EmptyEndpoint_ThrowsInvalidOperationException(string endpoint)
{
    // Arrange + Act + Assert
}

// ❌ Incorrect: 每组输入写一个 [Fact]，代码臃肿
[Fact]
public void Uri_EndsWithV1_AppendsChatCompletions() { ... }
[Fact]
public void Uri_OllamaFormat_AppendsV1ChatCompletions() { ... }
[Fact]
public void Uri_CustomFormat_AppendsV1ChatCompletions() { ... }
```

**选择依据：**
- 简单标量参数（string, int, enum）→ `[Theory]` + `[InlineData]`
- 复杂类型或多参数组合 → `[Theory]` + `MemberData`
- 逻辑分支完全不同 → 保留独立 `[Fact]`

### 可见性原则（不要为测试改生产代码）

- **禁止**为方便测试而将 `private` 方法改为 `internal` 或 `public`
- 应通过公共 API 间接测试私有方法的行为
- 示例：测试 `BuildChatCompletionsUri`（private）→ 通过 `ChatCompletionAsync` 发送请求并捕获实际 URI

---

## MSTest 集成测试（TestCode/Agent.TestEngine/）

### 测试模板

```csharp
[TestClass]
public class ChatTests
{
    private static EngineTestHost _host;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext context)
    {
        _host = await EngineTestHost.CreateAsync();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        await _host.DisposeAsync();
    }

    [TestInitialize]
    public async Task TestInitialize()
    {
        _host.Llm.ResetLogs();  // 隔离测试间的 Mock 状态
    }

    [TestMethod]
    public async Task SendMessage_WithValidInput_ReturnsResponse()
    {
        var response = await _host.SendMessage("test message");
        Assert.AreEqual(200, response.StatusCode);
    }
}
```

### 关键规则
- 服务必须先启动（用 `service-lifecycle` 技能）
- `[ClassInitialize]` / `[ClassCleanup]` 管理宿主生命周期
- `[TestInitialize]` 重置 Mock 状态保证隔离
- 使用 `TestCode/Agent.TestFramework/` 中的工具类

### 测试覆盖范围
1. **ChatTests** — 边界、并发、容错
2. **ChatStreamingTests** — NDJSON、SSE、工具调用、并发
3. **LlmTests** — 文本、工具调用、多轮、错误
4. **McpTests** — 单/多工具、错误、流式
5. **SkillTests** — 调用、自定义响应、禁用
6. **SsoTests** — Token、Fake Auth、错误

---

## PowerShell E2E 测试（TestCode/scripts/）

### 关键脚本

| 脚本 | 作用 |
|------|------|
| `test-e2e.ps1` | 完整 E2E（构建+启动+测试+清理） |
| `run-all-tests.ps1` | 运行所有集成测试 |
| `integration/test-it-mcp-protocol.ps1` | MCP 协议测试 |
| `integration/test-it-engine-endpoints.ps1` | Engine 端点测试 |
| `integration/test-it-skill-mcp-integration.ps1` | Skill-MCP 集成测试 |
| `specialized/test-it-negative.ps1` | 负面场景测试 |
| `specialized/test-pt-load.ps1` | 负载/性能测试 |

### 测试帮助函数（test-helpers.psm1）

| 函数 | 用途 |
|------|------|
| `Import-TestEnv` | 从 .env 加载环境变量 |
| `Get-AvailableProviders` | 获取有可用 Key 的提供商列表 |
| `Update-AgentConfigs` | 重写 Agent JSON 配置 |

---

## 测试最佳实践

1. **单元测试优先**：新功能先在 Core/Engine 层加 xunit 测试
2. **一个测试一个断言**：每个 [Fact] 验证一个行为
3. **隔离外部依赖**：LLM、Redis、MCP、SSO 全部 Mock
4. **场景驱动测试**：复杂流程用 `testscenarios.json` 定义（参考 `ChatScenarioTests.cs`）
5. **测试数据独立**：每个测试自己准备数据，不依赖执行顺序
6. **清理资源**：`IAsyncDisposable` 确保 Mock 和连接正确释放
