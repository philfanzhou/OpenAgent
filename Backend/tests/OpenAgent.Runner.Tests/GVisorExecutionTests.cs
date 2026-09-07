using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Execution;
using Xunit;

namespace OpenAgent.Runner.Tests;

public class GVisorExecutionTests
{
    [Fact]
    public void BuildArguments_UsesRunscAndFailClosedContainerDefaults()
    {
        var settings = new RunnerOptions { SandboxPythonPath = "/opt/openagent-code/venv/bin/python" };
        IReadOnlyList<string> arguments = GVisorCodeExecutor.BuildArguments(settings, "container-id");

        Assert.Contains("--runtime", arguments);
        Assert.Contains("runsc", arguments);
        Assert.Contains("--network", arguments);
        Assert.Contains("none", arguments);
        Assert.Contains("--read-only", arguments);
        Assert.Contains("--cap-drop", arguments);
        Assert.Contains("ALL", arguments);
        Assert.Contains("--security-opt", arguments);
        Assert.Contains("no-new-privileges:true", arguments);
        Assert.Contains("--tmpfs", arguments);
        Assert.Contains("--hostname", arguments);
        Assert.Contains("openagent-sandbox", arguments);
        Assert.DoesNotContain("--privileged", arguments);
        Assert.DoesNotContain("/var/run/docker.sock", arguments);
    }

    [GVisorFact]
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

    [GVisorFact]
    public async Task Execute_GeneratesThreeRowExcelThenBuildsEditablePptFromUploadedFile()
    {
        await using var runtime = new Runtime();
        CodeExecutionResult result = await runtime.Executor.ExecuteAsync(new CodeExecutionRequest
        {
            Code = """
                from openpyxl import Workbook, load_workbook
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
                print('three-row Excel verified')
                """
        }, CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.Stderr);
        ExecutionFile excel = Assert.Single(result.Files, file => file.Name == "report.xlsx");
        using (var archive = new ZipArchive(new MemoryStream(excel.Content)))
        {
            Assert.NotNull(archive.GetEntry("xl/workbook.xml"));
        }

        CodeExecutionResult ppt = await runtime.Executor.ExecuteAsync(new CodeExecutionRequest
        {
            Files = [excel],
            Code = """
                from openpyxl import load_workbook
                from pptx import Presentation
                rows = list(load_workbook('/input/report.xlsx', read_only=True).active.iter_rows(values_only=True))
                assert rows == [('地区', '数量'), ('华东', 42), ('华南', 17), ('华北', 29)]
                slides = Presentation()
                slide = slides.slides.add_slide(slides.slide_layouts[1])
                slide.shapes.title.text = '销售汇报'
                slide.placeholders[1].text = '\n'.join(f'{region}：{quantity}' for region, quantity in rows[1:])
                slides.save('/output/report.pptx')
                verified = Presentation('/output/report.pptx')
                assert verified.slides[0].shapes.title.text == '销售汇报'
                assert '华南：17' in verified.slides[0].placeholders[1].text
                print('Excel upload to editable PPT verified')
                """
        }, CancellationToken.None);
        Assert.Equal(0, ppt.ExitCode);
        Assert.Contains("Excel upload to editable PPT verified", ppt.Stdout);
        ExecutionFile pptFile = Assert.Single(ppt.Files, file => file.Name == "report.pptx");
        using (var archive = new ZipArchive(new MemoryStream(pptFile.Content)))
        {
            Assert.NotNull(archive.GetEntry("ppt/slides/slide1.xml"));
        }
        await runtime.AssertCleanAsync();
    }

    [GVisorFact]
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

    [GVisorFact]
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

    [GVisorFact]
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

    [GVisorFact]
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

    [GVisorFact]
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

    [GVisorFact]
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

    [GVisorFact]
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
        internal DockerProcess Docker { get; }
        internal GVisorCodeExecutor Executor { get; }

        internal Runtime(int timeoutSeconds = 120, int memoryMiB = 1536)
        {
            Directory.CreateDirectory(Root);
            var settings = Options.Create(new RunnerOptions
            {
                WorkspaceRoot = Root,
                DockerPath = Environment.GetEnvironmentVariable("CODEACT_TEST_DOCKER") ?? "/usr/bin/docker",
                DockerHost = Environment.GetEnvironmentVariable("CODEACT_TEST_DOCKER_HOST") ?? string.Empty,
                Runtime = "runsc",
                SandboxImage = Environment.GetEnvironmentVariable("CODEACT_TEST_GVISOR_IMAGE") ?? "openagent-codeact:local",
                SandboxPythonPath = Environment.GetEnvironmentVariable("CODEACT_TEST_SANDBOX_PYTHON") ?? "/opt/openagent-code/venv/bin/python",
                TimeoutSeconds = timeoutSeconds,
                MemoryMiB = memoryMiB
            });
            Docker = new DockerProcess(settings, NullLogger<DockerProcess>.Instance);
            Executor = new GVisorCodeExecutor(Docker, settings, NullLogger<GVisorCodeExecutor>.Instance);
        }

        internal async Task WaitForSandboxAsync()
        {
            await WaitForSandboxesAsync(1);
        }

        internal async Task WaitForSandboxesAsync(int count)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (Docker.ActiveProcesses < count)
            {
                await Task.Delay(50, deadline.Token);
            }
        }

        internal async Task AssertCleanAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (Docker.ActiveProcesses != 0)
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
