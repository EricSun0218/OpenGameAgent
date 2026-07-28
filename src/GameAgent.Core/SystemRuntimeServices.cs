namespace GameAgent.Core;

public sealed class SystemRuntimeClock : IRuntimeClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class GuidRuntimeIdGenerator : IRuntimeIdGenerator
{
    public string NewId(string category)
    {
        if (string.IsNullOrWhiteSpace(category)
            || category.Length > 64)
        {
            throw new ArgumentException(
                "Runtime id category is invalid.",
                nameof(category));
        }

        return category + "-" + Guid.NewGuid().ToString("N");
    }
}
