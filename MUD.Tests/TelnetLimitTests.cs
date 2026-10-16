using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using MUD.Telnet;
using Xunit;

namespace MUD.Tests
{
    public class TelnetLimitsTests
    {
        [Fact]
        public void Defaults_MatchApprovedValues()
        {
            var l = new TelnetLimits();
            Assert.Equal(1024, l.MaxLineLength);
            Assert.Equal(5, l.MaxConnectionsPerIp);
            Assert.Equal(200, l.MaxTotalConnections);
            Assert.Equal(TimeSpan.FromMinutes(30), l.IdleTimeoutLoggedIn);
            Assert.Equal(TimeSpan.FromMinutes(2), l.IdleTimeoutPreLogin);
            Assert.Equal(20, l.RateLimitLines);
            Assert.Equal(TimeSpan.FromSeconds(10), l.RateLimitWindow);
            Assert.Equal(5, l.LoginMaxFailures);
            Assert.Equal(TimeSpan.FromMinutes(5), l.LoginThrottleDuration);
        }

        [Fact]
        public void FromEnvironment_OverridesValidValues_IgnoresInvalid()
        {
            var env = new Dictionary<string, string>
            {
                { "MUD_MAX_LINE_LENGTH", "256" },
                { "MUD_MAX_CONNECTIONS_PER_IP", "abc" },
                { "MUD_MAX_CONNECTIONS", "-3" },
                { "MUD_IDLE_TIMEOUT_PRELOGIN_SECONDS", "30" },
            };
            var l = TelnetLimits.FromEnvironment(n => env.TryGetValue(n, out var v) ? v : null);
            Assert.Equal(256, l.MaxLineLength);
            Assert.Equal(5, l.MaxConnectionsPerIp);
            Assert.Equal(200, l.MaxTotalConnections);
            Assert.Equal(TimeSpan.FromSeconds(30), l.IdleTimeoutPreLogin);
            Assert.Equal(TimeSpan.FromMinutes(30), l.IdleTimeoutLoggedIn);
        }
    }

