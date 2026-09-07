# 企业智能 Agent 业务场景底层能力与 OpenAgent 支持情况

> 更新日期：2026-09-04
> 参考资料：`企业智能Agent业务场景建设情况汇报(1).md`

## 1. 分析口径

本文从功能角度分析各业务 Case，不评价项目进度，也不把现成的业务资产误判为 OpenAgent 的缺失功能。

分析遵循以下原则：

- 已经形成的 Skill、MCP、模板和确定性处理程序，均视为可以迁移和复用的业务资产；
- 重点判断 OpenAgent 是否具备加载 Skill、调用 MCP、传递数据和文件、维护会话以及交付结果的底层能力；
- 业务系统继续作为权威数据来源，确定性计算、文件处理和专业规则继续由 MCP 或受控工具执行；
- OpenAgent 不需要内置每个业务系统的连接器，也不需要重新实现已有的专业处理程序；
- “支持”表示 OpenAgent 已有对应的运行能力，“部分支持”表示可以承载但存在平台限制，“缺失”表示当前代码无法完成对应环节。
- 按当前 Case 配置判断，数据获取、文件处理和业务计算类 MCP 均通过 HTTP 接口接入；只有手绘电路图最后的图片转换工具通过 stdio 运行。
- Skill 作为业务流程、指令和资源可以直接加载；需要由 Skill 调用的确定性程序，应通过已支持的 HTTP MCP 提供。

当前 OpenAgent 的基础执行关系为：

```text
用户请求或外部业务事件
→ OpenAgent 调用大模型理解请求
→ 加载专业 Skill
→ 大模型按照 Skill 调用一个或多个 MCP 工具
→ MCP 执行查询、计算或文件处理
→ OpenAgent 接收结构化结果或业务制品
→ 大模型组织最终结论
→ OpenAgent 保存并发布报告或文件
```

## 2. Case 1：DE 手绘电路图数字化

### 2.1 案例简介

工程师上传纸张或白板上的手绘电路图，由视觉模型读取图片并生成结构化的 Draw.io XML，再通过专业 MCP 将 XML 转换为可编辑的 `.drawio` 文件和 PNG 预览图。

该案例主要减少工程师使用 Visio 或 Draw.io 重新绘图、连线、对齐和排版的重复工作。最终结果是对原始手绘图的数字化转录，不替代工程师的电气设计判断。

### 2.2 系统内实现方式

```text
用户上传手绘电路图
→ OpenAgent 将图片发送给视觉模型
→ 模型输出结构化识别结果
→ Skill 规定识别、确认和生成步骤
→ 用户确认识别结果
→ Agent 调用 Circuit Draw.io MCP
→ MCP 生成 Draw.io XML 和 PNG
→ OpenAgent 保存并发布 .drawio 和 PNG
```

本案例不包含独立的制品版本管理功能。

### 2.3 底层能力与 OpenAgent 对比

| 所需底层能力 | 在案例中的作用 | OpenAgent 支持情况 | 已支持或缺失说明 |
|---|---|---|---|
| 图片上传与文件存储 | 接收用户上传的手绘图 | 支持 | 已有文件资产上传、对象存储和会话文件引用能力 |
| 多模态模型调用 | 将手绘图发送给具备视觉能力的模型 | 支持 | 已支持多模态模型配置及图片输入 |
| 结构化内容生成 | 由模型生成结构化识别结果和 Draw.io XML | 支持 | 模型可以生成 XML 等文本内容 |
| Skill 加载 | 使用现成 Skill 约束识别、确认和生成步骤 | 支持 | 可以安装、绑定并加载现成 Skill 的指令和资源 |
| Draw.io XML MCP | 根据确认结果生成结构化 Draw.io XML | 支持 | 该业务接口按 HTTP MCP 接入 |
| Draw.io 转 PNG MCP | 将 Draw.io XML 渲染为 PNG | 不支持 | 参考配置中的图片转换工具通过 stdio 运行，当前 OpenAgent 不能启动和调用 stdio MCP |
| 输入文件传递给 MCP | 将原始图片交给外部绘图工具 | 支持 | 可以为会话文件生成短期访问地址，供 MCP 读取 |
| Draw.io 文件接收与发布 | 将 MCP 或模型生成的 `.drawio` 文件交付给用户 | 支持 | 当前文件白名单包含 `.drawio` |
| PNG 文件接收与发布 | 保存和展示 MCP 生成的 PNG 预览图 | 支持 | 当前文件白名单包含 PNG 等图片类型 |
| 普通多轮确认 | 先展示识别结果，用户确认后再执行生成 | 支持 | 可以通过会话历史在下一轮继续处理 |
| 正式暂停与恢复 | 在一次执行中暂停，等待审批后从原执行点恢复 | 缺失 | 当前聊天协议不能完整承载 MAF 审批请求和原执行状态恢复 |

