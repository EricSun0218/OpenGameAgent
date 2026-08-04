using System;
using GameAgent.Core;

namespace GameAgent.Unity
{
    public sealed class SystemRuntimeClock : IRuntimeClock
    {
        public DateTimeOffset UtcNow
        {
            get { return DateTimeOffset.UtcNow; }
        }
    }

    public sealed class GuidRuntimeIdGenerator : IRuntimeIdGenerator
    {
        public string NewId(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                throw new ArgumentException(
                    "An id category is required.",
                    nameof(category));
            }

            return category + "-" + Guid.NewGuid().ToString("N");
        }
    }
}
