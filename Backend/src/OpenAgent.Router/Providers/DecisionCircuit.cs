namespace OpenAgent.Router.Providers;

internal sealed class DecisionCircuit(TimeProvider clock)
{
    private readonly object _gate = new();
    private long _generation;
    private int _failures;
    private DateTimeOffset? _openUntil;
    private bool _probing;

    internal bool TryEnter(out Attempt attempt)
    {
        lock (_gate)
        {
            attempt = default;
            if (_openUntil.HasValue)
            {
                if (_probing || _openUntil.Value > clock.GetUtcNow())
                    return false;
                _probing = true;
            }
            attempt = new Attempt(_generation, _probing);
            return true;
        }
    }

    internal void Complete(Attempt attempt, bool? succeeded)
    {
        lock (_gate)
        {
            if (attempt.Generation != _generation)
                return;
            if (attempt.IsProbe)
                _probing = false;
            if (succeeded == true)
            {
                _failures = 0;
                _openUntil = null;
            }
            else if (succeeded == false && (attempt.IsProbe || ++_failures >= 5))
            {
                _openUntil = clock.GetUtcNow().AddSeconds(30);
                _generation++;
            }
        }
    }

    internal readonly record struct Attempt(long Generation, bool IsProbe);
}