### 2.4 判断

OpenAgent 已具备图片输入、视觉模型调用、Skill 加载、HTTP MCP 调用、Draw.io 文件交付和普通多轮确认能力。Draw.io XML 生成接口可以接入，但图片转换接口使用 stdio，当前不能直接运行。

当前直接相关的平台缺口是图片转换 stdio MCP 和正式的暂停、审批、恢复机制。如果将图片转换工具封装成 HTTP MCP，手绘图案例的文件生成链路即可完整接入。

## 3. Case 2：单 Lot 质量调查与失效分析报告

### 3.1 案例简介

QA 工程师输入 Lot、PN、失效 ATE 参数或调查问题，系统从 OneData、QIMS、Shipment、RAMS、PCN、OnHold 等多个业务系统获取证据，完成确定性统计计算后，由模型汇总结论并生成统一 HTML 报告。

该案例主要解决跨系统查数、人工整理 Excel、绘制 Histogram、计算质量指标和编写调查 Summary 的重复工作。

### 3.2 系统内实现方式

```text
用户输入 Lot 和调查范围
→ 模型提取 Lot、PN、Test Item 等参数
→ lot-data-checking Skill 建立调查计划
→ Agent 依次调用多个业务 MCP
→ MCP 返回结构化证据和确定性计算结果
→ Agent 汇总所有检查项状态
→ 模型基于可信证据整理结论
→ 报告工具使用现成模板生成 HTML
→ OpenAgent 发布 HTML 报告
→ QA 工程师确认关键结论
```

### 3.3 底层能力与 OpenAgent 对比

| 所需底层能力 | 在案例中的作用 | OpenAgent 支持情况 | 已支持或缺失说明 |
|---|---|---|---|
| 自然语言理解与参数提取 | 从用户请求中取得 Lot、PN、参数和调查范围 | 支持 | 由当前 Agent 模型运行时完成 |
| 主 Skill 与子 Skill 加载 | 固化调查步骤、业务规则和 MCP 调用要求 | 支持 | 可以安装、选择和加载主 Skill 及专业子 Skill |
| 多 MCP Server 绑定 | 同时接入多个业务系统和分析工具 | 支持 | 一个 Agent 可以配置多个 MCP Server |
| MCP 工具发现与调用 | 调用现成 OneData、QIMS、Shipment 等 MCP | 支持 | 参考配置中的数据获取接口为 HTTP MCP，OpenAgent 可以发现并调用 |
| 多轮工具执行 | 根据前一个 MCP 结果继续调用后续 MCP | 支持 | 已有 Function Calling 工具循环 |
| 确定性计算工具调用 | 调用 MCP 内的良率、PPM、Histogram 等程序 | 支持 | 计算由 MCP 执行，OpenAgent 负责传参与接收结果 |
| 结构化证据汇总 | 将多个 MCP 的结果提供给模型统一总结 | 支持 | 工具结果会回填到 Agent 上下文 |
| 部分失败处理 | 某一数据源失败时保留其他检查结果 | 部分支持 | 单个 MCP Server 创建失败会被隔离，但完整业务检查状态仍由 Skill 和 MCP 结果定义 |
| 多 MCP 并行执行 | 并行查询互不依赖的数据源 | 缺失 | 当前工具调用主要按顺序执行 |
| 大结果集处理 | 处理多个系统返回的大量明细数据 | 部分支持 | 受模型上下文窗口和工具结果长度影响，应由 MCP 先聚合后返回 |
| HTML 内容生成 | 生成调查报告正文 | 支持 | 支持 HTML 文本文件生成 |
| HTML 文件发布 | 将报告绑定到会话并交付用户 | 支持 | 文件资产支持 `.html` 上传、保存和发布 |
| 普通人工确认 | QA 对最终关键结论进行确认 | 支持 | 可以通过后续对话完成确认 |

