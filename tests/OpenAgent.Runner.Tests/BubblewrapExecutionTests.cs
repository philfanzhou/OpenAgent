using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Execution;
using Xunit;

namespace OpenAgent.Runner.Tests;

public class BubblewrapExecutionTests
{
    [Fact]
    public void BuildArguments_UsesFailClosedNamespacesAndOnlyExplicitMounts()
    {
        var settings = new RunnerOptions { PythonPath = "/opt/openagent-code/venv/bin/python" };
        IReadOnlyList<string> arguments = BubblewrapCodeExecutor.BuildArguments(
            settings, "/var/lib/runner/id", "/opt/runner/sandbox", ExecutionLanguage.Python);

        Assert.Contains("--unshare-user", arguments);
        Assert.Contains("--unshare-net", arguments);
        Assert.Contains("--disable-userns", arguments);
        Assert.Contains("--new-session", arguments);
        Assert.Contains("--die-with-parent", arguments);
        Assert.Contains("--clearenv", arguments);
        Assert.Contains("--ro-bind", arguments);
        Assert.Contains("--tmpfs", arguments);
        Assert.Contains("--remount-ro", arguments);
        Assert.DoesNotContain("--share-net", arguments);
        Assert.DoesNotContain("--cap-add", arguments);
        Assert.DoesNotContain("/", arguments.SkipWhile(argument => argument != "--ro-bind").Skip(1).Take(1));
        Assert.Contains("--as=1610612736:1610612736", arguments);
        Assert.Contains("--nproc=64:64", arguments);
        Assert.Equal(ExecutionLanguage.Python, EnvironmentValue(arguments, "EXECUTION_LANGUAGE"));
    }

    [Fact]
    public void BuildArguments_PassesCustomEntryToSandbox()
    {
        var settings = new RunnerOptions { PythonPath = "/opt/openagent-code/venv/bin/python" };
        IReadOnlyList<string> custom = BubblewrapCodeExecutor.BuildArguments(
            settings, "/var/lib/runner/id", "/opt/runner/sandbox", ExecutionLanguage.Python,
            "openagent_skill_entry__.py");
        IReadOnlyList<string> fallback = BubblewrapCodeExecutor.BuildArguments(
            settings, "/var/lib/runner/id", "/opt/runner/sandbox", ExecutionLanguage.Python);

        Assert.Equal("openagent_skill_entry__.py", EnvironmentValue(custom, "EXECUTION_ENTRY"));
        Assert.Equal("main.py", EnvironmentValue(fallback, "EXECUTION_ENTRY"));
    }

    [Fact]
    public void BuildArguments_RoutesLanguageAndNodeRuntime()
    {
        var settings = new RunnerOptions
        {
            PythonPath = "/opt/openagent-code/venv/bin/python",
            NodePath = "/opt/node/bin/node"
        };
        IReadOnlyList<string> javascript = BubblewrapCodeExecutor.BuildArguments(
            settings, "/var/lib/runner/id", "/opt/runner/sandbox", ExecutionLanguage.JavaScript);

        Assert.Equal(ExecutionLanguage.JavaScript, EnvironmentValue(javascript, "EXECUTION_LANGUAGE"));
        Assert.Equal("/opt/node/bin/node", EnvironmentValue(javascript, "EXECUTION_NODE"));
        Assert.Contains(RuntimeBin(settings.NodePath), EnvironmentValue(javascript, "PATH"));
        Assert.Contains(RuntimeBin(settings.PythonPath), EnvironmentValue(javascript, "PATH"));
        Assert.Contains("/usr/bin", EnvironmentValue(javascript, "PATH"));
        Assert.True(BindsSource(javascript, RuntimeRootOf(settings.NodePath)));
        Assert.True(BindsSource(javascript, RuntimeRootOf(settings.PythonPath)));

        IReadOnlyList<string> python = BubblewrapCodeExecutor.BuildArguments(
            settings, "/var/lib/runner/id", "/opt/runner/sandbox", ExecutionLanguage.Python);
        Assert.Equal(ExecutionLanguage.Python, EnvironmentValue(python, "EXECUTION_LANGUAGE"));
    }

