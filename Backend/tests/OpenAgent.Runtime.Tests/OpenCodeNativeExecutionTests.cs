using System.Text.Json;
using OpenAgent.Contracts.Requests;
using Xunit;

namespace OpenAgent.Runtime.Tests;

public sealed class OpenCodeNativeExecutionTests
{
    [OpenCodeLinuxTheory]
    [InlineData("node", "N02_NODE_EXECUTED")]
    [InlineData("python3", "N02_PYTHON_EXECUTED")]
    public async Task DevelopmentTools_RunNativeCodeWithPrivateHomeTempAndWritableWorkspace(string executable, string marker)
    {
        string script = executable == "node" ? """
            const fs = require('node:fs');
            const os = require('node:os');
            const path = require('node:path');
            const assert = require('node:assert/strict');
            assert.notEqual(process.getuid(), 0);
            assert.equal(process.env.HOME, process.env.USERPROFILE);
            assert.equal(os.tmpdir(), process.env.TMPDIR);
            fs.writeFileSync('local-module.cjs', 'module.exports = n => n * 3;');
            const triple = require(path.resolve('local-module.cjs'));
            fs.writeFileSync('result.json', JSON.stringify({value: triple(7)}));
            assert.equal(JSON.parse(fs.readFileSync('result.json', 'utf8')).value, 21);
            const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'native-node-'));
            fs.writeFileSync(path.join(temp, 'temp.txt'), 'node temp');
            assert.equal(fs.readFileSync(path.join(temp, 'temp.txt'), 'utf8'), 'node temp');
            fs.rmSync(temp, {recursive: true});
            console.log('N02_NODE_EXECUTED');
            """ : """
            import importlib.util, json, os, pathlib, tempfile
            assert os.getuid() != 0
            assert os.environ['HOME'] == os.environ['USERPROFILE']
            assert tempfile.gettempdir() == os.environ['TMPDIR']
            pathlib.Path('local_module.py').write_text('def triple(n): return n * 3\n')
            spec = importlib.util.spec_from_file_location('local_module', 'local_module.py')
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            pathlib.Path('result.json').write_text(json.dumps({'value': module.triple(7)}))
            assert json.loads(pathlib.Path('result.json').read_text())['value'] == 21
            with tempfile.TemporaryDirectory(prefix='native-python-') as temp:
                target = pathlib.Path(temp) / 'temp.txt'
                target.write_text('python temp')
                assert target.read_text() == 'python temp'
            print('N02_PYTHON_EXECUTED')
            """;
        string command = executable + (executable == "node" ? " -e " : " -c ") + "'" + script.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        bool resultSeen = false;
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
        {
            JsonElement[] results = request.GetProperty("messages").EnumerateArray()
                .Where(message => message.GetProperty("role").GetString() == "tool").ToArray();
            if (results.Length == 0)
            {
                string[] tools = request.GetProperty("tools").EnumerateArray()
                    .Select(tool => tool.GetProperty("function").GetProperty("name").GetString()!).ToArray();
                Assert.Contains("bash", tools);
                Assert.DoesNotContain(tools, name => name.EndsWith("_execute_code", StringComparison.Ordinal));
                await ModelFixture.ChunkAsync(context, new
                {
                    role = "assistant", tool_calls = new[] { new { index = 0, id = "native-execution", type = "function",
                        function = new { name = "bash", arguments = JsonSerializer.Serialize(new { command }) } } }
                }, "tool_calls");
            }
            else
            {
                Assert.Contains(marker, results[0].GetRawText());
                resultSeen = true;
                await ModelFixture.TextAsync(context, "execution verified");
            }
        });
        await using RuntimeFixture runtime = new(model.Endpoint, settings =>
        {
            // Consume the actual development whitelist, so disabling execution there breaks this regression.
            using JsonDocument development = JsonDocument.Parse(File.ReadAllText(DevelopmentSettingsPath()));
            string[] allowed = development.RootElement.GetProperty("OpenCode").GetProperty("AllowedNativeTools")
                .EnumerateArray().Select(value => value.GetString()!).ToArray();
            for (int i = 0; i < allowed.Length; i++)
            {
                settings[$"OpenCode:AllowedNativeTools:{i}"] = allowed[i];
            }
        });
        List<AgentStreamEvent> events = await runtime.CollectAsync(runtime.Request);
        Assert.True(resultSeen);
        Assert.Contains(events, item => item.Type == AgentStreamEventType.ToolResult && item.Content?.Contains(marker, StringComparison.Ordinal) == true);
        runtime.AssertWorkspacesReleased();
    }

    private static string DevelopmentSettingsPath()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "Backend", "src", "OpenAgent.Engine.Host", "appsettings.Development.json");
            if (File.Exists(path))
            {
                return path;
            }
        }
        throw new FileNotFoundException("Development settings are required for the native execution regression.");
    }
}
