# ACP 单包 SDK 与测试控制台基线验证

日期：2026-10-16。环境：macOS、.NET SDK 8.0.422、net8.0，工作区本地未提交变更。范围为 ACP-00，不是完整 SDK 或真实模型验收。

## 变更与验证范围

- 单一可发布 SDK：`Backend/src/OpenAgent.Runtime.Acp`，当前提供 Stream/NDJSON 字节 framing、消息上限、串行完整写入、EOF 与流拥有权。
- 非发布自动化测试：`Backend/tests/OpenAgent.Runtime.Acp.Tests`，覆盖 14 个传输用例、1 个 SDK 依赖用例和 4 个真实控制台进程用例。
- 非发布测试控制台：`Backend/tools/OpenAgent.Runtime.Acp.TestConsole`，当前 list、run framing、run all，调用 SDK 实现。
- 三个项目已加入 `Backend/OpenAgent.sln`；没有修改其他平台项目引用，也没有引用旧 Demo。

## 实际命令与结果

```sh
dotnet build Backend/OpenAgent.sln --nologo -v minimal
dotnet test Backend/OpenAgent.sln --no-build --nologo --logger 'trx;LogFilePrefix=acp-single-package'
dotnet run --no-build --project Backend/tools/OpenAgent.Runtime.Acp.TestConsole -- run all
dotnet pack Backend/src/OpenAgent.Runtime.Acp/OpenAgent.Runtime.Acp.csproj --no-build -c Debug -o /tmp/openagent-acp-package --nologo
```

| 项目/检查 | 通过 | 失败 | 跳过 |
|---|---:|---:|---:|
| ACP SDK 与控制台测试 | 19 | 0 | 0 |
| Architecture | 10 | 0 | 0 |
| Contracts | 58 | 0 | 0 |
| Core | 439 | 0 | 2 |
| Engine | 107 | 0 | 0 |
| Hosting | 111 | 0 | 0 |
| Infrastructure | 32 | 0 | 0 |
| Router | 124 | 0 | 0 |
| Runner | 36 | 0 | 22 |
| 主解决方案合计 | **936** | **0** | **24** |

主解构建 0 error；10 条 NU1902/NU1903 来自已有 Architecture.Tests 的 ImageSharp 传递依赖，本次未修改其版本。24 个跳过为真实 Runner/Bubblewrap 环境门控，不算通过或 ACP 互操作证据。旧 samples/acp-demo 不在主解，此处计数不包含其历史 34 个用例。

控制台输出：`PASS framing (SDK transport; no Agent or model invoked)`，退出码 0。其进程测试同时验证未知场景返回 2；成功不代表尚未注册的后续阶段通过。

本地生成 `OpenAgent.Runtime.Acp.0.1.0-dev.nupkg`，检查 zip/nuspec：只有 `lib/net8.0/OpenAgent.Runtime.Acp.dll` 一个 SDK 程序集，net8.0 依赖组为空，没有测试/控制台/平台程序集。未发布到任何包源；当前缺少包 README 等完整发布内容，列入 ACP-07 收口，不以此包作稳定版本。

## 验收边界

已验证 Unicode 跨字节分片、CRLF/多帧、半帧 EOF、按字节限制、并发完整帧、写前/写中取消、Dispose 幂等及 LeaveOpen、SDK 无平台依赖、控制台真实启动。

当前不含强类型完整协议、JSON-RPC 关联/分派、Client/Agent、session、权限/工具执行、Processes、代码生成或 AOT；这些按 ACP-01～ACP-07 同步扩展 SDK、自动化测试、测试控制台。本轮没有调用外部 Agent、模型、个人凭据或生产服务。