    [Fact]
    public void BuildArguments_DefaultNodeInUsrNeedsNoExtraMount()
    {
        var settings = new RunnerOptions
        {
            PythonPath = "/opt/openagent-code/venv/bin/python",
            NodePath = "/usr/bin/node"
        };
        IReadOnlyList<string> arguments = BubblewrapCodeExecutor.BuildArguments(
            settings, "/var/lib/runner/id", "/opt/runner/sandbox", ExecutionLanguage.JavaScript);

        string? path = EnvironmentValue(arguments, "PATH");
        Assert.NotNull(path);
        Assert.Equal(1, path.Split(':').Count(entry => entry == "/usr/bin"));
        Assert.Equal(1, BindCount(arguments, "/usr"));
    }

    // The guards below pin the single-source hardening contract shared by the
    // ephemeral and session sandbox builders: identical isolation/root-filesystem
    // prefix and a root that is only sealed read-only after every mount.
    [Fact]
    public void BuildArguments_SessionAndEphemeral_ShareHardeningPrefix()
    {
        IReadOnlyList<string> ephemeral = BuildVariantArguments("ephemeral");
        IReadOnlyList<string> session = BuildVariantArguments("session");

        int shared = 0;
        while (shared < ephemeral.Count && shared < session.Count && ephemeral[shared] == session[shared])
        {
            shared++;
        }

        // The shared head must cover the full isolation and root-filesystem
        // hardening, i.e. everything up to and including /etc/nsswitch.conf.
        Assert.True(shared > IndexOf(ephemeral, "/etc/nsswitch.conf"));
        // The variants diverge exactly at their input mount: the one-shot
        // read-only /input bind versus the session read-write /channel bind.
        Assert.Equal("--ro-bind", ephemeral[shared]);
        Assert.Equal("/input", ephemeral[shared + 2]);
        Assert.Equal("--bind", session[shared]);
        Assert.Equal("/channel", session[shared + 2]);
    }

    [Theory]
    [InlineData("ephemeral")]
    [InlineData("session")]
    public void BuildArguments_EachVariant_RemountsRootReadOnlyAfterAllMounts(string variant)
    {
        IReadOnlyList<string> arguments = BuildVariantArguments(variant);

        int seal = IndexOf(arguments, "--remount-ro");
        Assert.True(seal > 0);
        Assert.Equal("/", arguments[seal + 1]);

        // Bubblewrap applies mounts in argument order: every mount must precede
        // the read-only seal, and the entry point must only follow the seal.
        foreach (string mount in new[]
                 {
                     "--ro-bind", "--ro-bind-try", "--bind", "--symlink", "--tmpfs",
                     "--dir", "--proc", "--dev", "--chdir"
                 })
        {
            Assert.True(LastIndexOf(arguments, mount) < seal, $"{mount} must precede the seal");
        }
        Assert.True(seal < IndexOf(arguments, "--"));
    }

    private static IReadOnlyList<string> BuildVariantArguments(string variant)
    {
        var settings = new RunnerOptions
        {
            PythonPath = "/opt/openagent-code/venv/bin/python",
            NodePath = "/opt/node/bin/node"
        };
        return variant switch
        {
            "ephemeral" => BubblewrapCodeExecutor.BuildArguments(
                settings, "/var/lib/runner/id", "/opt/runner/sandbox", ExecutionLanguage.Python),
            "session" => BubblewrapCodeExecutor.BuildSessionArguments(
                settings, "/var/lib/runner/session-demo/channel", "/opt/runner/sandbox"),
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown sandbox variant.")
        };
    }

