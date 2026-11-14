using System;
using System.Collections.Generic;
using MUD.Core;
using MUD.CoreApi;
using MUD.Telnet;
using Xunit;

namespace MUD.Tests
{
    public class LoginSessionTests
    {
        private class FakeConnection : ILoginConnection
        {
            public string RemoteKey { get; set; } = "1.2.3.4";
            public List<string> Events = new List<string>();
            public bool EchoHidden;
            public string Disconnected;

            public void Send(string text) { Events.Add("send:" + text); }
            public void SendPrompt(string text) { Events.Add("prompt:" + text); }
            public void SetEchoHidden(bool hidden) { EchoHidden = hidden; Events.Add(hidden ? "echo-off" : "echo-on"); }
            public void DisconnectWithMessage(string message) { Disconnected = message; }
            public void Disconnect(bool flush) { }
        }

        // A fake core: records the calls the login dialogue makes and answers them from the flags. The throttle is core's
        // own class, so the dialogue's use of IsThrottled and RecordLoginOutcome is tested against the real rules.
        private class FakeHandler : ICoreApi
        {
            public bool LoginResult, CreateResult;
            public List<string> Calls = new List<string>();
            public LoginThrottle Throttle;

            public LoginResult Authenticate(SessionId id, string name, string password)
            {
                Calls.Add("login:" + name + ":" + password);
                return LoginResult ? MUD.CoreApi.LoginResult.Success : MUD.CoreApi.LoginResult.BadCredentials;
            }

            public LoginResult CreatePlayer(SessionId id, string name, string password)
            {
                Calls.Add("create:" + name + ":" + password);
                return CreateResult ? MUD.CoreApi.LoginResult.Success : MUD.CoreApi.LoginResult.NameTaken;
            }

            public bool IsThrottled(string remoteKey) { return Throttle != null && Throttle.IsBlocked(remoteKey); }

            public void RecordLoginOutcome(string remoteKey, bool success)
            {
                if (Throttle == null) { return; }
                if (success) { Throttle.RecordSuccess(remoteKey); } else { Throttle.RecordFailure(remoteKey); }
            }

            public IReadOnlyList<string> GetOnlineNames() { return new List<string>(); }
            public SessionId OpenSession(string frontEnd, string remoteKey, IFrontEndSink sink) { return new SessionId("fake"); }
            public void SubmitLine(SessionId id, string rawLine) { }
            public void CloseSession(SessionId id, SessionEndReason reason) { }
        }

        private static LoginSession Make(out FakeConnection conn, out FakeHandler handler, LoginThrottle throttle = null)
        {
            conn = new FakeConnection();
            handler = new FakeHandler { Throttle = throttle };
            return new LoginSession(conn, handler, new SessionId("fake"));
        }

        [Fact]
        public void NonLoginLine_IsNotConsumed()
        {
            var s = Make(out var c, out var h);
            Assert.False(s.HandleLine("who"));
            Assert.False(s.HandleLine(""));
            Assert.Equal(LoginState.Idle, s.State);
        }

        [Theory]
        [InlineData("connect")]
        [InlineData("login")]
        public void Connect_PromptsNameThenPassword_WithEchoOffOnlyForPassword(string verb)
        {
            var s = Make(out var c, out var h);
            h.LoginResult = true;

            Assert.True(s.HandleLine(verb));
            Assert.Equal(LoginState.AwaitingName, s.State);
            Assert.Equal(new[] { "prompt:Name: " }, c.Events);
            Assert.False(c.EchoHidden);

            s.HandleLine("Alice");
            Assert.Equal(LoginState.AwaitingPassword, s.State);
            Assert.True(c.EchoHidden);
            Assert.Equal("prompt:Password: ", c.Events[c.Events.Count - 1]);

            s.HandleLine("secret");
            Assert.False(c.EchoHidden); // echo restored before the world is called
            Assert.Equal(new[] { "login:Alice:secret" }, h.Calls);
            Assert.True(s.IsLoggedIn);
            Assert.Equal(LoginState.Idle, s.State);
        }

        [Fact]
        public void Connect_WrongPassword_ReportsFailure_AndReturnsToIdle()
        {
            var s = Make(out var c, out var h);
            s.HandleLine("connect");
            s.HandleLine("Alice");
            s.HandleLine("bad");
            Assert.False(s.IsLoggedIn);
            Assert.Equal(LoginState.Idle, s.State);
            Assert.Contains("send:Login failed for Alice.", c.Events);
            Assert.False(c.EchoHidden);
        }

