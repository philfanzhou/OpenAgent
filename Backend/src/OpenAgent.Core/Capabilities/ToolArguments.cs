using System.Globalization;

namespace OpenAgent.Core.Capabilities;

internal static class ToolArguments
{
    internal static string? ReadString(IReadOnlyDictionary<string, object?> arguments, string name) =>
        arguments.TryGetValue(name, out object? value) ? value?.ToString() : null;

    internal static int? ReadInt(IReadOnlyDictionary<string, object?> arguments, string name) =>
        int.TryParse(ReadString(arguments, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;

    internal static bool ReadBool(IReadOnlyDictionary<string, object?> arguments, string name) =>
        bool.TryParse(ReadString(arguments, name), out bool value) && value;
}
