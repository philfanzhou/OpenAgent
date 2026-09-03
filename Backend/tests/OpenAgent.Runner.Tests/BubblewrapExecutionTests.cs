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
        IReadOnlyList<string> arguments = BubblewrapCodeExecutor.BuildArguments(settings, "/var/lib/runner/id", "/opt/runner/sandbox");

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
            assert os.uname().nodename == 'openagent-sandbox'
            assert not pathlib.Path('/var/run/docker.sock').exists()
            assert 'Runner__ApiKey' not in os.environ
            assert 'ConnectionStrings__OpenAgentDatabase' not in os.environ
            assert not pathlib.Path(HOST_MARKER).exists()
            assert pathlib.Path('/input/data.txt').read_text() == 'input value'
            for target in ['/input/data.txt', '/usr/codeact-write-test', '/etc/codeact-write-test']:
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
                workbook.save('/output/report.xlsx')
                assert load_workbook('/output/report.xlsx').active['B2'].value == 42
                slides = Presentation()
                slide = slides.slides.add_slide(slides.slide_layouts[1])
                slide.shapes.title.text = '销售汇报'
                slide.placeholders[1].text = '华东：42'
                slides.save('/output/report.pptx')
                assert Presentation('/output/report.pptx').slides[0].shapes.title.text == '销售汇报'
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

    private sealed class Runtime : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "codeact-tests-" + Guid.NewGuid().ToString("N"));
        internal BubblewrapProcess Bubblewrap { get; }
        internal BubblewrapCodeExecutor Executor { get; }

        internal Runtime(int timeoutSeconds = 120, int memoryMiB = 1536)
        {
            Directory.CreateDirectory(Root);
            var settings = Options.Create(new RunnerOptions
            {
                WorkspaceRoot = Root,
                BubblewrapPath = Environment.GetEnvironmentVariable("CODEACT_TEST_BWRAP") ?? "/usr/bin/bwrap",
                PythonPath = Environment.GetEnvironmentVariable("CODEACT_TEST_PYTHON") ?? "/opt/openagent-code/venv/bin/python",
                TimeoutSeconds = timeoutSeconds,
                MemoryMiB = memoryMiB
            });
            Bubblewrap = new BubblewrapProcess(settings);
            Executor = new BubblewrapCodeExecutor(Bubblewrap, settings, NullLogger<BubblewrapCodeExecutor>.Instance);
        }

        internal async Task WaitForSandboxAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (Bubblewrap.ActiveProcesses == 0)
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
