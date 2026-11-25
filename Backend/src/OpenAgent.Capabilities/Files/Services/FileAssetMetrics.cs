using System.Diagnostics.Metrics;

namespace OpenAgent.Capabilities.Files.Services;

internal static class FileAssetMetrics
{
    private static readonly Meter Meter = new("OpenAgent.Engine");
    internal static readonly Histogram<double> RestoreDuration = Meter.CreateHistogram<double>("openagent.files.restore.duration", "s");
    internal static readonly Counter<long> StoredBytes = Meter.CreateCounter<long>("openagent.files.stored.bytes", "By");
    internal static readonly Counter<long> StoredAssets = Meter.CreateCounter<long>("openagent.files.stored.assets");
}
