# PR-2：Bubblewrap 沙箱参数单一事实源（bwrap-args-single-builder）

- 分支：`refactor/bwrap-args-single-builder`（基于 `main@9b694ab`）
- 改动文件：`Backend/src/OpenAgent.Runner/BubblewrapCodeExecutor.cs`、`Backend/tests/OpenAgent.Runner.Tests/BubblewrapExecutionTests.cs`
- 性质：纯重构，**不改变任何执行行为、超时、限额数值与参数序列**

## 1. 动机：安全加固参数双写风险

重构前，`BubblewrapCodeExecutor` 中两个参数构造器维护着两份**逐字相同**的安全加固段（行号为重构前文件）：

- `BuildArguments`（:161-229）：一次性（ephemeral）沙箱；
- `BuildSessionArguments`（:236-296）：持久会话（session）沙箱。

两者约 60 行安全参数逐字重复：

| 重复段 | 内容 |
|--------|------|
| Isolation | `--unshare-user/ipc/pid/net/uts/cgroup-try`、`--disable-userns`、`--new-session`、`--die-with-parent`、`--uid/--gid 65532`、`--hostname openagent-sandbox`、`--clearenv` |
| RootFilesystem | `/usr` 只读绑定、`bin/lib/lib64/sbin` symlink、`/etc/fonts|libreoffice|ld.so.cache|localtime` ro-bind-try、`passwd/group/hosts/nsswitch.conf` 合成文件绑定 |
| 沙箱脚本 | `--ro-bind <sandboxFilesDirectory> /sandbox` |
| Scratch tmpfs | `/work`（WorkspaceMiB）、`/output`（32MiB）、`/tmp`（64MiB） |
| System mounts | `/run` tmpfs、`/var`、`/var/tmp` symlink、`/home`、`/proc`、`/dev`、`--chdir /work` |
| 基础环境 | `PATH` + 12 个 `--setenv`（HOME/TMPDIR/XDG_RUNTIME_DIR/LANG/PYTHONDONTWRITEBYTECODE/MPL*/SAL_*/OMP*/OPENBLAS*/MKL*/NUMEXPR*） |
| 收尾 | `AddRuntimeBinds` + `--remount-ro /` |

两者的实际差异只有 4 处：

1. 一次性 `--ro-bind inputDirectory /input`（只读）vs 会话 `--bind channelDirectory /channel`（读写控制通道）；
2. 会话版在 `/tmp` 与 `/run` 之间多一个 `--size InputMiB --tmpfs /input`（持久输入 tmpfs）；
3. 尾部环境变量：`EXECUTION_*`（仅一次性）vs `SANDBOX_*`（仅会话）；
4. 入口：`-- /usr/bin/prlimit … /sandbox/execute.py`（一次性）vs `-- python -I /sandbox/supervisor.py`（会话）。

**风险**：namespace/uid/bind 等 fail-closed 加固参数改动必须双写，漏改其中一边即产生沙箱逃逸面（例如只在一个变体上补 `--unshare-net` 或调整 uid），且 review 时难以发现两边漂移。

## 2. 重构方案

将逐字相同的安全段收敛为同文件的 `private static` 构造方法（可见性遵循 private > internal > public），两个构造器改为**只由共享段拼装**，各自的差异项（变体挂载、专属环境变量、入口）内联保留：

```
BuildArguments（一次性）                BuildSessionArguments（会话）
├─ AddIsolation()                      ├─ AddIsolation()                 ← 单一事实源
├─ AddRootFilesystem(sandboxFiles)     ├─ AddRootFilesystem(sandboxFiles)← 单一事实源
├─ --ro-bind inputDir /input    ★差异① ├─ --bind channelDir /channel     ★差异①
├─ AddSandboxFiles(sandboxFiles)       ├─ AddSandboxFiles(sandboxFiles)  ← 单一事实源
├─ AddScratchMounts(settings)          ├─ AddScratchMounts(settings)     ← 单一事实源
│                                      ├─ --size InputMiB --tmpfs /input ★差异②（顺序：/tmp 之后、/run 之前）
├─ AddSystemMounts()                   ├─ AddSystemMounts()              ← 单一事实源
├─ AddBaseEnvironment(RuntimePath)     ├─ AddBaseEnvironment(RuntimePath)← 单一事实源
├─ EXECUTION_*（4 个 setenv）   ★差异③ ├─ SANDBOX_*（7 个 setenv）       ★差异③
├─ SealRoot(pythonRoot, nodeRoot)      ├─ SealRoot(pythonRoot, nodeRoot) ← 单一事实源（含 --remount-ro /）
└─ -- prlimit … execute.py     ★差异④ └─ -- python -I supervisor.py     ★差异④
```

