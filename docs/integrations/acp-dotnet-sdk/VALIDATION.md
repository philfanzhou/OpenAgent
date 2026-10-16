# 验证与兼容策略

## 本地检查

```sh
dotnet build Backend/OpenAgent.sln
dotnet test Backend/OpenAgent.sln
dotnet test Backend/tests/OpenAgent.Runtime.Acp.Tests
dotnet run --project Backend/tools/OpenAgent.Runtime.Acp.TestConsole -- run all --json
dotnet publish Backend/tools/OpenAgent.Runtime.Acp.TestConsole -c Release -r osx-arm64 -p:PublishAot=true -o .preview/acp-aot
.preview/acp-aot/OpenAgent.Runtime.Acp.TestConsole run all --json
dotnet pack Backend/src/OpenAgent.Runtime.Acp -c Release -o .preview/acp-package
python3 scripts/check-acp-package.py .preview/acp-package
```

替换 AOT RID/可执行文件后可在对应平台复现；不能在 macOS 上构建成功就宣称 Linux/Windows 运行通过。`.github/workflows/acp-sdk.yml` 提供三平台 SDK 测试与 AOT 场景，以及 Ubuntu 官方 SDK 互通、包检查。

## 官方 SDK 双向互通

夹具位于 `Backend/tests/OpenAgent.Runtime.Acp.Interop/`，依赖版本见 [来源锁定](sources.json)。Node 使用 npm lock；Python 需要 3.12+；Rust 使用固定 git rev 与 Cargo.lock。示例准备及运行：

```sh
python3 scripts/check-acp-contracts.py # 显式联网核验摘要与固定官方 schema/meta 哈希
npm ci --prefix Backend/tests/OpenAgent.Runtime.Acp.Interop/node
python3 -m venv .preview/acp-python
.preview/acp-python/bin/pip install -r Backend/tests/OpenAgent.Runtime.Acp.Interop/python/requirements.txt
cargo build --locked --manifest-path Backend/tests/OpenAgent.Runtime.Acp.Interop/rust/Cargo.toml --target-dir .preview/acp-rust
.preview/acp-python/bin/python Backend/tests/OpenAgent.Runtime.Acp.Interop/run.py --console Backend/tools/OpenAgent.Runtime.Acp.TestConsole/bin/Debug/net10.0/OpenAgent.Runtime.Acp.TestConsole.dll --rust .preview/acp-rust/debug/openagent-acp-interop
```

Windows 使用 venv 的 Scripts 路径与 `.exe`。三个官方 Client 调用 C# Agent，验证初始化、session/new、流式中文/表情、反向权限和取消确认；C# 控制台 interop 调用三个官方 Agent，验证初始化、会话、流式输出和原 prompt 的 cancelled 最终响应。Rust 夹具遵照官方有序分派约束，使用 connection.spawn 执行长 prompt。

## 发布与升级边界

- 包只允许一个 SDK DLL、文档与符号，不能含测试、控制台、schema 或运行时第三方依赖；检查脚本验证 nupkg 内容。
- 编译由 .NET SDK 的内置 SourceLink 生成源码链接；开启 RepositoryUrl/PublishRepositoryUrl，符号生成 snupkg。编译元数据不提交源码仓库。API 使用与边界以 USAGE/DESIGN 为准，当前 preview 未提供逐成员 XML 参考手册。
- schema 契约摘要和 SDK 固定来源只在显式升级时更新；更改 DTO 后补充合法/非法 wire 测试、已有 API 的兼容评审，以及公共控制台场景。普通 getter 的分配回归必须保留。
- 只保证当前文档中的 v1 范围。HTTP transport、v2 与 unstable 不在本包首版范围；TCK 是额外验证，未运行时必须明确记录。
- Preview 合入、发布稳定版、平台切流、生产部署和真实模型通过是不同事件；由对应证据验收，不自动连带。