### 3.4 判断

在现成 Skill、MCP 和 HTML 模板可复用的前提下，OpenAgent 已覆盖该案例的主要底层能力。业务计算继续由 HTTP MCP 执行，OpenAgent 不需要重新实现良率、PPM 或 Histogram 算法。

当前限制主要是多 MCP 串行调用和大结果集上下文压力。这些限制影响性能和数据传递方式，但不构成核心业务功能缺失。

## 4. Case 3：QA 月度质量报告生成

### 4.1 案例简介

用户指定目标月份，系统通过 MCP 获取 Shipment、RPPM、DPPM 和 FA 数据，形成 Excel 与 PowerPoint 共用的数据快照，通过确定性工具填充现有 XLSX/PPTX 模板并进行回读校验，最终交付月报文件。

该案例的重点是复用既有模板、Sheet、页面、图表、Logo 和企业视觉规范，而不是由模型重新设计报告。

### 4.2 系统内实现方式

```text
用户指定月份和报告参数
→ 模型提取目标月份
→ qa-monthly-report Skill 规定数据范围和输出要求
→ Agent 调用 Shipment/PPM/QIMS MCP
→ 确定性程序形成统一数据快照
→ Office 工具使用现成模板生成 XLSX 和 PPTX
→ 工具回读并校验两个文件
→ OpenAgent 接收、保存并发布月报制品
→ QA 负责人确认报告
```

### 4.3 底层能力与 OpenAgent 对比

| 所需底层能力 | 在案例中的作用 | OpenAgent 支持情况 | 已支持或缺失说明 |
|---|---|---|---|
| 参数提取 | 获取目标月份和报告参数 | 支持 | 由模型完成自然语言理解和参数提取 |
| Skill 与模板资源加载 | 加载现成月报流程、字段映射和模板资源 | 支持 | Skill 指令、资源和现成模板可以加载或交给外部处理工具 |
| 多 MCP 数据获取 | 调用 Shipment、PPM、QIMS 等业务工具 | 支持 | 参考配置中的数据接口为 HTTP MCP |
| 确定性数据快照 | 形成 Excel/PPT 共用的规范化数据 | 支持 | 由现成 MCP 或确定性工具执行，OpenAgent 负责调用 |
| 调用现成 Office MCP | 将模板、数据快照和生成要求交给外部工具 | 支持 | Office 处理接口按 HTTP MCP 接入 |
| XLSX 执行能力 | 读取工作簿、填充模板、更新公式和图表并生成 XLSX | 缺失 | OpenAgent 当前没有可执行 XLSX 处理环境；Skill 内脚本也被禁用，只能委托外部 MCP 执行 |
| PPTX 执行能力 | 读取演示文稿、填充页面和图表并生成 PPTX | 缺失 | OpenAgent 当前没有可执行 PPTX 处理环境；只能委托外部 MCP 执行 |
| Office 文件回读校验能力 | 验证文件结构、字段、图表和跨文件一致性 | 缺失 | OpenAgent 自身不能执行 Office 回读校验，需由现成 Office MCP 返回结构化校验结果 |
| 双 Office 平台验证 | 在不同 Office 产品中验证兼容性 | 缺失 | OpenAgent 自身没有 Office 运行环境，需调用外部验证工具或 MCP |
| Office 制品交付通道 | 保存并向用户交付生成的 XLSX 和 PPTX | 部分支持 | 已有通用文件传输和发布框架，但当前 Office 媒体类型仍需接入；这是执行结果交付问题，不等同于 Office 执行能力 |
| 人工确认 | QA 负责人确认报告内容和发布范围 | 支持 | 可以通过多轮会话完成普通确认 |

### 4.4 判断