- `SealRoot` 把 `AddRuntimeBinds`（原有共享方法）与 `--remount-ro /` 绑定为一个方法，保证"先全部 bind、后封根"的顺序约束只在一处表达（原注释原样保留在其内部）。
- `BuildProbeArguments` 保持独立：探测是可用性健康检查的最小子集（isolation 子集且无 `--hostname`、最小 /usr 绑定），复用完整 isolation 段会改变其输出序列，违背行为等价硬约束；已加 doc comment 说明其刻意独立。
- 重构后文件中每个安全加固段（unshare/uid/gid/hostname/clearenv、/usr 与 etc 绑定、tmpfs、setenv、remount-ro）**只出现一次**。

## 3. 等价性证明

### 3.1 参数序列逐项比对（机械验证）

用一个临时测试夹具（已删除，未提交）分别在重构前后把三个方法的完整参数列表导出为文本并 diff：

- `BuildArguments`（167 项）、`BuildSessionArguments`（174 项）、`BuildProbeArguments`（35 项）
- 结果：`diff` 三组输出**完全一致（0 差异）**，即重构前后参数序列逐项不变。

### 3.2 构建与测试

- `dotnet build Backend/OpenAgent.sln`：**0 警告 / 0 错误**
- `dotnet test Backend/OpenAgent.sln`：**641 通过 / 0 失败 / 22 跳过**（基线 638/0/22，新增 3 条测试；Linux 沙箱集成测试在 Windows 跳过属预期）
  - Runner：34 通过 / 20 跳过（基线 31/20，+3）
  - 其余项目与基线一致：Contracts 15、Architecture 6、Engine 102、Infrastructure 16、Core 285（+2 跳过）、Router 124、Hosting 59

现有测试中直接约束参数序列的断言全部保持通过，例如：

- `BuildArguments_UsesFailClosedNamespacesAndOnlyExplicitMounts`
- `BuildSessionArguments_KeepsHardeningAndAddsChannelWithPersistentInput`（断言 `["--size","67108864","--perms","1777","--tmpfs","/input"]` 与 `["--bind",…,"/channel"]` 的序列与位置）

### 3.3 新增守卫测试（BubblewrapExecutionTests.cs）

1. `BuildArguments_SessionAndEphemeral_ShareHardeningPrefix`：计算两个构造器输出的最长公共前缀，断言公共前缀完整覆盖 isolation + root filesystem 段（直到 `/etc/nsswitch.conf`），且两序列**恰好**在各自的变体挂载处分叉（一次性 `--ro-bind … /input`，会话 `--bind … /channel`）。今后任何人只改一边的加固前缀都会被此测试击落。
2. `BuildArguments_EachVariant_RemountsRootReadOnlyAfterAllMounts`（Theory，ephemeral/session 两行）：断言 `--remount-ro /` 存在，且所有挂载类参数（`--ro-bind/--ro-bind-try/--bind/--symlink/--tmpfs/--dir/--proc/--dev/--chdir`，含 `AddRuntimeBinds` 追加的运行时绑定）的**最后一次出现**都在 seal 之前，入口分隔符 `--` 在 seal 之后。这守护了 bubblewrap 按参数顺序应用挂载的顺序敏感约束。

## 4. 风险与回滚

- **风险**：极低。纯参数拼装重构，无控制流/数值/顺序变化；三方法输出经机械 diff 证明逐项一致；沙箱行为由 Linux 集成测试（CI 上执行）继续覆盖。
- **残余注意点**：会话版 `/input` tmpfs 的插入位置（`/tmp` 与 `/run` 之间）与两版的 `EXECUTION_*/SANDBOX_*` 差异仍是内联代码，属变体差异而非重复，无需收敛。
- **回滚**：单 commit 纯文本重构，`git revert <commit>` 即可完整回退，不涉及数据/配置/契约变更。
