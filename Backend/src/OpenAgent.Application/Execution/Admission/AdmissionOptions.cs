namespace OpenAgent.Application.Execution.Admission;

/// <summary>
/// 进程内有界准入：同时执行的轮次有上限，超出的先排队，队列也满了就立即拒绝（HTTP 429）。
/// 排队期间不占用历史、会话锁、附件与工具等准备资源；配置在宿主启动时校验。
/// </summary>
public sealed class AdmissionOptions
{
    public const string SectionName = "RuntimeAdmission";

    /// <summary>
    /// 整个进程同时执行的轮次上限，包含准备、执行与终态持久化。默认与运行时的
    /// <c>OpenCode:MaxConcurrentTurns</c> 相同（8）：准入不再额外压低现有吞吐，只负责排队上限与过载信号。
    /// </summary>
    public int MaxConcurrentTurns { get; set; } = 8;

    /// <summary>整个进程的等待队列上限，0 表示不排队、超出立即拒绝。默认 16。</summary>
    public int MaxQueuedTurns { get; set; } = 16;

    /// <summary>单个租户同时执行的轮次上限。默认与进程上限相同，即不额外限制；多租户部署可调低以保证公平。</summary>
    public int MaxConcurrentPerTenant { get; set; } = 8;

    /// <summary>
    /// 单个租户的等待队列上限。默认与进程队列相同，单租户部署也能用满整个队列；
    /// 多租户部署可调低，避免一个租户占满队列。
    /// </summary>
    public int MaxQueuedPerTenant { get; set; } = 16;

    /// <summary>
    /// 排队的最长等待时间，超时按错误收尾；已获准执行的轮次不受它限制。
    /// 队列 16、并发 8 时排在最后的请求要等约两轮，默认 120 秒让每轮 60 秒以内的回答也能排完。
    /// </summary>
    public int QueueTimeoutSeconds { get; set; } = 120;

    /// <summary>从获准执行到持久化结束的时限，与所选 Runtime 的时限取较小值。</summary>
    public int ExecutionTimeoutSeconds { get; set; } = 1800;

    /// <summary>队列满或排队超时时提示的重试间隔（秒），随 Retry-After 返回；服务端不会自动重试。</summary>
    public int RetryAfterSeconds { get; set; } = 1;
}
