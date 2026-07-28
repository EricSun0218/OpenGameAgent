namespace GameAgent.Core;

public sealed class AttemptIdentity
{
    public string RunAttemptId { get; set; } = string.Empty;

    public string TurnId { get; set; } = string.Empty;

    public string ProviderAttemptId { get; set; } = string.Empty;

    public string StreamAttemptId { get; set; } = string.Empty;
}

public sealed class AttemptFence
{
    private readonly object _gate = new();
    private long _generation;
    private AttemptIdentity? _active;

    public long Activate(AttemptIdentity identity)
    {
        lock (_gate)
        {
            _generation++;
            _active = identity;
            return _generation;
        }
    }

    public bool IsCurrent(long generation, AttemptIdentity identity)
    {
        lock (_gate)
        {
            return generation == _generation
                && _active is not null
                && Same(_active, identity);
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _active = null;
        }
    }

    private static bool Same(AttemptIdentity left, AttemptIdentity right)
    {
        return left.RunAttemptId == right.RunAttemptId
            && left.TurnId == right.TurnId
            && left.ProviderAttemptId == right.ProviderAttemptId
            && left.StreamAttemptId == right.StreamAttemptId;
    }
}
