using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Capabilities.Tests.TestDoubles;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Endpoints.Files;
using OpenAgent.Engine.Host.Middleware;
using OpenAgent.Framework.ObjectStorage;
using OpenAgent.Runtime;
using OpenAgent.Runtime.Tests;
using Xunit;
using Xunit.Abstractions;
using static OpenAgent.Engine.Tests.Hosting.SseToolStreamingTests;

namespace OpenAgent.Engine.Tests.Hosting;

public sealed class OpenCodeFileExchangeTests(ITestOutputHelper output)
{
    [OpenCodeS3Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(64, false)]
    public async Task HttpUpload_NativePython_ExportPublishAndDownload_AfterRuntimeAndHostRebuild(
        int payloadMiB, bool disconnectDuringFirstSession)
    {
        string endpoint = Environment.GetEnvironmentVariable("OPENAGENT_TEST_S3_URL")!;
        string access = Environment.GetEnvironmentVariable("OPENAGENT_TEST_S3_ACCESS_KEY")!;
        string secret = Environment.GetEnvironmentVariable("OPENAGENT_TEST_S3_SECRET_KEY")!;
        string bucket = "fx-files-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "fx-files-" + Guid.NewGuid().ToString("N"));
        string stateRoot = Path.Combine(root, "sessions");
        string workspaceRoot = Path.Combine(root, "workspaces");
        RecordingFileAssetRepository repository = new();
        string? uploadedId = null;
        string? originalHash = null;
        string? resultHash = null;
        string? resultId = null;
        string? chartHash = null;
        int generated = 0;
        int restored = 0;
        using AmazonS3Client s3 = new(new BasicAWSCredentials(access, secret), new AmazonS3Config
        {
            ServiceURL = endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        });
        await s3.PutBucketAsync(bucket);
        try
        {
            string executable = Environment.GetEnvironmentVariable("OPENAGENT_TEST_OPENCODE")!;
            if (disconnectDuringFirstSession)
            {
                executable = await CreateDisconnectingLauncherAsync(root, executable);
            }
            await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
            {
                JsonElement[] tools = request.GetProperty("messages").EnumerateArray()
                    .Where(message => message.GetProperty("role").GetString() == "tool").ToArray();
                bool restoring = request.GetRawText().Contains("RESTORE_RESULT", StringComparison.Ordinal);
                string Tool(string suffix) => request.GetProperty("tools").EnumerateArray()
                    .Select(tool => tool.GetProperty("function").GetProperty("name").GetString()!)
                    .Single(name => name == suffix || name.EndsWith("_" + suffix, StringComparison.Ordinal));
                async Task Call(string name, object arguments) => await ModelFixture.ChunkAsync(context, new
                {
                    role = "assistant", tool_calls = new[] { new { index = 0, id = "fx-" + tools.Length,
                        type = "function", function = new { name, arguments = JsonSerializer.Serialize(arguments) } } }
                }, "tool_calls");
                if (restoring)
                {
                    if (tools.Length == 0)
                    {
                        Assert.Contains("outputs/analysis.xlsx", request.GetRawText());
                        Assert.Contains("outputs/chart.png", request.GetRawText());
                        await Call(Tool("import_file"), new { fileId = uploadedId });
                    }
                    else if (tools.Length == 1)
                    {
                        string script = $$"""
                            import hashlib,pathlib
                            assert not pathlib.Path('work/intermediate.csv').exists()
                            result=pathlib.Path('outputs/analysis.xlsx')
                            chart=pathlib.Path('outputs/chart.png')
                            original=pathlib.Path('inputs/{{uploadedId}}/sales.xlsx')
                            assert hashlib.sha256(result.read_bytes()).hexdigest()=='{{resultHash}}'
                            assert hashlib.sha256(chart.read_bytes()).hexdigest()=='{{chartHash}}'
                            assert hashlib.sha256(original.read_bytes()).hexdigest()=='{{originalHash}}'
                            print('FX_RESTORED')
                            """;
                        await Call("bash", new { command = Python(script) });
                    }
                    else
                    {
                        Assert.Contains("FX_RESTORED", tools[^1].GetRawText());
                        restored++;
                        await ModelFixture.TextAsync(context, "Restored original and finished outputs.");
                    }
                    return;
                }
                if (tools.Length == 0)
                {
                    Assert.Contains("inputs/" + uploadedId, request.GetRawText());
                    Assert.Contains("only after publish_files succeeds", request.GetRawText());
                    string script = $$"""
                        import pathlib,hashlib,zipfile,base64,os
                        assert os.getuid()!=0
                        source=pathlib.Path('inputs/{{uploadedId}}/sales.xlsx')
                        assert hashlib.sha256(source.read_bytes()).hexdigest()=='{{originalHash}}'
                        with zipfile.ZipFile(source) as z, zipfile.ZipFile('outputs/analysis.xlsx','w',zipfile.ZIP_DEFLATED) as result:
                            for item in z.namelist():
                                data=z.read(item)
                                if item=='xl/worksheets/sheet1.xml':
                                    assert b'<v>21</v>' in data
                                    data=data.replace(b'<v>21</v>',b'<v>42</v>')
                                result.writestr(item,data)
                        pathlib.Path('work').mkdir()
                        pathlib.Path('work/intermediate.csv').write_text('temporary,scratch')
                        pathlib.Path('outputs/chart.png').write_bytes(base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a5EAAAAAASUVORK5CYII='))
                        print('FX_GENERATED')
                        """;
                    await Call("bash", new { command = Python(script) });
                }
                else if (tools.Length == 1)
                {
                    Assert.Contains("FX_GENERATED", tools[0].GetRawText());
                    generated++;
                    await Call(Tool("publish_files"), new { paths = new[] { "outputs/analysis.xlsx" } });
                }
                else
                {
                    Assert.Contains("fileId", tools[^1].GetRawText());
                    await ModelFixture.TextAsync(context, "Created analysis and chart.");
                }
            });
            Dictionary<string, string?> settings = new()
            {
                ["FileAssets:Enabled"] = "true",
                ["FileAssets:ObjectStorage:ServiceUrl"] = endpoint, ["FileAssets:ObjectStorage:BucketName"] = bucket,
                ["FileAssets:ObjectStorage:Region"] = "us-east-1", ["FileAssets:ObjectStorage:KeyPrefix"] = "files",
                ["FileAssets:ObjectStorage:ForcePathStyle"] = "true", ["FileAssets:ObjectStorage:AccessKey"] = access,
                ["FileAssets:ObjectStorage:SecretKey"] = secret, ["OpenCode:Executable"] = executable,
                ["OpenCode:WorkspaceRoot"] = workspaceRoot, ["OpenCode:SessionStateRoot"] = stateRoot,
                ["OpenCode:KeepAliveSeconds"] = "0", ["OpenCode:TurnTimeoutSeconds"] = "120",
                ["OpenCode:AllowedNativeTools:0"] = "bash", ["OpenCode:AllowedNativeTools:1"] = "read"
            };
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            LlmConfig profile = new() { ModelId = "fixture", Endpoint = model.Endpoint, ApiKey = "local-test", ContextTokens = 100000 };
            IConversationStore? conversations = null;
            async Task<StreamingHost> StartAsync() => await StreamingHost.StartAsync(settings: settings, model: profile,
                configure: services =>
                {
                    services.AddOpenCodeRuntime(config);
                    services.AddS3FileObjectStorage(config, allowInsecureTls: false);
                    services.RemoveAll<IFileAssetRepository>();
                    services.AddSingleton<IFileAssetRepository>(repository);
                    if (conversations is not null)
                    {
                        services.RemoveAll<IConversationStore>();
                        services.AddSingleton(conversations);
                    }
                }, configureApplication: app =>
                {
                    app.MapRuntimeToolBridge();
                    app.Use((context, next) =>
                    {
                        context.Features.Set(new AgentRequestFeature(Guid.NewGuid().ToString("N"),
                            new AgentUserContext { TenantId = "tenant-1", UserId = "user-1" }));
                        return next(context);
                    });
                    app.MapGroup("/api/v1/agent").MapFileAssets();
                });
            await using (StreamingHost host = await StartAsync())
            {
                conversations = host.Store;
                byte[] workbook = Workbook(payloadMiB);
                originalHash = Convert.ToHexStringLower(SHA256.HashData(workbook));
                using MultipartFormDataContent form = new();
                ByteArrayContent file = new(workbook);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
                form.Add(file, "file", "sales.xlsx");
                using HttpResponseMessage uploaded = await host.Client.PostAsync("/api/v1/agent/files", form);
                Assert.True(uploaded.IsSuccessStatusCode, await uploaded.Content.ReadAsStringAsync());
                using JsonDocument metadata = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
                uploadedId = metadata.RootElement.GetProperty("fileId").GetString();
                var frames = await host.PostStreamAsync("file-exchange", [uploadedId!], "GENERATE_RESULT");
                Assert.DoesNotContain(frames, frame => frame.Event == "error");
                Assert.Single(frames, frame => frame.Event == "done");
                ConversationRecord record = (await host.Store.GetRecordAsync("tenant-1", "file-exchange"))!;
                FileAsset result = repository.Assets.Values.Single(asset => asset.FileName == "analysis.xlsx");
                resultId = result.FileId;
                resultHash = result.Sha256;
                Assert.Equal(result.FileId, Assert.Single(record.Messages.Last(message => message.Role == "assistant").FileIds));
                Assert.Single(repository.Assets.Values, asset => asset.FileName == "analysis.xlsx");
                Assert.DoesNotContain(repository.Assets.Values, asset => asset.FileName == "intermediate.csv");
                if (disconnectDuringFirstSession)
                {
                    Assert.True(File.Exists(Path.Combine(root, "restarted")));
                }
                byte[] downloaded = await DownloadAsync(host, resultId!);
                using ZipArchive zip = new(new MemoryStream(downloaded), ZipArchiveMode.Read);
                using StreamReader sheet = new(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
                Assert.Contains("<v>42</v>", await sheet.ReadToEndAsync());
                FileAsset chart = repository.Assets.Values.Single(asset => asset.FileName == "chart.png");
                chartHash = chart.Sha256;
                Assert.Equal("outputs/chart.png", chart.RuntimeOutputPath);
            }
            // Force a cold workspace, including loss of the optional native-session checkpoint.
            Directory.Delete(stateRoot, recursive: true);
            Directory.Delete(workspaceRoot, recursive: true);
            await using (StreamingHost rebuilt = await StartAsync())
            {
                Assert.Equal(resultHash, Convert.ToHexStringLower(SHA256.HashData(await DownloadAsync(rebuilt, resultId!))));
                var frames = await rebuilt.PostStreamAsync("file-exchange", message: "RESTORE_RESULT");
                Assert.DoesNotContain(frames, frame => frame.Event == "error");
                Assert.Single(frames, frame => frame.Event == "done");
            }
            Assert.Equal(3, repository.Assets.Count);
            Assert.Equal(1, generated);
            Assert.Equal(1, restored);
            output.WriteLine("Original XLSX, published XLSX and unpublished PNG survived a cold workspace; the intermediate file did not.");
            output.WriteLine("Input payload: {0} MiB; test process peak working set: {1:F1} MiB; metadata stores are in-memory fixtures.",
                payloadMiB, System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1048576d);
            if (File.Exists("/sys/fs/cgroup/memory.peak"))
            {
                output.WriteLine("Container cgroup memory.peak: {0}", await File.ReadAllTextAsync("/sys/fs/cgroup/memory.peak"));
            }
        }
        finally
        {
            foreach (FileAsset asset in repository.Assets.Values.Where(asset => !string.IsNullOrEmpty(asset.ObjectKey)))
            {
                await s3.DeleteObjectAsync(bucket, asset.ObjectKey);
            }
            await s3.DeleteBucketAsync(bucket);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task<byte[]> DownloadAsync(StreamingHost host, string fileId)
    {
        using HttpResponseMessage response = await host.Client.GetAsync($"/api/v1/agent/files/{fileId}/download?conversationId=file-exchange");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.NotNull(response.Content.Headers.ContentDisposition);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static async Task<string> CreateDisconnectingLauncherAsync(string root, string executable)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }
        Directory.CreateDirectory(root);
        string launcher = Path.Combine(root, "opencode-launcher");
        static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        const string initializeResponse = """{"jsonrpc":"2.0","id":"rpc-1","result":{"protocolVersion":1,"agentInfo":{"name":"test-disconnect","version":"1.19.3"}}}""";
        await File.WriteAllTextAsync(launcher, $$"""
            #!/bin/sh
            if [ -f {{Quote(Path.Combine(root, "first-launch"))}} ]; then
                touch {{Quote(Path.Combine(root, "restarted"))}}
                exec {{Quote(executable)}} "$@"
            fi
            touch {{Quote(Path.Combine(root, "first-launch"))}}
            IFS= read -r initialize || exit 1
            printf '%s\n' {{Quote(initializeResponse)}}
            IFS= read -r session || exit 1
            case "$session" in
                *session/new*) exit 0 ;;
                *) exit 1 ;;
            esac
            """);
        File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return launcher;
    }

    private static string Python(string script) => "python3 -c '" + script.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static byte[] Workbook(int payloadMiB)
    {
        using MemoryStream bytes = new();
        using (ZipArchive zip = new(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string content)
            {
                using StreamWriter writer = new(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
            Add("[Content_Types].xml", """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>""");
            Add("_rels/.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Add("xl/workbook.xml", """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sales" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Add("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
            Add("xl/worksheets/sheet1.xml", """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1"><v>21</v></c></row></sheetData></worksheet>""");
            if (payloadMiB > 0)
            {
                byte[] block = RandomNumberGenerator.GetBytes(1024 * 1024);
                using Stream payload = zip.CreateEntry("xl/media/payload.bin", CompressionLevel.NoCompression).Open();
                for (int index = 0; index < payloadMiB; index++) payload.Write(block);
            }
        }
        return bytes.ToArray();
    }
}

internal sealed class OpenCodeS3TheoryAttribute : TheoryAttribute
{
    public OpenCodeS3TheoryAttribute()
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) || new[] { "OPENAGENT_TEST_OPENCODE", "OPENAGENT_TEST_S3_URL", "OPENAGENT_TEST_S3_ACCESS_KEY", "OPENAGENT_TEST_S3_SECRET_KEY" }
            .Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
        {
            Skip = "Requires Unix, real OpenCode, Python 3, and isolated S3 test settings.";
        }
    }
}