        [Fact]
        public void EmptyName_CancelsPrompt()
        {
            var s = Make(out var c, out var h);
            s.HandleLine("connect");
            s.HandleLine("");
            Assert.Equal(LoginState.Idle, s.State);
            Assert.Empty(h.Calls);
        }

        [Fact]
        public void Create_PromptsNamePasswordConfirm_AndCreates()
        {
            var s = Make(out var c, out var h);
            h.CreateResult = true;

            s.HandleLine("create");
            s.HandleLine("Bob");
            Assert.Equal(LoginState.AwaitingPassword, s.State);
            s.HandleLine("pw1");
            Assert.Equal(LoginState.AwaitingConfirm, s.State);
            Assert.True(c.EchoHidden); // still hidden for the confirmation
            Assert.Equal("prompt:Confirm password: ", c.Events[c.Events.Count - 1]);
            s.HandleLine("pw1");

            Assert.False(c.EchoHidden);
            Assert.Equal(new[] { "create:Bob:pw1" }, h.Calls);
            Assert.True(s.IsLoggedIn);
        }

        [Fact]
        public void Create_MismatchedConfirmation_DoesNotCreate()
        {
            var s = Make(out var c, out var h);
            h.CreateResult = true;
            s.HandleLine("create");
            s.HandleLine("Bob");
            s.HandleLine("pw1");
            s.HandleLine("pw2");
            Assert.Empty(h.Calls);
            Assert.False(s.IsLoggedIn);
            Assert.Equal(LoginState.Idle, s.State);
            Assert.False(c.EchoHidden);
        }

        [Fact]
        public void Create_NameTaken_Reports()
        {
            var s = Make(out var c, out var h);
            s.HandleLine("create");
            s.HandleLine("Bob");
            s.HandleLine("pw");
            s.HandleLine("pw");
            Assert.False(s.IsLoggedIn);
            Assert.Contains("send:There is already someone by the name of Bob.", c.Events);
        }

        [Fact]
        public void Echo_IsOffOnlyBetweenPasswordPromptAndReply()
        {
            var s = Make(out var c, out var h);
            s.HandleLine("connect");
            s.HandleLine("Alice");
            s.HandleLine("pw");
            int off = c.Events.IndexOf("echo-off");
            int on = c.Events.IndexOf("echo-on");
            Assert.True(off >= 0 && on > off);
            Assert.True(c.Events.IndexOf("prompt:Password: ") > off);
        }

        [Fact]
        public void LegacyForms_StillWork_AndPrintNotice()
        {
            var s = Make(out var c, out var h);
            h.LoginResult = true;
            Assert.True(s.HandleLine("connect Alice secret"));
            Assert.Equal(new[] { "login:Alice:secret" }, h.Calls);
            Assert.Contains("send:" + LoginSession.LegacyNotice, c.Events);
            Assert.True(s.IsLoggedIn);
            Assert.DoesNotContain("echo-off", c.Events);

            var s2 = Make(out var c2, out var h2);
            h2.CreateResult = true;
            Assert.True(s2.HandleLine("create Bob pw"));
            Assert.Equal(new[] { "create:Bob:pw" }, h2.Calls);
            Assert.Contains("send:" + LoginSession.LegacyNotice, c2.Events);
            Assert.True(s2.IsLoggedIn);
        }

        [Fact]
        public void BadArgumentCount_ShowsFormat_AndDoesNotCallWorld()
        {
            var s = Make(out var c, out var h);
            Assert.True(s.HandleLine("connect a b c"));
            Assert.Empty(h.Calls);
            Assert.Equal(LoginState.Idle, s.State);
        }

        [Fact]
        public void FailedLogins_FeedThrottle_AndBlockedClientIsDisconnected()
        {
            var throttle = new LoginThrottle(2, TimeSpan.FromMinutes(5));
            var s = Make(out var c, out var h, throttle);
            s.HandleLine("connect Alice x");
            s.HandleLine("connect Alice x");
            Assert.Null(c.Disconnected);
            s.HandleLine("connect Alice x");
            Assert.NotNull(c.Disconnected);
            Assert.Equal(2, h.Calls.Count); // blocked attempt never reached the world
        }

        [Fact]
        public void Passwords_AreNeverInLoggableText()
        {
            var s = Make(out var c, out var h);
            Assert.DoesNotContain("hunter2", s.Loggable("connect Alice hunter2"));
            Assert.Equal("connect", s.Loggable("connect"));
            s.HandleLine("connect");
            s.HandleLine("Alice");
            Assert.DoesNotContain("hunter2", s.Loggable("hunter2"));
        }
    }
}