OpenAgent 已具备模型、Skill 指令、HTTP MCP 和 Agent 编排能力，现成模板可以继续复用，不属于平台缺失。QA 月报的 Office 生成仍属于 OpenAgent 自身缺少的 Office 执行能力，但现成 Office MCP 可以作为外部执行方接入。

当前明确缺失的是 XLSX/PPTX 的实际执行能力，包括模板读取、内容填充、图表更新、文件生成和回读校验。OpenAgent 可以调用现成 Office MCP 完成这些操作，但这些操作发生在外部 MCP 中，不代表 OpenAgent 自身已经具备 Office 执行能力。

此外，生成后的 Office 制品进入 OpenAgent 会话时仍需补充对应媒体类型的交付通道；该问题属于执行结果传递，不是本案例的核心执行能力定义。

## 5. Case 4：DES 设计文件自动校验与结果追踪

### 5.1 案例简介

用户提供项目编号，系统根据项目阶段对 Excel、Word、PowerPoint、PDF、邮件和图片等项目文件执行检查，生成统一 HTML 报告，并支持历史结果查询和人工复核。

文件读取、确定性规则检查和业务结果持久化分别由现成 PTS MCP、DES MCP 和 DES Validation Results MCP 完成。

### 5.2 系统内实现方式

```text
用户输入项目编号和操作类型
→ des-document-validation Skill 选择处理流程
→ Agent 调用 PTS MCP 获取项目上下文
→ Agent 调用 DES MCP 执行文件检查
→ 模型仅处理 MCP 指定的语义判断任务
→ Agent 调用 Validation Results MCP 保存结果
→ 生成并发布 HTML 报告
→ 用户提交人工复核意见
→ Agent 调用 MCP 追加复核记录
```

### 5.3 底层能力与 OpenAgent 对比

| 所需底层能力 | 在案例中的作用 | OpenAgent 支持情况 | 已支持或缺失说明 |
|---|---|---|---|
| Skill 条件流程 | 区分新校验、历史查询和人工复核 | 支持 | 可以加载现成 DES Skill，并根据指令选择工具 |
| 多 MCP 协作 | 分别获取项目、执行校验和保存结果 | 支持 | 参考配置中的 PTS、DES 和 Results 接口为 HTTP MCP |
| 业务文件处理 | 读取 Excel、Word、PPT、PDF、邮件和图片 | 支持 | 文件处理在 DES MCP 内完成，不要求 OpenAgent 直接解析 Office 文件 |
| 确定性规则执行 | 检查模板、字段、修订号、PN 和一致性 | 支持 | 规则由现成 DES MCP 执行 |
| 模型语义判断 | 处理 MCP 明确返回的语义判断任务 | 支持 | 工具结果可以回填给模型继续分析 |
| 结果持久化 | 保存检查明细、状态和证据 | 支持 | 由现成 Validation Results MCP 管理，OpenAgent 负责调用 |
| 历史查询 | 按项目、阶段、规则和日期查询结果 | 支持 | 由 Results MCP 提供查询工具 |
| HTML 报告生成与发布 | 交付统一检查报告 | 支持 | 支持 HTML 文件生成和发布 |
| 普通人工复核 | 用户在后续会话中提交复核结论 | 支持 | 可在下一轮调用 Results MCP 追加复核意见 |
| 正式审批暂停与恢复 | 在执行中暂停并恢复原任务 | 缺失 | 当前仅适合以多轮对话重新进入复核步骤 |
| 长任务和业务步骤状态 | 跟踪多个检查阶段的终态 | 部分支持 | OpenAgent 有会话状态，但业务任务状态仍应由 Results MCP 保存 |

### 5.4 判断

OpenAgent 已具备承载 DES Case 的主要底层能力。Office 文件读取、规则校验和业务历史均由现成 HTTP MCP 完成，因此不属于 OpenAgent 的缺失功能。

平台限制主要是没有正式审批暂停/恢复机制，以及没有独立的通用业务任务状态机。按照参考方案由 Validation Results MCP 保存业务状态，可以避免后一个限制阻塞实施。

## 6. Case 5：FA EMMI Lights

### 6.1 案例简介

该案例面向 FA 过程中的 EMMI Lights 相关数据或图像处理。当前提供的参考资料没有定义实际输入、处理步骤、调用工具和输出形式，因此本文不推测具体识别、检测、标注或判定流程。

