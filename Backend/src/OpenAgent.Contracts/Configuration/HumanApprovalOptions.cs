namespace OpenAgent.Contracts.Configuration;

public sealed class HumanApprovalOptions
{
    public const string SectionName = "HumanApproval";

    public int RequestTimeoutMinutes { get; set; } = 15;
    public int SweepIntervalSeconds { get; set; } = 30;
}
