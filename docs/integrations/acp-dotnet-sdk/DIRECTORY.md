# 工程与目录

全部工程加入 `Backend/OpenAgent.sln`，共用 `Directory.Build.props`、`Directory.Packages.props` 和根 `global.json`（.NET 10）。

| 路径 | 内容与发布约束 |
|---|---|
| `Backend/src/OpenAgent.Runtime.Acp/` | 唯一 SDK；IsPackable=true，PackageId/AssemblyName/RootNamespace 为 OpenAgent.Runtime.Acp |
| `Backend/tests/OpenAgent.Runtime.Acp.Tests/` | xUnit，IsPackable=false；引用 SDK/控制台，验证公共 API 与真实控制台子进程 |
| `Backend/tools/OpenAgent.Runtime.Acp.TestConsole/` | Exe，IsPackable=false，只引用 SDK 公共 API |
| `Backend/tools/OpenAgent.Runtime.Acp.CodeGen/` | 非发布生成工具；Program.cs 生成 DTO/context/catalog/schema，verify.py 做两次字节比较与篡改拒绝检查 |
| `docs/integrations/acp-dotnet-sdk/` | 正式规格、schema/v1 原件及 lock/许可、fixtures/contracts.json 固定契约输入；源码目录不散放 README |

SDK 内按需创建 Protocol/{Generated,JsonRpc,Serialization,Validation}、Transport、Rpc、Client/{Connections,Sessions,Handlers}、Agent/{Hosting,Sessions,Handlers,Context}、Processes、Diagnostics、Extensions；职责见 [DESIGN](./DESIGN.md)。不预建空项目，不将模块拆成发布包。

测试按 Protocol/Transport/Rpc/Client/Agent/Processes/Console/Architecture/Interop/Load 归域，夹具和替身集中 Fixtures/TestDoubles。控制台随能力拆 Scenarios、Agents、Reporting；不得复制 framing/RPC 循环。SDK 不引用其他 OpenAgent 平台模块，测试/控制台/生成器不进入消费者依赖。
