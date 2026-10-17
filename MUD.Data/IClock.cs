using System;

namespace MUD.Data
{
    /// <summary>Time source for the game loop and the persistence worker, replaceable so tests never sleep.</summary>
    public interface IClock
    {
        DateTime Now { get; }
    }

    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();

        public DateTime Now { get { return DateTime.Now; } }
    }
}