    private static int IndexOf(IReadOnlyList<string> arguments, string value)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] == value)
            {
                return index;
            }
        }
        return -1;
    }

    private static int LastIndexOf(IReadOnlyList<string> arguments, string value)
    {
        for (int index = arguments.Count - 1; index >= 0; index--)
        {
            if (arguments[index] == value)
            {
                return index;
            }
        }
        return -1;
    }

    // Mirrors the runtime-root derivation in BubblewrapCodeExecutor so the
    // assertions stay meaningful on hosts where System.IO rewrites Unix paths.
    private static string RuntimeRootOf(string executable) =>
        Directory.GetParent(Path.GetDirectoryName(executable)
            ?? throw new InvalidOperationException("Path has no parent directory."))!.FullName;

    private static string RuntimeBin(string executable) => RuntimeRootOf(executable) + "/bin";

    private static string? EnvironmentValue(IReadOnlyList<string> arguments, string name)
    {
        for (int index = 0; index < arguments.Count - 2; index++)
        {
            if (arguments[index] == "--setenv" && arguments[index + 1] == name)
            {
                return arguments[index + 2];
            }
        }
        return null;
    }

    private static bool BindsSource(IReadOnlyList<string> arguments, string source) => BindCount(arguments, source) > 0;

    private static int BindCount(IReadOnlyList<string> arguments, string source)
    {
        int count = 0;
        for (int index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index] == "--ro-bind" && arguments[index + 1] == source)
            {
                count++;
            }
        }
        return count;
    }

    [Fact]
    public async Task Execute_RejectsUnsupportedLanguageAndReservedEntryName()
    {
        await using var runtime = new Runtime();
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.Executor.ExecuteAsync(
            new CodeExecutionRequest { Code = "print(1)", Language = "ruby" }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.Executor.ExecuteAsync(new CodeExecutionRequest
        {
            Code = "print(1)",
            Files = [new ExecutionFile { Name = "main.mjs", Content = [1, 2, 3] }]
        }, CancellationToken.None));
        Assert.Empty(Directory.EnumerateDirectories(runtime.Root));
    }

    [BubblewrapFact]
    public async Task Execute_JavaScriptRunsNodeInIsolatedSandbox()
    {
        await using var runtime = new Runtime();
        CodeExecutionResult result = await runtime.Executor.ExecuteAsync(new CodeExecutionRequest
        {
            Language = ExecutionLanguage.JavaScript,
            Code = """
                import assert from 'node:assert/strict';
                import { readFile, writeFile } from 'node:fs/promises';
                assert.strictEqual(process.getuid?.() ?? 65532, 65532);
                assert.strictEqual('Runner__ApiKey' in process.env, false);
                const input = await readFile('/input/data.txt', 'utf8');
                assert.strictEqual(input.trim(), 'node input');
                await writeFile('/output/result.txt', input.trim() + ' verified');
                console.log('javascript executed', process.version);
                """,
            Files = [new ExecutionFile { Name = "data.txt", Content = "node input"u8.ToArray() }]
        }, CancellationToken.None);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Contains("javascript executed", result.Stdout);
        ExecutionFile output = Assert.Single(result.Files);
        Assert.Equal("result.txt", output.Name);
        Assert.Equal("node input verified", System.Text.Encoding.UTF8.GetString(output.Content));
        await runtime.AssertCleanAsync();
    }

    [BubblewrapFact]
    public async Task Execute_EnforcesNamespaceFilesystemAndEnvironmentBoundary()
    {
        await using var runtime = new Runtime();
        string marker = Path.Combine(runtime.Root, "host-secret.txt");
        await File.WriteAllTextAsync(marker, "host-only-secret");
        string code = """
            import os, pathlib, socket, subprocess, time
            assert os.getuid() == 65532
            status = dict(line.split(':', 1) for line in pathlib.Path('/proc/self/status').read_text().splitlines())
            assert int(status['CapEff'], 16) == 0
            assert int(status['CapPrm'], 16) == 0
            assert int(status['NoNewPrivs']) == 1
            assert os.uname().nodename == 'openagent-sandbox'
            assert 'Runner__ApiKey' not in os.environ
            assert 'ConnectionStrings__OpenAgentDatabase' not in os.environ
            assert not pathlib.Path(HOST_MARKER).exists()
            assert pathlib.Path('/input/data.txt').read_text() == 'input value'
            for target in ['/input/data.txt', '/usr/codeact-write-test', '/etc/codeact-write-test', '/proc/sys/kernel/hostname']:
                try:
                    pathlib.Path(target).write_text('escape')
                except OSError:
                    pass
                else:
                    raise AssertionError('Unexpected writable path: ' + target)
            assert subprocess.run(['/usr/bin/unshare', '--user', 'true'], capture_output=True).returncode != 0
            try:
                socket.create_connection(('1.1.1.1', 443), timeout=1)
            except OSError:
                pass
            else:
                raise AssertionError('Unexpected outbound network')
            print('isolation passed', flush=True)
            time.sleep(1)
            """.Replace("HOST_MARKER", JsonSerializer.Serialize(marker), StringComparison.Ordinal);

        CodeExecutionResult result = await runtime.Executor.ExecuteAsync(new CodeExecutionRequest
        {
            Code = code,
            Files = [new ExecutionFile { Name = "data.txt", Content = "input value"u8.ToArray() }]
        }, CancellationToken.None);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Contains("isolation passed", result.Stdout);
        Assert.Equal("host-only-secret", await File.ReadAllTextAsync(marker));
        await runtime.AssertCleanAsync();
    }

    [BubblewrapFact]
    public async Task Execute_GeneratesEditableOfficeFilesAndAllowsSubsequentEditing()
    {
        await using var runtime = new Runtime();
        CodeExecutionResult result = await runtime.Executor.ExecuteAsync(new CodeExecutionRequest
        {
            Code = """
                from openpyxl import Workbook, load_workbook
                from pptx import Presentation
                from pathlib import Path
                import subprocess
                import time
                workbook = Workbook()
                sheet = workbook.active
                sheet.append(['地区', '数量'])
                sheet.append(['华东', 42])
                sheet.append(['华南', 17])
                sheet.append(['华北', 29])
                workbook.save('/output/report.xlsx')
                loaded = load_workbook('/output/report.xlsx', read_only=True).active
                rows = list(loaded.iter_rows(values_only=True))
                assert rows == [('地区', '数量'), ('华东', 42), ('华南', 17), ('华北', 29)]
                slides = Presentation()
                slide = slides.slides.add_slide(slides.slide_layouts[1])
                slide.shapes.title.text = '销售汇报'
                slide.placeholders[1].text = '\n'.join(f'{region}：{quantity}' for region, quantity in rows[1:])
                slides.save('/output/report.pptx')
                verified = Presentation('/output/report.pptx')
                assert verified.slides[0].shapes.title.text == '销售汇报'
                assert '华南：17' in verified.slides[0].placeholders[1].text
                conversion = subprocess.run(['libreoffice', '-env:UserInstallation=file:///tmp/lo', '--headless', '--convert-to', 'pdf', '--outdir', '/output', '/output/report.pptx'], capture_output=True, timeout=45)
                assert conversion.returncode == 0, conversion.stderr
                pdf = Path('/output/report.pdf')
                deadline = time.monotonic() + 10
                while not pdf.exists() and time.monotonic() < deadline:
                    time.sleep(0.1)
                assert pdf.exists(), (conversion.stdout, conversion.stderr, list(Path('/output').iterdir()))
                assert pdf.read_bytes().startswith(b'%PDF')
                print('documents verified')
                """
        }, CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.Stderr);
        ExecutionFile excel = Assert.Single(result.Files, file => file.Name == "report.xlsx");
        ExecutionFile ppt = Assert.Single(result.Files, file => file.Name == "report.pptx");
        Assert.Single(result.Files, file => file.Name == "report.pdf");
        using (var archive = new ZipArchive(new MemoryStream(excel.Content)))
        {
            Assert.NotNull(archive.GetEntry("xl/workbook.xml"));
        }
        using (var archive = new ZipArchive(new MemoryStream(ppt.Content)))
        {
            Assert.NotNull(archive.GetEntry("ppt/slides/slide1.xml"));
        }

        CodeExecutionResult edited = await runtime.Executor.ExecuteAsync(new CodeExecutionRequest
        {
            Files = [excel],
            Code = "from openpyxl import load_workbook\nw=load_workbook('/input/report.xlsx')\nw.active['B2']=84\nw.save('/output/updated.xlsx')\nprint(w.active['B2'].value)"
        }, CancellationToken.None);
        Assert.Equal(0, edited.ExitCode);
        Assert.Contains("84", edited.Stdout);
        Assert.Single(edited.Files);
        await runtime.AssertCleanAsync();
    }

    [BubblewrapFact]
    public async Task Execute_TimeoutStopsChildProcessesAndCleansWorkspace()
    {
        await using var runtime = new Runtime(timeoutSeconds: 2);
        CodeExecutionResult result = await runtime.Executor.ExecuteAsync(new CodeExecutionRequest
        {
            Code = "import subprocess,time\nsubprocess.Popen(['python','-c','import time; time.sleep(600)'])\ntime.sleep(600)"
        }, CancellationToken.None);
        Assert.True(result.TimedOut);
        Assert.NotEqual(0, result.ExitCode);
        await runtime.AssertCleanAsync();
    }

    [BubblewrapFact]
    public async Task Execute_CancellationKillsSandboxEvenWhenRequestIsAborted()
    {
        await using var runtime = new Runtime();
        using var cancel = new CancellationTokenSource();
        Task<CodeExecutionResult> execution = runtime.Executor.ExecuteAsync(new CodeExecutionRequest
        {
            Code = "import time\ntime.sleep(600)"
        }, cancel.Token);
        await runtime.WaitForSandboxAsync();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        await runtime.AssertCleanAsync();
    }

    [BubblewrapFact]
    public async Task Execute_RejectsSymlinkArtifactsAndTruncatesOutput()
    {
        await using var runtime = new Runtime();
        CodeExecutionResult result = await runtime.Executor.ExecuteAsync(new CodeExecutionRequest
        {
            Code = "import os\nprint('x'*200000)\nos.symlink('/etc/passwd','/output/leak.txt')"
        }, CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.Files);
        Assert.True(result.Stdout.Length <= ExecutionLimits.MaxLogCharacters);
        await runtime.AssertCleanAsync();
    }

    [BubblewrapFact]
    public async Task Execute_MemoryExhaustionTerminatesOnlyTheSandbox()
    {
        await using var runtime = new Runtime(memoryMiB: 128);
        CodeExecutionResult result = await runtime.Executor.ExecuteAsync(new CodeExecutionRequest
        {
            Code = "chunks=[]\nwhile True: chunks.append(bytearray(16*1024*1024))"
        }, CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        await runtime.AssertCleanAsync();
        CodeExecutionResult next = await runtime.Executor.ExecuteAsync(new CodeExecutionRequest { Code = "print(42)" }, CancellationToken.None);
        Assert.Equal(0, next.ExitCode);
        Assert.Contains("42", next.Stdout);
    }

    [BubblewrapFact]
    public async Task Execute_WorkAndOutputTmpfsAreBounded()
    {
        await using var runtime = new Runtime();
        CodeExecutionResult result = await runtime.Executor.ExecuteAsync(new CodeExecutionRequest
        {
            Code = """
                import errno, os
                output_stats = os.statvfs('/output')
                work_stats = os.statvfs('/work')
                assert output_stats.f_blocks * output_stats.f_frsize <= 32*1024*1024
                assert work_stats.f_blocks * work_stats.f_frsize <= 128*1024*1024
                try:
                    with open('/output/large.txt', 'wb', buffering=0) as output:
                        for _ in range(40):
                            output.write(b'x' * 1024 * 1024)
                except OSError as error:
                    assert error.errno in (errno.ENOSPC, errno.EFBIG)
                else:
                    raise AssertionError('Unbounded output disk')
                os.remove('/output/large.txt')
                print('disk limit passed')
                """
        }, CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Contains("disk limit passed", result.Stdout);
        await runtime.AssertCleanAsync();
    }

    [BubblewrapFact]
    public async Task Execute_FreshSandboxDoesNotRetainPreviousTaskFiles()
    {
        await using var runtime = new Runtime();
        await runtime.Executor.ExecuteAsync(new CodeExecutionRequest
        {
            Code = "from pathlib import Path\nPath('/work/private.txt').write_text('secret')"
        }, CancellationToken.None);
        CodeExecutionResult result = await runtime.Executor.ExecuteAsync(new CodeExecutionRequest
        {
            Code = "from pathlib import Path\nassert not Path('/work/private.txt').exists()\nassert list(Path('/output').iterdir()) == []"
        }, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        await runtime.AssertCleanAsync();
    }

    [BubblewrapFact]
    public async Task Execute_ConcurrentRunsUseIndependentSandboxesAndBothComplete()
    {
        await using var runtime = new Runtime();

        Task<CodeExecutionResult> alpha = runtime.Executor.ExecuteAsync(
            CreateConcurrentRequest("alpha"), CancellationToken.None);
        Task<CodeExecutionResult> beta = runtime.Executor.ExecuteAsync(
            CreateConcurrentRequest("beta"), CancellationToken.None);

        await runtime.WaitForSandboxesAsync(2);
        CodeExecutionResult[] results = await Task.WhenAll(alpha, beta);

        Assert.All(results, result => Assert.Equal(0, result.ExitCode));
        Assert.Equal("alpha", ReadOutput(results[0], "alpha.txt"));
        Assert.Equal("beta", ReadOutput(results[1], "beta.txt"));
        await runtime.AssertCleanAsync();
    }

    private static CodeExecutionRequest CreateConcurrentRequest(string value) => new()
    {
        Code = $"""
            from pathlib import Path
            import time
            Path('/work/shared-name.txt').write_text('{value}')
            time.sleep(1)
            assert Path('/work/shared-name.txt').read_text() == '{value}'
            Path('/output/{value}.txt').write_text('{value}')
            """
    };

    private static string ReadOutput(CodeExecutionResult result, string name)
    {
        ExecutionFile output = Assert.Single(result.Files, file => file.Name == name);
        return System.Text.Encoding.UTF8.GetString(output.Content);
    }

    private sealed class Runtime : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "codeact-tests-" + Guid.NewGuid().ToString("N"));
        internal BubblewrapProcess Bubblewrap { get; }
        internal SessionSandboxManager Manager { get; }
        internal BubblewrapCodeExecutor Executor { get; }

        internal Runtime(int timeoutSeconds = 120, int memoryMiB = 1536)
        {
            Directory.CreateDirectory(Root);
            var settings = Options.Create(new RunnerOptions
            {
                WorkspaceRoot = Root,
                BubblewrapPath = Environment.GetEnvironmentVariable("CODEACT_TEST_BWRAP") ?? "/usr/bin/bwrap",
                PythonPath = Environment.GetEnvironmentVariable("CODEACT_TEST_PYTHON") ?? "/opt/openagent-code/venv/bin/python",
                NodePath = Environment.GetEnvironmentVariable("CODEACT_TEST_NODE") ?? "/usr/bin/node",
                TimeoutSeconds = timeoutSeconds,
                MemoryMiB = memoryMiB
            });
            Bubblewrap = new BubblewrapProcess(settings, NullLogger<BubblewrapProcess>.Instance);
            Manager = new SessionSandboxManager(Bubblewrap, settings, NullLogger<SessionSandboxManager>.Instance);
            Executor = new BubblewrapCodeExecutor(Bubblewrap, Manager, settings, NullLogger<BubblewrapCodeExecutor>.Instance);
        }

        internal async Task WaitForSandboxAsync()
        {
            await WaitForSandboxesAsync(1);
        }

        internal async Task WaitForSandboxesAsync(int count)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (Bubblewrap.ActiveProcesses < count)
            {
                await Task.Delay(50, deadline.Token);
            }
        }

        internal async Task AssertCleanAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (Bubblewrap.ActiveProcesses != 0)
            {
                await Task.Delay(50, deadline.Token);
            }
            Assert.Empty(Directory.EnumerateDirectories(Root));
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
            return ValueTask.CompletedTask;
        }
    }
}
