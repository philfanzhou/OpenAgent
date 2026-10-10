# 功能 Issue 与动态进度报表

具体功能任务由 GitHub Issue 维护。Excel 和甘特图是根据当前 Issue 数据生成的报告，不提交生成结果到仓库，不从 Excel 回写进度。

## Issue 格式

使用 [功能任务表单](../../.github/ISSUE_TEMPLATE/function-task.yml)，标题直接填写功能名称，正文保留以下字段：

| 字段 | 约定 |
|---|---|
| 序号 | 首批固定为 1～68；后续可留空，以 Issue 编号作为报表排序标识，不因排序重编号 |
| 任务模块 | 业务功能归属，如异常处理、会话压缩、授权管理 |
| 优先级 | P0、P1、P2；按正文值排期，同名标签用于筛选 |
| 预计工时（人时） | 正数；只计本条增量开发、自测和修复，公共能力由所属任务计一次 |
| 任务目标 | 具体可使用的功能，不写空泛的验收或收口目标 |
| 验收标准 | 可观察、可验证的结果，覆盖该功能的正常和关键失败行为 |
| 前序任务 | 写真实 `#Issue` 引用，并在 GitHub **Blocked by** 中建立依赖；排期以原生依赖为准，正文仅作阅读参考 |

负责人使用 GitHub Assignees，认领不受预先分派限制。Issue 地址和编号直接读取 GitHub。状态不写进正文，以关闭状态及标签确定：

| GitHub 状态 | 报表状态 |
|---|---|
| Open、无负责人、无状态标签 | 待认领 |
| Open、已分配、无状态标签 | 待开发 |
| `status:in-progress` | 进行中 |
| `status:review` | 待评审 |
| `status:blocked` | 阻塞 |
| Closed / completed | 已完成 |
| Closed / not planned 等其它原因 | 已取消；不会解锁依赖它的任务 |

状态标签只保留一个；关闭状态优先。只分配负责人不推断已开工，缺少数据不推测实际耗时或百分比。

## 首批任务

[首次创建输入](functional-task-seed.json)包含全部 68 条功能定义，共 654 人时；[Issue 对照索引](functional-issues.json)只保存固定序号与真实 Issue 地址。输入是创建归档，创建后工时、目标、标准和状态以 Issue 为准；工具恢复不会覆盖人工修改。

首版候选范围使用 `production-first` 标签，首次为 47 条、404 人时，包含必要前序。其余能力为后续扩展。范围调整通过 Issue 标签和原生依赖进行；未完成前序也必须进入排期。

创建工具默认预览，显式执行才写 GitHub；批次标记用于恢复，重跑复用已建 Issue，补齐缺失依赖，不重复创建：

运行需安装 GitHub CLI 并通过 `gh auth login` 登录；创建需要 Issue 写权限，报表只需要读取权限。

```bash
python3 scripts/project_progress.py create
python3 scripts/project_progress.py create --apply
```

## 动态排期与报告

[排期配置](project-schedule.json)当前采用 **1 名开发、每周 5 个有效人日、每天 8 人时**，开始日为 **2026-10-12**。这是容量模型，不是负责人分派。未提供节假日，默认只排除周末；有假期时填写 `holidays`。不额外累加集中联调缓冲。

```bash
python3 scripts/project_progress.py report --out outputs/project-progress
```

每次实时读取全部 Issue，包括已关闭任务和原生依赖，生成：

- `progress.json`：当前事实、计算依据和预测起止时间。
- `progress.csv`：原管制表的 11 个字段，可直接用 Excel 打开。
- `gantt.html`：按模块、优先级、状态筛选，点击任务可打开 Issue。
- `gantt.svg`：用于汇报的甘特图。

排期先满足前序，再按首版范围、进行中状态、优先级和固定序号选择任务；并行任务不超过配置人数，同一负责人不会同时执行多项。已完成任务从剩余工时中剔除；未完成项使用全额估算。取消、阻塞、缺失或未估算的前序不视为已完成，无法排期时显示原因，不给出完整阶段的虚假完成日期。

未按功能格式填写的旧总议题保留在“未纳入排期”列表，不能用零工时掩盖缺失估算。循环依赖、缺失必填字段或 API 失败会使生成失败。`--as-of` 只设置排期基准日，不重建过去的 Issue 状态；过去的配置开始日自动前移到该基准日。

## 按需生成 Excel

汇报时先刷新 `progress.json`，再导出，保留 11 列及数据抓取时间：

```bash
node scripts/export-progress-workbook.mjs --input outputs/project-progress/progress.json --output outputs/project-progress/progress.xlsx
```

导出需要运行环境提供 `@oai/artifact-tool`。Codex 使用其工作区依赖运行时；其它环境可用 `OPENAGENT_ARTIFACT_RUNTIME` 指向含 `node_modules` 的运行时目录。仓库不保存个人运行时路径；基础报表命令只依赖 Python 标准库和 GitHub CLI，Excel 在具备电子表格运行时的汇报环境按需导出。

## 更新与验证

汇报前或 Issue 状态、内容、标签、负责人、依赖变动后，重新执行 `report` 命令读取最新数据；需要工作簿时再执行导出命令。当前采用按需生成，不设置自动刷新工作流；所有生成结果留在忽略的 `outputs/` 中。

```bash
python3 scripts/test_project_progress.py
```

GitHub 格式及原生依赖规则参考 [Issue Forms](https://docs.github.com/en/communities/using-templates-to-encourage-useful-issues-and-pull-requests/syntax-for-issue-forms) 与 [Issue dependencies](https://docs.github.com/en/issues/tracking-your-work-with-issues/using-issues/creating-issue-dependencies)。