### 6.2 当前可确认的系统实现范围

```text
用户提交 EMMI 相关请求或文件
→ OpenAgent 调用模型理解请求
→ 加载现成 EMMI Skill
→ 调用现成 EMMI MCP 或专业工具
→ 接收结构化结果或业务文件
→ 展示或发布结果
```

以上链路仅表示 OpenAgent 可以提供的通用承载方式，不代表 EMMI Case 已经确定采用该流程。

### 6.3 底层能力与 OpenAgent 对比

| 所需底层能力 | OpenAgent 支持情况 | 已支持或缺失说明 |
|---|---|---|
| 自然语言请求理解 | 支持 | 已有模型运行时 |
| 图片输入 | 支持 | 如果 Case 使用图像，OpenAgent 可以将图片发送给多模态模型 |
| Skill 加载 | 支持 | 可以加载现成 EMMI Skill 指令和资源 |
| MCP 工具调用 | 支持 | 按数据和业务接口使用 HTTP MCP；具体接口仍需以 Case 配置为准 |
| 结果文件接收与发布 | 部分支持 | 取决于输出文件类型是否在当前白名单内 |
| 多轮人工确认 | 支持 | 可以通过后续会话完成普通确认 |
| 其他底层能力 | 无法判断 | 需要补充实际输入、输出、MCP 合同和处理流程 |

### 6.4 判断

OpenAgent 已具备模型、图片、Skill 指令、HTTP MCP 和文件的通用承载底座。由于参考资料没有提供 EMMI Case 的详细配置，暂不能判断是否存在图片转换类 stdio 工具或其他专用执行要求。

## 7. Case 6：FA Data Seek

### 7.1 案例简介

该案例面向 FA 数据查询、关联和结果汇总。当前参考资料没有说明实际数据源、输入参数、查询步骤、关联规则和交付形式，因此本文不推测具体业务实现。

### 7.2 当前可确认的系统实现范围

```text
用户输入 FA 数据查询要求
→ OpenAgent 提取查询参数
→ 加载现成 Data Seek Skill
→ 调用一个或多个数据 MCP，或使用 RAG 检索
→ 汇总结构化结果
→ 返回结论或发布结果文件
```

以上链路仅表示可采用的 OpenAgent 通用实现方式，不代表 Case 已经确认使用全部组件。

### 7.3 底层能力与 OpenAgent 对比

| 所需底层能力 | OpenAgent 支持情况 | 已支持或缺失说明 |
|---|---|---|
| 自然语言参数提取 | 支持 | 可以把用户请求转换为 MCP 工具参数 |
| Skill 加载 | 支持 | 可以加载现成 Data Seek Skill 指令和资源 |
| 多数据源 MCP | 支持 | 数据获取接口按 HTTP MCP 接入，一个 Agent 可以绑定多个 MCP Server |
| RAG 检索 | 支持 | 已有 RAG 注册、授权和工具调用能力 |
| 结果汇总 | 支持 | 多个工具结果可以回填给模型总结 |
| 文件结果发布 | 部分支持 | 取决于最终文件类型是否在当前白名单内 |
| 并行数据查询 | 缺失 | 当前工具执行以串行为主 |
| 大结果集处理 | 部分支持 | 需要 MCP 先过滤、分页或聚合，避免大量原始数据进入模型上下文 |
| 其他底层能力 | 无法判断 | 需要补充实际 Case 配置和工具合同 |

### 7.4 判断

OpenAgent 已具备 Data Seek 可能需要的 Skill 指令、HTTP MCP、RAG 和结果汇总能力。当前无法确认的部分来自 Case 定义不足。

## 8. Case 7：邮件附件 XLSX 自动合并

### 8.1 案例简介

用户将待处理邮件转发到指定业务邮箱，Mail 服务根据请求范围固定用户身份和邮件线程。Agent 理解月份、主题、附件、Sheet 和字段要求，调用 Mail XLSX Workflow MCP 完成邮件检索、Workbook 预检、数据合并、结果验证和原线程回复。

