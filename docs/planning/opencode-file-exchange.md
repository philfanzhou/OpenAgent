# OpenCode 文件收发方案

方案日期：2026-10-29。用户已授权按序实施；完成范围与验证以各实现 PR 为准；任务状态只维护于 [TODO](TODO.md#opencode-文件收发闭环)。
设计基线：`origin/dev@7d13057bd478f123b8e2e820a2e4e03f1dfa885f`；实施工作区从该 dev 基线创建。

## 目标与边界

用户上传原文件后，OpenCode 可按路径读取、处理，并将明确选择的结果作为消息附件交付；工作区回收后仍可下载或重新导入处理。
复用 FileAsset、对象存储、会话/消息引用、publish_files 和前端附件组件；首版不新增数据库 schema，不恢复 Runner，不改独立 ACP SDK，不实现文件目录同步、自动压缩包解包或任意宿主路径访问。
本方案是可信服务身份下的文件交换能力，不代替 Worker/OS 隔离；原生 bash 获得的主机访问面仍受现有部署边界约束。

## 现状核对

| 环节 | 基线行为与缺口 |
|---|---|
| 用户上传 | 文件 API 写入对象存储和 FileAsset，聊天以 fileIds 关联；已具备 |
| 运行时输入 | RuntimePreparation 只将 DataContent 转成 RuntimeAttachment；目前主要是内联图片，可能降采样；缺原始通用附件物化 |
| 本地文件 | OpenCodeProcessKeeper 持有临时工作区；默认空闲 300 秒、最多 16 个空闲进程，失败/取消/淘汰回收；不能承担资产持久化 |
| 登记与发布 | write_file 可创建文本资产，publish_files 发布已有 fileId；缺本地产物导出入口 |
| 前端展示 | useChatStreaming 在 done 后重载会话，useFileHandling 加载附件预览、下载；首版可以复用 |

核对路径均相对设计基线：`Backend/src/OpenAgent.Runtime/OpenCode/{OpenCodeRuntime,OpenCodeProcessKeeper}.cs`、`Backend/src/OpenAgent.Application/{Execution/RuntimePreparation,Conversation/History/PlatformChatHistory}.cs`、`Backend/src/OpenAgent.Capabilities/Files/Tools/FileAssetCapabilitySource.cs`、`Frontend/OpenAgent.Chat/src/composables/{useChatStreaming,useFileHandling}.ts`。

## 端到端流程

1. 用户上传 → FileAsset Ready → 请求携带 fileIds；平台检查租户、用户、会话与数量/大小限制。
2. Runtime 获取工作区后、注册本轮 MCP 桥之前，平台准备原文件并写入 `inputs/<fileId>/<安全文件名>`；返回清单供模型按路径操作，图片视觉输入独立保留。
3. OpenCode 使用获准的原生工具读取 inputs，将最终产物写入 outputs；临时中间文件可留在其他工作目录。
4. 模型调用 `export_file(path)` → 校验并快照该文件 → UploadAsync → Ready → 关联会话 → 返回 fileId；导出完成即可摆脱工作区生命周期。
5. 模型调用现有 `publish_files(fileIds)` → 登记本轮发布集合 → 平台保存助手消息及文件引用 → done → 前端重载并显示附件。
6. 清理本轮文件交换租约，成功轮次归还保活进程；回收后追问可经 `list_files` + `import_file(fileId)` 从对象存储恢复所需输入。

## 模型工具与输入契约

| 契约 | 拟议输入与结果 |
|---|---|
| import_file | 输入 fileId；仅当前会话已引用且当前主体可访问的 Ready 资产；输出 fileId、fileName、mediaType、length、sha256、relativePath；复用未改变的本地副本 |
| export_file | 输入 `path`（必须为 outputs 下的文件相对路径）、可选 fileName；输出已登记资产的 fileId、fileName、mediaType、length、sha256；不自动发布 |
| publish_files | 保持现有 fileIds 入参与显式发布语义；登记成功不等于已交付，消息持久化成功后才是可恢复交付 |
| Runtime 输入 | 新增原始文件描述符，与 RuntimeAttachment 视觉字节分离；含 fileId、原文件名、MIME、原始长度、SHA-256；不在 prompt 中塞入二进制/base64 |

首版自动物化本轮显式上传的文件；历史文件按需导入，不每轮下载全部会话资产。模型清单给出稳定的 fileId→相对路径映射，同名文件按 fileId 分目录，重建进程后路径一致。
inputs 是输入副本，模型指令要求修改另存 outputs；现有原生 bash 下不承诺不可写。重新使用缓存前校验哈希，副本被改写时恢复原件；不回写对象存储原资产。
采用“先登记、后发布”便于检查后选择交付；模型工具说明明确提示 export 后需 publish，文本 write_file 与二进制 export_file 使用相同发布入口。

## 模块与依赖设计

| 主责 | 变更内容 |
|---|---|
| Contracts/Runtime | 原文件描述符、`IRuntimeWorkspaceFiles` 中性端口、RuntimeInvocation 的可选文件准备回调；只用平台类型与标准 Stream，不暴露 ACP/OpenCode/绝对路径 |
| Runtime/OpenCode | 实现端口：输入原子落盘、输出有界快照、相对路径与链接检查、租约失效；在 acquire 后绑定，在回收前关闭并排空文件操作 |
| Capabilities/Files | 复用 scoped 文件上下文，新增 import/export 能力定义及实现；文件授权、资产登记、会话引用、发布记录继续归文件业务 |
| Application/Execution | 为本轮构造文件准备回调；驱动提供端口后绑定文件上下文并物化输入，返回本轮可释放租约；沿用现有能力发现、逐调用授权、超时/并发/结果预算 |
| Application/Conversation | 成功与失败/取消消息都关联已明确 publish 的 Ready 文件；未 publish 的文件仅保留为会话资产 |
| Engine.Host、前端 | Host 组合配置；前端补充新工具展示名称并验证附件恢复；首版保持 HTTP/SSE wire 形态 |

回调只连接 Contracts 中性端口：Runtime 不引用 Capabilities/Application/Framework，Capabilities 不接触物理工作区或 ACP。存储凭据留在平台进程服务中，模型只能提供 fileId/相对路径，不能指定 Tenant/User/Conversation、对象键或绝对路径。
工具定义仍由平台能力工厂发现并通过现有 MCP 桥暴露；不在 Runtime 偷加绕开授权和工具预算的另一组工具。文件端口按轮次绑定，不把 scoped 服务、旧 bearer 或工具闭包保留进保活池。

## 一致性、失败与限制

- 输入使用临时文件加原子替换；校验来源资产及实际字节，写入完整后才出现在清单。本轮必需附件读取失败即明确失败，不静默遗漏后继续声称已读取。
- 导出只接收普通文件；逐级拒绝绝对路径、路径穿越、符号链接/reparse point、越界真实路径及设备/管道，检查根目录和文件句柄；先生成有界快照，上传的是稳定快照，不是持续变化的源文件。支持平台差异须以真实测试定界，应用层检查不宣称抵御恶意同身份进程的全部竞态。
- 复用 FileAssets 单文件上限；拟新增每轮输入/导出各 10 个、累计各 200 MiB、进程内同时交换最多 2 个的可配置限额。按实际字节计数，重复同内容导出不重复计额；超限返回稳定错误和可执行提示。限额只约束交换，不等于 OS 磁盘/内存配额。
- 当前存储读写存在整文件内存缓冲，首版复用并限制并发，须实测峰值；若不满足运行环境预算，在合入前降低限额或改流式读写，不能仅凭 Stream 参数宣称已经实现全链路流式。
- 同轮以规范路径+内容 SHA-256 缓存成功导出的 fileId；内容变化创建新资产。同轮明确重试复用结果；跨进程/响应丢失不承诺 exactly-once，首版允许残留未发布资产，不自动删除可能已引用文件。
- 区分资产 Ready、会话引用、消息发布三个事实。上传失败不返回成功 fileId；会话关联失败不缓存为成功；消息保存失败不发送成功 done、不确认 harness 可续接。已有失败消息保存路径也必须挂载已 publish 资产并标记整轮未完成。
- 取消/结束先禁用新文件操作和工具 bearer，取消并等待在途传输释放句柄，再释放文件租约和进程；排空超时走有界失败清理，记录清理失败，禁止将未排空环境归还池。
- 冷启动不依赖遗留目录；本轮附件自动恢复，历史资产按需导入。无成功导出保证的本地文件随工作区消失；资产保留/孤儿回收另列后续任务，不将增加保活时长当作持久化。
- 沿用 FileMediaTypeCatalog 的现行推断行为，未知扩展名按通用二进制处理；不恢复旧扩展名白名单。复用 HTML 沙箱预览与下载响应保护，导出文件名/路径不得拼入未经转义的模型指令。

## 实施拆分与验收

| 步骤 | 拥有范围与交付 | 核心验收 |
|---|---|---|
| FX01 原文件输入 | Contracts、Application/Execution、Capabilities/Files/Requests、Runtime/OpenCode；原文件描述符、准备租约、输入清单和 import_file | PDF/XLSX/大于内联阈值的图片原字节可读；非多模态模型同样可按文件处理；中文/同名文件；热复用、冷重建、历史导入、越权拒绝 |
| FX02 本地导出 | Capabilities/Files/Tools、Runtime 文件端口；export_file、有界快照、登记/引用、限额和同轮重试 | 原生 Python/Node 生成二进制，导出后 SHA-256 一致；路径/链接/特殊文件/并发变更拒绝或明确失败；超限、存储失败、取消可归因 |
| FX03 消息交付 | Application/Conversation、前端文件展示；复用 publish_files、失败/取消关联和前端重载 | 成功消息显示附件；刷新、重启、工作区淘汰后仍可下载；发布前失败不冒充已发布，发布后失败保留已选产物和未完成状态；保存失败不发成功 done |
| FX04 完整验证 | 对应 Tests、架构门禁、运行时/文件文档、部署配置 | 真实 OpenCode + 原生执行 + MinIO 往返；并发身份/保活/取消/内存测试；Linux 发布环境通过，Windows 支持单独实测 |

各步骤独立 PR、按 FX01→FX02→FX03→FX04 顺序；共享契约由 FX01 固定，后续以已合入契约适配。构建/测试使用实施基线要求的 SDK；Contracts 变化全量构建和架构测试，模块测试随拥有范围执行。
最终业务用例：上传 `销售表.xlsx` → OpenCode 读取原文件 → outputs 生成分析表与 PNG → export → publish → 用户预览/下载；销毁保活工作区并重启后仍可下载，再次追问可重新导入继续修改。
解析器和字体/依赖是否预置须在环境验证中列明；能传送 DOCX/XLSX 不等于已安装相应解析库。首版完成标准是上述业务闭环及失败用例通过，不以单测模拟存储或模拟 harness 代替真实链路。

## 启用与回退

新增文件交换开关与限额配置，默认关闭，验证环境显式启用；FileAssets 未启用时不暴露 import/export，不产生虚假的可读路径。启用依赖对象存储可用、宿主已授权必要原生工具和目标执行依赖齐备。
回退时关闭交换入口并排空在途操作，保留已有 FileAsset、消息引用及下载能力；不删除用户文件、不回滚 schema。后续可另行评估目录/多文件依赖打包、孤儿资产回收和全链路流式传输。