    public class RateLimiterTests
    {
        [Fact]
        public void AllowsLimitThenRejectsUntilWindowSlides()
        {
            var limiter = new RateLimiter(20, TimeSpan.FromSeconds(10));
            var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 20; i++) { Assert.True(limiter.TryAcquire(t)); }
            Assert.False(limiter.TryAcquire(t.AddSeconds(9.9)));
            Assert.True(limiter.TryAcquire(t.AddSeconds(10)));
        }
    }

    public class ConnectionTrackerTests
    {
        [Fact]
        public void EnforcesPerAddressCap_AndReleases()
        {
            var tracker = new ConnectionTracker(5, 200);
            for (int i = 0; i < 5; i++) { Assert.True(tracker.TryAdd("1.1.1.1")); }
            Assert.False(tracker.TryAdd("1.1.1.1"));
            Assert.True(tracker.TryAdd("2.2.2.2"));
            tracker.Remove("1.1.1.1");
            Assert.True(tracker.TryAdd("1.1.1.1"));
            Assert.Equal(6, tracker.Total);
        }

        [Fact]
        public void EnforcesTotalCap()
        {
            var tracker = new ConnectionTracker(5, 200);
            for (int i = 0; i < 200; i++) { Assert.True(tracker.TryAdd("10.0." + (i / 5) + ".1")); }
            Assert.False(tracker.TryAdd("9.9.9.9"));
            Assert.Equal(200, tracker.Total);
        }

        [Fact]
        public void RemoveOfUnknownAddress_DoesNotCorruptCounts()
        {
            var tracker = new ConnectionTracker(5, 200);
            tracker.Remove("nobody");
            Assert.Equal(0, tracker.Total);
        }
    }

    public class LoginThrottleTests
    {
        [Fact]
        public void BlocksAfterFiveFailures_ForFiveMinutes_ThenAllows()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var throttle = new LoginThrottle(5, TimeSpan.FromMinutes(5), () => now);
            for (int i = 0; i < 4; i++) { throttle.RecordFailure("1.1.1.1"); }
            Assert.False(throttle.IsBlocked("1.1.1.1"));
            throttle.RecordFailure("1.1.1.1");
            Assert.True(throttle.IsBlocked("1.1.1.1"));
            Assert.False(throttle.IsBlocked("2.2.2.2"));

            now = now.AddMinutes(4.9);
            Assert.True(throttle.IsBlocked("1.1.1.1"));
            now = now.AddMinutes(0.2);
            Assert.False(throttle.IsBlocked("1.1.1.1"));
        }

        [Fact]
        public void SuccessResetsFailureCount_ButNotAnActiveBlock()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var throttle = new LoginThrottle(5, TimeSpan.FromMinutes(5), () => now);
            for (int i = 0; i < 4; i++) { throttle.RecordFailure("a"); }
            throttle.RecordSuccess("a");
            throttle.RecordFailure("a");
            Assert.False(throttle.IsBlocked("a"));

            for (int i = 0; i < 5; i++) { throttle.RecordFailure("b"); }
            throttle.RecordSuccess("b");
            Assert.True(throttle.IsBlocked("b"));
        }
    }

    public class OptionRefusalsTests
    {
        [Fact]
        public void RefusesDoWithWont_AndWillWithDont_OncePerOption()
        {
            var r = new OptionRefusals();
            Assert.Equal(new byte[] { 255, 252, 31 }, r.RefuseDo(31));
            Assert.Null(r.RefuseDo(31));
            Assert.Equal(new byte[] { 255, 254, 31 }, r.RefuseWill(31));
            Assert.Null(r.RefuseWill(31));
            Assert.Equal(new byte[] { 255, 252, 99 }, r.RefuseDo(99));
        }
    }

    /// <summary>
    /// Drives Client over a real loopback socket pair with a fake clock.
    /// </summary>
    public class ClientSocketTests : IDisposable
    {
        private readonly Socket _listener;
        private readonly Socket _peer;      // test side
        private readonly Socket _serverSide; // handed to Client
        private DateTime _now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public ClientSocketTests()
        {
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _listener.Listen(1);
            _peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _peer.Connect((IPEndPoint)_listener.LocalEndPoint);
            _serverSide = _listener.Accept();
            _peer.ReceiveTimeout = 3000;
        }

        public void Dispose()
        {
            _peer.Dispose();
            _serverSide.Dispose();
            _listener.Dispose();
        }

        private Client StartClient(TelnetLimits limits, List<string> lines = null)
        {
            var client = new Client(_serverSide, limits, () => _now);
            if (lines != null)
            {
                client.DataReceived += (s, l) => { lock (lines) { lines.Add(l); } };
            }
            client.Read();
            return client;
        }

        private string ReadUntilClosed()
        {
            var sb = new StringBuilder();
            var buf = new byte[1024];
            try
            {
                int n;
                while ((n = _peer.Receive(buf)) > 0) { sb.Append(Encoding.ASCII.GetString(buf, 0, n)); }
            }
            catch (SocketException) { }
            return sb.ToString();
        }

        private byte[] ReadBytes(int count)
        {
            var result = new List<byte>();
            var buf = new byte[256];
            while (result.Count < count)
            {
                int n = _peer.Receive(buf);
                if (n == 0) { break; }
                for (int i = 0; i < n; i++) { result.Add(buf[i]); }
            }
            return result.ToArray();
        }

        private static void WaitFor(Func<bool> condition)
        {
            for (int i = 0; i < 300 && !condition(); i++) { Thread.Sleep(10); }
        }

        [Fact]
        public void OverlongLine_SendsMessageAndDisconnects_WithoutDispatching()
        {
            var lines = new List<string>();
            var client = StartClient(new TelnetLimits(), lines);
            int disconnects = 0;
            client.ClientDisconnected += (s, c) => Interlocked.Increment(ref disconnects);

            _peer.Send(Encoding.ASCII.GetBytes(new string('a', 1025) + "\r\n"));

            string reply = ReadUntilClosed();
            Assert.Contains("too long", reply);
            Assert.Empty(lines);
            WaitFor(() => disconnects > 0);
            Assert.Equal(1, disconnects);
        }

        [Fact]
        public void UnterminatedFlood_IsCutOffAtLimit()
        {
            var lines = new List<string>();
            var client = StartClient(new TelnetLimits { MaxLineLength = 100 }, lines);

            _peer.Send(Encoding.ASCII.GetBytes(new string('a', 101)));

            Assert.Contains("too long", ReadUntilClosed());
            Assert.Empty(lines);
        }

        [Fact]
        public void LineAtExactLimit_IsAccepted()
        {
            var lines = new List<string>();
            StartClient(new TelnetLimits { MaxLineLength = 50 }, lines);

            _peer.Send(Encoding.ASCII.GetBytes(new string('a', 50) + "\r\n"));

            WaitFor(() => { lock (lines) { return lines.Count == 1; } });
            Assert.Single(lines);
            Assert.Equal(50, lines[0].Length);
        }

        [Fact]
        public void TooManyLinesInWindow_Disconnects()
        {
            var lines = new List<string>();
            StartClient(new TelnetLimits(), lines);

            var sb = new StringBuilder();
            for (int i = 0; i < 21; i++) { sb.Append("look\r\n"); }
            _peer.Send(Encoding.ASCII.GetBytes(sb.ToString()));

            Assert.Contains("too quickly", ReadUntilClosed());
            Assert.Equal(20, lines.Count);
        }

        [Fact]
        public void LinesSpreadOverWindow_AreNotLimited()
        {
            var lines = new List<string>();
            StartClient(new TelnetLimits(), lines);

            for (int i = 0; i < 25; i++)
            {
                _now = _now.AddSeconds(1);
                _peer.Send(Encoding.ASCII.GetBytes("look\r\n"));
                int expected = i + 1;
                WaitFor(() => { lock (lines) { return lines.Count == expected; } });
            }
            Assert.Equal(25, lines.Count);
            Assert.True(_peer.Connected);
        }

        [Fact]
        public void IdlePreLogin_TimesOutAfterTwoMinutes_ButNotBefore()
        {
            var client = StartClient(new TelnetLimits());

            Assert.False(client.CheckIdle(_now.AddSeconds(119)));
            Assert.True(client.CheckIdle(_now.AddSeconds(121)));
            Assert.Contains("idle", ReadUntilClosed());
        }

        [Fact]
        public void IdleLoggedIn_UsesThirtyMinutes()
        {
            var client = StartClient(new TelnetLimits());
            client.IsLoggedIn = true;

            Assert.False(client.CheckIdle(_now.AddMinutes(29)));
            Assert.True(client.CheckIdle(_now.AddMinutes(31)));
            Assert.Contains("idle", ReadUntilClosed());
        }

        [Fact]
        public void ActivityResetsIdleTimer()
        {
            var lines = new List<string>();
            var client = StartClient(new TelnetLimits(), lines);

            _now = _now.AddSeconds(100);
            _peer.Send(Encoding.ASCII.GetBytes("hi\r\n"));
            WaitFor(() => { lock (lines) { return lines.Count == 1; } });

            Assert.False(client.CheckIdle(_now.AddSeconds(100)));
            Assert.True(client.CheckIdle(_now.AddSeconds(121)));
        }

        [Fact]
        public void UnsupportedDo_GetsWontOnce_AndWillGetsDont_AndWontDontAreNotAnswered()
        {
            var lines = new List<string>();
            StartClient(new TelnetLimits(), lines);

            _peer.Send(new byte[]
            {
                255, 253, 31,   // DO NAWS (unsupported) -> WONT NAWS
                255, 253, 31,   // repeated -> no second reply
                255, 251, 32,   // WILL terminal speed (unsupported) -> DONT
                255, 252, 5,    // WONT -> no reply
                255, 254, 6,    // DONT -> no reply
            });
            _peer.Send(Encoding.ASCII.GetBytes("ping\r\n"));

            WaitFor(() => { lock (lines) { return lines.Count == 1; } });
            Assert.Equal("ping", lines[0]);

            // Nothing more than the two refusals should have been sent.
            byte[] reply = ReadBytes(6);
            Assert.Equal(new byte[] { 255, 252, 31, 255, 254, 32 }, reply);
            _peer.ReceiveTimeout = 300;
            Assert.ThrowsAny<SocketException>(() => _peer.Receive(new byte[16]));
        }

        [Fact]
        public void WillTerminalType_StillRequestsTerminalType()
        {
            StartClient(new TelnetLimits());

            _peer.Send(new byte[] { 255, 251, 24 });

            Assert.Equal(new byte[] { 255, 250, 24, 1, 255, 240 }, ReadBytes(6));
        }

        [Fact]
        public void Disconnect_IsIdempotent_AndRaisesEventOnce()
        {
            var client = StartClient(new TelnetLimits());
            int disconnects = 0;
            client.ClientDisconnected += (s, c) => disconnects++;

            client.Disconnect();
            client.Disconnect();

            Assert.Equal(1, disconnects);
        }
    }
}