该案例中，邮箱凭证、邮件线程和附件二进制继续由 Mail 服务管理；大模型只负责理解要求和生成结构化规格，MCP 负责确定性文件处理。

### 8.2 系统内实现方式

```text
Mail 服务接收用户邮件
→ 创建可信 requestId 并固定请求身份和邮件线程
→ Mail 服务使用服务凭据调用 OpenAgent
→ 模型生成 SearchSpec 和 MergeSpec
→ Skill 判断是否需要补充澄清
→ Agent 调用 Mail XLSX Workflow MCP
→ MCP 检索邮件、预检 Workbook 并生成 validatedPlanId
→ MCP 执行合并和结果校验
→ MCP 或 Mail 服务回复原邮件线程
→ OpenAgent 保存会话和执行记录
```

### 8.3 底层能力与 OpenAgent 对比

| 所需底层能力 | 在案例中的作用 | OpenAgent 支持情况 | 已支持或缺失说明 |
|---|---|---|---|
| 外部服务调用 Agent | Mail 服务触发 OpenAgent 执行 | 支持 | 已有 HTTP API 和第三方 Bearer API Key 认证 |
| 服务身份与租户上下文 | 建立可信调用方身份 | 支持 | API Key 可映射用户、租户和权限上下文 |
| 自然语言转结构化规格 | 生成 SearchSpec 和 MergeSpec | 支持 | 模型可以依据 Skill 生成 MCP 工具参数 |
| Skill 加载 | 使用现成 XLSX 合并规则和澄清流程 | 支持 | 可以加载现成 Skill 指令和资源 |
| 多轮澄清 | 合并规则不完整时向用户询问 | 部分支持 | OpenAgent 支持会话，但 Mail 服务需要把后续邮件关联回同一 conversationId |
| Mail XLSX Workflow MCP 调用 | 执行邮件检索、预检、合并和回信 | 支持 | 邮件和数据处理接口按 HTTP MCP 接入 |
| 业务任务状态和幂等 | 保存 requestId、计划、租约和执行状态 | 支持 | 由现成 Mail 服务和 MCP 负责，OpenAgent 只传递可信 requestId |
| XLSX 解析执行能力 | 读取 Workbook、Sheet、表头、范围和单元格数据 | 缺失 | OpenAgent 自身没有 XLSX 执行环境，只能调用现成 MCP 完成 |
| XLSX 合并执行能力 | 按 validatedPlanId 执行字段映射、合并和完整性检查 | 缺失 | OpenAgent 只负责编排，确定性 XLSX 操作由 Mail XLSX Workflow MCP 执行 |
| XLSX 结果交付 | 将合并结果发送到邮件或发布到会话 | 部分支持 | 邮件交付可由 Mail 服务完成；如果需要进入 OpenAgent 文件资产，还需接入 Office 媒体类型 |
| 邮件线程与会话关联 | 将用户回复继续路由到原 Agent 会话 | 缺失 | 当前没有标准邮件 Channel 或 requestId-to-conversationId 关联合同 |
| 受控回信 | 只回复服务端固定的原邮件线程 | 支持 | 由现成 Mail 服务和 MCP 执行，不要求 OpenAgent 提供通用发信接口 |

### 8.4 判断

OpenAgent 可以承担语义理解、Skill 指令加载和 HTTP MCP 编排，Mail 服务和现成 MCP 继续负责邮件身份、附件、确定性合并及原线程回复。XLSX 解析和合并的实际执行仍由现成 MCP 完成，不属于 OpenAgent 自身能力。

OpenAgent 自身缺少 XLSX 解析、合并和结果校验的执行能力，但可以调用现成 Mail XLSX Workflow MCP 完成。该案例还需要 Mail `requestId` 与 OpenAgent `conversationId` 的标准关联方式。

如果合并文件由 Mail 服务和 MCP 管理，并由 Mail 服务直接回复原邮件线程，则不要求 OpenAgent 保存 XLSX；如果还要在 OpenAgent 会话中下载结果，则需要额外打通 Office 制品交付通道。

## 9. OpenAgent 已覆盖的共性底层能力

