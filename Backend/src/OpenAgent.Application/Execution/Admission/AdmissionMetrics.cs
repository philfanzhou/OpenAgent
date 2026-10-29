using System.Diagnostics.Metrics;

namespace OpenAgent.Application.Execution.Admission;

internal static class AdmissionMetrics
{
    private static readonly Meter Meter = new("OpenAgent.Admission");
    internal static readonly UpDownCounter<long> Active = Meter.CreateUpDownCounter<long>("openagent_admission_active");
    internal static readonly UpDownCounter<long> Waiting = Meter.CreateUpDownCounter<long>("openagent_admission_waiting");
    internal static readonly Counter<long> Rejected = Meter.CreateCounter<long>("openagent_admission_rejected");
    internal static readonly Histogram<double> QueueTime = Meter.CreateHistogram<double>("openagent_admission_queue_duration", "s");
    internal static readonly Histogram<double> ExecutionTime = Meter.CreateHistogram<double>("openagent_admission_execution_duration", "s");
}
