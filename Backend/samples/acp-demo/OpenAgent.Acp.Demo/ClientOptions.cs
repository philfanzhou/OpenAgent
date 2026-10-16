namespace OpenAgent.Acp.Demo;

internal sealed record ClientOptions(
    string Prompt, bool Cancel, string? AgentCommand, IReadOnlyList<string> AgentArguments,
    string WorkingDirectory, TimeSpan Timeout)
{
    internal static ClientOptions Parse(string[] arguments)
    {
        string? command = null;
        List<string> agentArguments = [];
        List<string> prompt = [];
        string cwd = Directory.GetCurrentDirectory();
        int timeoutSeconds = 30;
        bool cancel = false;
        for (int index = 0; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            switch (argument)
            {
                case "--cancel":
                    cancel = true;
                    break;
                case "--agent":
                    command = ReadValue(arguments, ref index);
                    break;
                case "--agent-arg":
                    agentArguments.Add(ReadValue(arguments, ref index));
                    break;
                case "--cwd":
                    cwd = Path.GetFullPath(ReadValue(arguments, ref index));
                    break;
                case "--timeout-seconds":
                    if (!int.TryParse(ReadValue(arguments, ref index), out timeoutSeconds) || timeoutSeconds is < 1 or > 1800)
                    {
                        throw new ArgumentException("Timeout must be between 1 and 1800 seconds.");
                    }

                    break;
                case "--":
                    prompt.AddRange(arguments.Skip(index + 1));
                    index = arguments.Length;
                    break;
                default:
                    if (argument.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown client option: {argument}");
                    }

                    prompt.Add(argument);
                    break;
            }
        }

        if (command is null && agentArguments.Count != 0)
        {
            throw new ArgumentException("--agent-arg requires --agent.");
        }

        if (!Directory.Exists(cwd))
        {
            throw new DirectoryNotFoundException("Client working directory does not exist.");
        }

        return new ClientOptions(prompt.Count == 0 ? "Hello ACP from C#!" : string.Join(" ", prompt), cancel,
            command, agentArguments, cwd, TimeSpan.FromSeconds(timeoutSeconds));
    }

    private static string ReadValue(string[] arguments, ref int index)
    {
        string option = arguments[index];
        if (++index >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index]))
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return arguments[index];
    }
}