| 能力领域 | 当前支持内容 |
|---|---|
| 模型运行 | MAF Agent、流式/非流式执行、多模型 Provider、工具调用循环 |
| 多模态 | 图片和 PDF 输入、模型多模态配置 |
| Skill | ZIP/Markdown Skill 安装、对象存储、Agent 绑定、指令与资源加载 |
| MCP | HTTP MCP 的多 Server 配置、工具发现、工具调用、权限过滤和取消传播 |
| RAG | RAG 注册、授权和检索工具接入 |
| 会话 | PostgreSQL 会话和消息存储、上下文压缩、文件引用 |
| 文件 | 图片、PDF、JSON、CSV、Markdown、HTML、Draw.io 的上传、存储、下载和发布 |
| 外部文件交换 | 为文件生成短期读取地址，从 HTTP(S) 地址下载 MCP 生成结果 |
| 安全 | JWT、第三方 Bearer API Key、租户上下文和能力授权 |
| 可观测性 | 工具调用展示、日志、Trace、Metrics 和健康检查 |

## 10. 与上述 Case 直接相关的 OpenAgent 功能缺口

现成 Skill、MCP、模板和确定性程序不计入缺失后，OpenAgent 剩余的平台级缺口如下：

| 功能缺口 | 影响的 Case | 影响说明 |
|---|---|---|
| 图片转换 stdio MCP 执行能力 | DE 手绘电路图 | 图片转换工具通过 stdio 运行，当前 OpenAgent 不能启动和调用该 MCP |
| XLSX/PPTX Office 执行能力 | QA 月报、邮件 XLSX 合并 | OpenAgent 没有工作簿、演示文稿的读取、模板填充、生成和回读校验执行环境；当前只能委托外部 MCP |
| Office 制品交付通道 | QA 月报、邮件 XLSX 合并 | 外部 MCP 生成 Office 文件后，如需进入 OpenAgent 会话，还要支持对应媒体类型的接收和发布 |
| 正式 Human-in-the-loop 暂停与恢复 | 手绘电路图、DES、质量结论确认 | 普通多轮确认可用，但不能从一次暂停的执行中按审批令牌恢复 |
| 多 MCP 并行调用 | Lot 分析、Data Seek | 当前主要串行调用，影响多个独立数据源的查询效率 |
| 大工具结果治理 | Lot 分析、Data Seek、DES | 缺少通用工具结果缓存和大结果外置机制，需要 MCP 先聚合或分页 |
| 外部事件与会话关联合同 | 邮件 XLSX 合并 | Mail 服务需要维护 requestId 与 conversationId 的稳定映射 |
| 内网文件结果回收限制 | 需要 MCP 返回文件的 Case | 当前 URL 下载只允许安全的 HTTP(S) 地址并阻止内网/本机地址，企业内网 MCP 需使用受支持的文件交付方式 |
| Skill 脚本隔离执行 | 仅影响依赖 Skill 内脚本的资产 | 当前 Skill 脚本被禁用；业务程序通过 MCP 提供时不受影响 |

## 11. 总体结论

上述案例共同使用了模型、多模态、Skill、MCP、确定性工具、文件处理、人工交互和安全治理等底层能力。

现有 Skill、MCP、模板和业务程序能否直接迁移，主要取决于具体 Case 的外部接口。当前配置中的数据获取和业务处理 MCP 均为 HTTP，可以接入 OpenAgent；只有手绘电路图的图片转换 MCP 使用 stdio，当前不能直接运行。

在上述接口可用的前提下，OpenAgent 已具备承载这些案例的核心 Agent 运行能力。它不需要重新实现 Lot 统计、Draw.io XML 生成或 DES 文件检查等专业逻辑，可以通过受支持的 HTTP MCP 调用这些能力。

对于 XLSX 和 PPTX，应明确区分“能够调用外部 MCP”与“平台自身具备执行能力”：OpenAgent 当前可以编排外部 Office MCP，但自身缺少 Office 文件读取、模板填充、生成和回读校验的执行能力。

当前真正需要由 OpenAgent 补充的部分，主要集中在图片转换 stdio MCP、Office 执行能力、Office 制品交付、正式审批恢复、多 MCP 并行、大结果处理以及外部事件与会话关联，而不是业务 Skill、HTTP MCP 或模板内容本身。
