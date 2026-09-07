using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenAgent.Contracts.Execution;
using Xunit;

namespace OpenAgent.Runner.Tests;

public class RunnerApiTests
{
    [GVisorFact]
    public async Task Execute_RealHttpContractReturnsIsolatedBinaryArtifact()
    {
        using var factory = new RealFactory();
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Factory.Key);
        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/execute", new CodeExecutionRequest
        {
            Code = "from openpyxl import Workbook\nw=Workbook()\nw.active['A1']=42\nw.save('/output/report.xlsx')"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CodeExecutionResult result = Assert.IsType<CodeExecutionResult>(await response.Content.ReadFromJsonAsync<CodeExecutionResult>());
        Assert.True(result.ExitCode == 0, result.Stderr);
        ExecutionFile artifact = Assert.Single(result.Files);
        Assert.Equal("report.xlsx", artifact.Name);
        Assert.Equal(new byte[] { 80, 75 }, artifact.Content.Take(2));
        Assert.True(Guid.TryParseExact(result.ExecutionId, "N", out _));
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Unauthorized)]
    [InlineData(true, HttpStatusCode.OK)]
    public async Task Execute_RequiresServiceCredential(bool authorized, HttpStatusCode expected)
    {
        using var factory = new Factory();
        using HttpClient client = factory.CreateClient();
        if (authorized)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Factory.Key);
        }
        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/execute", new CodeExecutionRequest { Code = "print(42)" });
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(authorized ? 1 : 0, factory.Executor.Calls);
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        internal const string Key = "test-only-runner-key-with-32-characters";
        internal FakeExecutor Executor { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Runner:ApiKey", Key);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<ICodeExecutor>();
                services.AddSingleton<ICodeExecutor>(Executor);
            });
        }
    }

    private sealed class FakeExecutor : ICodeExecutor
    {
        internal int Calls { get; private set; }
        public Task<CodeExecutionResult> ExecuteAsync(CodeExecutionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new CodeExecutionResult { Stdout = "42" });
        }
    }

    private sealed class RealFactory : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "codeact-api-tests-" + Guid.NewGuid().ToString("N"));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Runner:ApiKey", Factory.Key);
            builder.UseSetting("Runner:WorkspaceRoot", _root);
            builder.UseSetting("Runner:DockerPath", Environment.GetEnvironmentVariable("CODEACT_TEST_DOCKER") ?? "/usr/bin/docker");
            builder.UseSetting("Runner:DockerHost", Environment.GetEnvironmentVariable("CODEACT_TEST_DOCKER_HOST") ?? string.Empty);
            builder.UseSetting("Runner:Runtime", "runsc");
            builder.UseSetting("Runner:SandboxImage", Environment.GetEnvironmentVariable("CODEACT_TEST_GVISOR_IMAGE") ?? "openagent-codeact:local");
            builder.UseSetting("Runner:SandboxPythonPath", Environment.GetEnvironmentVariable("CODEACT_TEST_SANDBOX_PYTHON") ?? "/opt/openagent-code/venv/bin/python");
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
