using Microsoft.Extensions.Logging;

namespace OpenAgent.Runtime;

internal static partial class OpenCodeLog
{
    // 由 OpenCode:LogUsageUpdates 显式开启；只记录类型化的 usage_update 值，不落对话报文。
    [LoggerMessage(EventId = 4500, Level = LogLevel.Information, Message = "OpenCode usage update: used={Used} size={Size}")]
    internal static partial void UsageUpdate(ILogger logger, long used, long size);
}
