using System;
using MUD.Core;
using Xunit;

namespace MUD.Tests
{
    /// <summary>
    /// Pins the thresholds and clearing rules of the brute-force login throttle, so that moving the class into core
    /// can be shown not to change them.
    /// </summary>
    public class LoginThrottleBehaviorTests
    {
        private class Clock
        {
            public DateTime Now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            public DateTime Read() { return Now; }
        }

        private static LoginThrottle Make(Clock clock, int maxFailures = 3, int blockMinutes = 5)
        {
            return new LoginThrottle(maxFailures, TimeSpan.FromMinutes(blockMinutes), clock.Read);
        }

        [Fact]
        public void NothingIsBlockedBeforeAnyFailure()
        {
            Assert.False(Make(new Clock()).IsBlocked("1.2.3.4"));
        }

        [Fact]
        public void FewerFailuresThanTheLimitDoNotBlock()
        {
            var throttle = Make(new Clock());
            throttle.RecordFailure("1.2.3.4");
            throttle.RecordFailure("1.2.3.4");
            Assert.False(throttle.IsBlocked("1.2.3.4"));
        }

        [Fact]
        public void TheLimitFailureBlocksTheKey()
        {
            var throttle = Make(new Clock());
            for (int i = 0; i < 3; i++) { throttle.RecordFailure("1.2.3.4"); }
            Assert.True(throttle.IsBlocked("1.2.3.4"));
        }

        [Fact]
        public void TheBlockIsPerKey()
        {
            var throttle = Make(new Clock());
            for (int i = 0; i < 3; i++) { throttle.RecordFailure("1.2.3.4"); }
            Assert.False(throttle.IsBlocked("5.6.7.8"));
        }

        [Fact]
        public void TheBlockLastsExactlyTheBlockDuration()
        {
            var clock = new Clock();
            var throttle = Make(clock);
            for (int i = 0; i < 3; i++) { throttle.RecordFailure("1.2.3.4"); }

            clock.Now += TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1);
            Assert.True(throttle.IsBlocked("1.2.3.4"));

            clock.Now += TimeSpan.FromSeconds(1);
            Assert.False(throttle.IsBlocked("1.2.3.4"));
        }

        [Fact]
        public void AfterABlockStartsTheFailureCountStartsAgain()
        {
            var clock = new Clock();
            var throttle = Make(clock);
            for (int i = 0; i < 3; i++) { throttle.RecordFailure("1.2.3.4"); }
            clock.Now += TimeSpan.FromMinutes(6);

            throttle.RecordFailure("1.2.3.4");
            throttle.RecordFailure("1.2.3.4");
            Assert.False(throttle.IsBlocked("1.2.3.4"));
            throttle.RecordFailure("1.2.3.4");
            Assert.True(throttle.IsBlocked("1.2.3.4"));
        }

        [Fact]
        public void ASuccessClearsTheFailureCount()
        {
            var throttle = Make(new Clock());
            throttle.RecordFailure("1.2.3.4");
            throttle.RecordFailure("1.2.3.4");
            throttle.RecordSuccess("1.2.3.4");
            throttle.RecordFailure("1.2.3.4");
            throttle.RecordFailure("1.2.3.4");
            Assert.False(throttle.IsBlocked("1.2.3.4"));
        }

        [Fact]
        public void ASuccessDoesNotLiftAnActiveBlock()
        {
            var throttle = Make(new Clock());
            for (int i = 0; i < 3; i++) { throttle.RecordFailure("1.2.3.4"); }
            throttle.RecordSuccess("1.2.3.4");
            Assert.True(throttle.IsBlocked("1.2.3.4"));
        }

        [Fact]
        public void ManyKeysDoNotLoseAnActiveBlock()
        {
            var throttle = Make(new Clock());
            for (int i = 0; i < 3; i++) { throttle.RecordFailure("blocked"); }
            for (int i = 0; i < 300; i++) { throttle.RecordFailure("other" + i); }

            Assert.True(throttle.IsBlocked("blocked"));
            Assert.False(throttle.IsBlocked("other0"));
        }
    }
}
