using System.Globalization;

namespace OpenAgent.Runtime.Acp.TestConsole;

/// <summary>
/// The `fixture` command: a controllable child process for the process tests and the process
/// scenario — environment reporting, non-zero exits, stderr volume, delayed exits, a stdin-
/// ignoring hang, slow stdout and a stdin drainer. Its stdout is plain text, not protocol.
/// </summary>
internal static class FixtureCommand
{
    internal static async Task<int> RunAsync(string[] arguments, TextWriter output, TextWriter error)
    {
        try
        {
            switch (arguments)
            {
                case ["env", string name]:
                    string? value = Environment.GetEnvironmentVariable(name);
                    await output.WriteLineAsync($"{name}={value ?? "<unset>"}").ConfigureAwait(false);
                    return 0;
                case ["stderr", string countText]:
                    return await StderrAsync(error, Parse(countText), 0).ConfigureAwait(false);
                case ["stderr", string countText, string codeText]:
                    return await StderrAsync(error, Parse(countText), Parse(codeText)).ConfigureAwait(false);
                case ["delayed-exit", string millisecondsText, string codeText]:
                    await Task.Delay(Parse(millisecondsText)).ConfigureAwait(false);
                    return Parse(codeText);
                case ["hang", string secondsText]:
                    await Task.Delay(TimeSpan.FromSeconds(Parse(secondsText))).ConfigureAwait(false);
                    return 0;
                case ["slow", string countText, string delayText]:
                    for (int index = 0; index < Parse(countText); index++)
                    {
                        await output.WriteLineAsync($"slow-line-{index + 1}").ConfigureAwait(false);
                        await output.FlushAsync().ConfigureAwait(false);
                        await Task.Delay(Parse(delayText)).ConfigureAwait(false);
                    }

                    return 0;
                case ["read-stdin", string delayText]:
                    int delay = Parse(delayText);
                    int frames = 0;
                    while (await Console.In.ReadLineAsync().ConfigureAwait(false) is not null)
                    {
                        frames++;
                        if (delay > 0) await Task.Delay(delay).ConfigureAwait(false);
                    }

                    await error.WriteLineAsync($"frames={frames}").ConfigureAwait(false);
                    return 0;
            }
        }
        catch (FormatException exception)
        {
            await error.WriteLineAsync($"Invalid fixture argument: {exception.Message}").ConfigureAwait(false);
            return 2;
        }

        await error.WriteLineAsync("Unknown fixture behavior. See docs/integrations/acp-dotnet-sdk/TEST-CONSOLE.md.").ConfigureAwait(false);
        return 2;
    }

    private static async Task<int> StderrAsync(TextWriter error, int count, int exitCode)
    {
        for (int index = 0; index < count; index++)
        {
            await error.WriteLineAsync($"stderr-line-{index + 1}").ConfigureAwait(false);
        }

        return exitCode;
    }

    private static int Parse(string text) => int.Parse(text, CultureInfo.InvariantCulture);
}
