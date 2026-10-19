namespace Shop.Tests;

// A clock that always returns the same moment, so "new arrival" tests do not depend on today's date.
public class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _utcNow;

    public FixedTimeProvider(DateTime utcNow)
    {
        _utcNow = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;
}
