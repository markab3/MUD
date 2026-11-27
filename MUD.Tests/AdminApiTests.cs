using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core.Administration;
using MUD.Core.GameObjects;
using MUD.Core.Modules;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;
using Xunit;

namespace MUD.Tests
{
    /// <summary>Card P6-1: the Admin service logic. No database: fake repositories, a fake audit store and a host that runs work inline.</summary>
    [Collection("World construction")] // Room and Player serialisation uses the process-wide BSON class maps
    public class AdminApiTests
    {
        private class FakeHost : IAdminHost
        {
            public bool GameThreadAvailable = true;
            public string StartRoomKey { get; set; } = "t.start";
            public HashSet<string> Throttled = new HashSet<string>();
            public List<KeyValuePair<string, bool>> Outcomes = new List<KeyValuePair<string, bool>>();
            public HashSet<Player> Online = new HashSet<Player>();

            public bool TryRunOnGameThread<T>(Func<T> work, out T result)
            {
                if (!GameThreadAvailable) { result = default(T); return false; }
                result = work();
                return true;
            }

            public bool IsThrottled(string remoteKey) { return Throttled.Contains(remoteKey); }
            public void RecordLoginOutcome(string remoteKey, bool success) { Outcomes.Add(new KeyValuePair<string, bool>(remoteKey, success)); }
            public bool IsOnline(Player player) { return Online.Contains(player); }
        }

        private class FakeAudit : IAuditStore
        {
            public List<AuditRecord> Rows = new List<AuditRecord>();
            public Dictionary<string, bool> Outcomes = new Dictionary<string, bool>();
            public bool FailInsert;

            public void Insert(IReadOnlyList<AuditRecord> rows)
            {
                if (FailInsert) { throw new InvalidOperationException("audit down"); }
                Rows.AddRange(rows);
            }

            public void SetOutcome(IReadOnlyList<string> ids, bool applied)
            {
                foreach (string id in ids)
                {
                    Outcomes[id] = applied;
                    AuditRecord row = Rows.FirstOrDefault(r => r.Id == id);
                    if (row != null) { row.Applied = applied; row.Rejected = !applied; }
                }
            }

            public AuditPage Query(string entity, string entityId, int page, int pageSize)
            {
                List<AuditRecord> matching = Rows.Where(r => (string.IsNullOrEmpty(entity) || r.Entity == entity) && (string.IsNullOrEmpty(entityId) || r.EntityId == entityId)).ToList();
                return new AuditPage { Items = matching.Skip((page - 1) * pageSize).Take(pageSize).ToList(), Total = matching.Count };
            }
        }

        private class FakeQueries : IAdminQueries
        {
            public SyncPlayerRepo Players;
            public SyncRoomRepo Rooms;

            public List<BsonDocument> ListPlayers(string nameFilter, bool descending, int page, int pageSize, out int total)
            {
                IEnumerable<BsonDocument> all = Players.Stored.Values.Where(d => string.IsNullOrEmpty(nameFilter) || d["PlayerName"].AsString.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0);
                List<BsonDocument> sorted = (descending ? all.OrderByDescending(d => d["NameKey"].AsString) : all.OrderBy(d => d["NameKey"].AsString)).ToList();
                total = sorted.Count;
                return sorted.Skip((page - 1) * pageSize).Take(pageSize).Select(Strip).ToList();
            }

            // Like the real projection: never the password.
            private static BsonDocument Strip(BsonDocument document)
            {
                BsonDocument copy = document.DeepClone().AsBsonDocument;
                copy.Remove("Password");
                return copy;
            }

            public BsonDocument FindPlayer(string nameKey)
            {
                return Players.Stored.Values.Where(d => d["NameKey"].AsString == nameKey).Select(Strip).FirstOrDefault();
            }

            public int CountAdmins()
            {
                return Players.Stored.Values.Count(d => d.Contains("roles") && d["roles"].AsBsonDocument.Contains("admin") && d["roles"]["admin"].AsBoolean);
            }

            public List<BsonDocument> ListRooms() { return Rooms.Stored.Select(p => { var d = p.Value.DeepClone().AsBsonDocument; d["_id"] = p.Key; return d; }).ToList(); }

            public List<string> RoomIdsWithExitTo(string key)
            {
                return Rooms.Stored.Where(p => p.Value.Contains("Exits") && p.Value["Exits"].AsBsonArray.Any(e => e["DestinationId"].AsString == key)).Select(p => p.Key).ToList();
            }
        }

        // Stands in for the supervisor: it records what was queued and changes nothing by itself.
        private class FakeModules : IModuleControl
        {
            public List<ModuleStatus> Statuses = new List<ModuleStatus>();
            public List<string> Starts = new List<string>();
            public List<string> Stops = new List<string>();
            public List<string> Restarts = new List<string>();
            public bool Running = true;

            public FakeModules Add(string name, ModuleState state, bool required = false, string problem = null, string detail = "", string version = "1.0")
            {
                Statuses.Add(new ModuleStatus { Name = name, State = state, Required = required, Problem = problem, Detail = detail, Version = version });
                return this;
            }

            public IReadOnlyList<ModuleStatus> GetStatuses() { return Running ? Statuses : null; }
            public string RequestStart(string name) { if (!Running) { return null; } Starts.Add(name); return "queued"; }
            public string RequestStop(string name) { if (!Running) { return null; } Stops.Add(name); return "queued"; }
            public string RequestRestart(string name) { if (!Running) { return null; } Restarts.Add(name); return "queued"; }
        }

        private class Person : Player
        {
            public List<string> Messages = new List<string>();
            public int Quits;

            public Person(string name, IPlayerRepository repository) : base(null, null, repository)
            {
                Id = "p." + name.ToLowerInvariant();
                PlayerName = name;
                NameKey = name.ToLowerInvariant();
                ShortDescription = name;
                Password = "secret1";
                Race = "human";
                Gender = "female";
                Class = "fighter";
            }

            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        private class Fixture
        {
            public FakeHost Host = new FakeHost();
            public FakeAudit Audit = new FakeAudit();
            public SyncPlayerRepo Players = new SyncPlayerRepo();
            public SyncRoomRepo Rooms = new SyncRoomRepo();
            public AdminSessions Sessions = new AdminSessions();
            public List<string> Log = new List<string>();
            public FakeModules Modules = new FakeModules();
            public AdminApi Api;

            public Fixture()
            {
                var queries = new FakeQueries { Players = Players, Rooms = Rooms };
                Api = new AdminApi(Host, Players, Rooms, queries, Audit, Sessions, new FakeClock(), Log.Add, max => 0, Modules);
                AddRoom("t.start", "t.hall");
                AddRoom("t.hall", "t.start");
            }

            public Person AddPlayer(string name, bool creator = false, bool admin = false, bool retired = false)
            {
                var person = new Person(name, Players) { Retired = retired };
                person.Roles.Creator = creator || admin;
                person.Roles.Admin = admin;
                Players.Live.Add(person);
                Players.Stored[person.Id] = person.ToBsonDocument();
                return person;
            }

            public Room AddRoom(string key, params string[] exitsTo)
            {
                var room = new Room(Rooms)
                {
                    Id = key,
                    ShortDescription = "room " + key,
                    LongDescription = "long " + key,
                    Area = key.Substring(0, key.IndexOf('.')),
                    Exits = exitsTo.Select((to, i) => new Exit { Name = "exit" + new string('a', i + 1), DestinationId = to }).ToList()
                };
                Rooms.Cache[key] = room;
                Rooms.Stored[key] = room.ToBsonDocument();
                return room;
            }

            public string Login(string name, string password = "secret1")
            {
                WebAuthReply reply = Api.WebAuthenticate(new WebAuthRequest { Name = name, Password = password, RemoteKey = "1.2.3.4" });
                Assert.Equal(AuthResult.AuthOk, reply.Result);
                return reply.Token;
            }

            public static RoomEditRequest Edit(string token, string key, string shortText = "a new short", string longText = "a new long", string area = "t", params string[] exits)
            {
                var request = new RoomEditRequest { Token = token, Key = key, ShortDescription = shortText, LongDescription = longText, Area = area };
                for (int i = 0; i + 1 < exits.Length; i += 2) { request.Exits.Add(new RoomExit { Name = exits[i], To = exits[i + 1] }); }
                return request;
            }
        }

        private static AdminCallException Refused(Action call)
        {
            return Assert.Throws<AdminCallException>(call);
        }

        // ------------------------------------------------------------------ session table

        [Fact]
        public void TokensAreOpaqueRandomAndNeverReused()
        {
            var sessions = new AdminSessions();
            var seen = new HashSet<string>();
            for (int i = 0; i < 200; i++)
            {
                AdminSession session = sessions.Issue("p.ann", "ann");
                Assert.True(session.Token.Length >= 40);
                Assert.Matches("^[A-Za-z0-9_-]+$", session.Token);
                Assert.True(seen.Add(session.Token));
            }
            Assert.Equal(200, sessions.Count);
        }

        [Fact]
        public void AnEmptyUnknownOrRevokedTokenFindsNothing()
        {
            var sessions = new AdminSessions();
            AdminSession session = sessions.Issue("p.ann", "ann");
            Assert.Null(sessions.Find(null));
            Assert.Null(sessions.Find(string.Empty));
            Assert.Null(sessions.Find("nope"));
            Assert.Same(session, sessions.Find(session.Token));
            Assert.True(sessions.Revoke(session.Token));
            Assert.False(sessions.Revoke(session.Token));
            Assert.Null(sessions.Find(session.Token));
        }

        [Fact]
        public void RevokeAllForEndsEverySessionOfOnePlayerOnly()
        {
            var sessions = new AdminSessions();
            AdminSession a1 = sessions.Issue("p.ann", "ann"), a2 = sessions.Issue("p.ann", "ann"), bob = sessions.Issue("p.bob", "bob");
            Assert.Equal(2, sessions.RevokeAllFor("p.ann"));
            Assert.Null(sessions.Find(a1.Token));
            Assert.Null(sessions.Find(a2.Token));
            Assert.Same(bob, sessions.Find(bob.Token));
        }

        // ------------------------------------------------------------------ WebAuthenticate / Logout

        [Fact]
        public void AGoodLoginReturnsATokenAndTheRoleAndClearsTheThrottle()
        {
            var f = new Fixture();
            f.AddPlayer("Ann", creator: true);
            WebAuthReply reply = f.Api.WebAuthenticate(new WebAuthRequest { Name = "  ANN ", Password = "secret1", RemoteKey = "9.9.9.9" });
            Assert.Equal(AuthResult.AuthOk, reply.Result);
            Assert.False(string.IsNullOrEmpty(reply.Token));
            Assert.Equal("Ann", reply.PlayerName);
            Assert.Equal(AdminRole.Creator, reply.Role);
            Assert.Equal(new[] { new KeyValuePair<string, bool>("9.9.9.9", true) }, f.Host.Outcomes);
            Assert.Equal(1, f.Sessions.Count);
        }

        [Theory]
        [InlineData("Ann", "wrong")]
        [InlineData("Nobody", "secret1")]
        [InlineData("", "secret1")]
        [InlineData("Ann", "")]
        public void ABadLoginIsRefusedWithoutATokenAndCountsAsAFailure(string name, string password)
        {
            var f = new Fixture();
            f.AddPlayer("Ann");
            WebAuthReply reply = f.Api.WebAuthenticate(new WebAuthRequest { Name = name, Password = password, RemoteKey = "k" });
            Assert.Equal(AuthResult.AuthBadCredentials, reply.Result);
            Assert.Equal(string.Empty, reply.Token);
            Assert.Equal(0, f.Sessions.Count);
            Assert.False(f.Host.Outcomes.Single().Value);
        }

        [Fact]
        public void ARetiredCharacterWithTheRightPasswordIsToldSoAndGetsNoToken()
        {
            var f = new Fixture();
            f.AddPlayer("Ann", retired: true);
            WebAuthReply reply = f.Api.WebAuthenticate(new WebAuthRequest { Name = "ann", Password = "secret1", RemoteKey = "k" });
            Assert.Equal(AuthResult.AuthRetired, reply.Result);
            Assert.Equal(string.Empty, reply.Token);
            Assert.Equal(0, f.Sessions.Count);
        }

        [Fact]
        public void ARetiredCharacterWithTheWrongPasswordLooksLikeAnyBadLogin()
        {
            var f = new Fixture();
            f.AddPlayer("Ann", retired: true);
            WebAuthReply reply = f.Api.WebAuthenticate(new WebAuthRequest { Name = "ann", Password = "wrong", RemoteKey = "k" });
            Assert.Equal(AuthResult.AuthBadCredentials, reply.Result);
        }

        [Fact]
        public void AThrottledKeyIsRefusedBeforeAnythingIsChecked()
        {
            var f = new Fixture();
            f.AddPlayer("Ann");
            f.Host.Throttled.Add("bad.key");
            WebAuthReply reply = f.Api.WebAuthenticate(new WebAuthRequest { Name = "ann", Password = "secret1", RemoteKey = "bad.key" });
            Assert.Equal(AuthResult.AuthThrottled, reply.Result);
            Assert.Empty(f.Host.Outcomes);
            Assert.Equal(0, f.Sessions.Count);
        }

        [Fact]
        public void AGameThreadThatDoesNotRunTheLoginAnswersTryAgainLater()
        {
            var f = new Fixture();
            f.AddPlayer("Ann");
            f.Host.GameThreadAvailable = false;
            WebAuthReply reply = f.Api.WebAuthenticate(new WebAuthRequest { Name = "ann", Password = "secret1", RemoteKey = "k" });
            Assert.Equal(AuthResult.AuthTryAgainLater, reply.Result);
            Assert.Equal(0, f.Sessions.Count);
            Assert.Empty(f.Host.Outcomes);
        }

        [Fact]
        public void LogoutEndsTheSessionAndAlsoSucceedsForATokenThatIsAlreadyGone()
        {
            var f = new Fixture();
            f.AddPlayer("Ann", creator: true);
            string token = f.Login("ann");
            Assert.True(f.Api.Logout(new TokenRequest { Token = token }).Ok);
            Assert.Equal(AdminFailure.Unauthenticated, Refused(() => f.Api.ListRooms(new TokenRequest { Token = token })).Failure);
            Assert.True(f.Api.Logout(new TokenRequest { Token = token }).Ok);
            Assert.True(f.Api.Logout(new TokenRequest { Token = "never issued" }).Ok);
        }

        [Fact]
        public void NothingASessionDoesEverLogsAPasswordOrAToken()
        {
            var f = new Fixture();
            f.AddPlayer("Ann", creator: true);
            string token = f.Login("ann");
            f.Api.WebAuthenticate(new WebAuthRequest { Name = "ann", Password = "wrongpass9", RemoteKey = "k" });
            f.Api.ChangePassword(new ChangePasswordRequest { Token = token, CurrentPassword = "secret1", NewPassword = "brandnew7", ConfirmPassword = "brandnew7" });
            f.Audit.FailInsert = true;
            f.Api.UpdateRoom(Fixture.Edit(token, "t.start"));
            string everything = string.Join("\n", f.Log) + string.Join("\n", f.Audit.Rows.Select(r => r.Old + " " + r.New));
            Assert.DoesNotContain("secret1", everything);
            Assert.DoesNotContain("wrongpass9", everything);
            Assert.DoesNotContain("brandnew7", everything);
            Assert.DoesNotContain(token, everything);
        }

        // ------------------------------------------------------------------ per-call role re-check and revocation (D31)

        [Fact]
        public void APlayerTokenIsRefusedForCreatorCallsAndACreatorTokenForAdminCalls()
        {
            var f = new Fixture();
            f.AddPlayer("Pat");
            f.AddPlayer("Cris", creator: true);
            string patToken = f.Login("pat"), crisToken = f.Login("cris");
            Assert.Equal(AdminFailure.Forbidden, Refused(() => f.Api.ListRooms(new TokenRequest { Token = patToken })).Failure);
            Assert.Equal(AdminFailure.Forbidden, Refused(() => f.Api.ListPlayers(new ListPlayersRequest { Token = patToken })).Failure);
            Assert.Equal(AdminFailure.Forbidden, Refused(() => f.Api.UpdateRoom(Fixture.Edit(patToken, "t.start"))).Failure);
            Assert.Equal(AdminFailure.Forbidden, Refused(() => f.Api.ListAudit(new ListAuditRequest { Token = crisToken })).Failure);
            Assert.Equal(2, f.Api.ListRooms(new TokenRequest { Token = crisToken }).Rooms.Count);
            Assert.Equal("Pat", f.Api.GetSession(new TokenRequest { Token = patToken }).PlayerName);
        }

        [Fact]
        public void ADemotionTakesEffectOnTheNextCallWithTheSameToken()
        {
            var f = new Fixture();
            Person cris = f.AddPlayer("Cris", creator: true);
            string token = f.Login("cris");
            Assert.Equal(AdminRole.Creator, f.Api.GetSession(new TokenRequest { Token = token }).Role);
            cris.Roles.Creator = false; // however the role is lowered (web edit, in-game demotion)
            Assert.Equal(AdminFailure.Forbidden, Refused(() => f.Api.ListRooms(new TokenRequest { Token = token })).Failure);
            Assert.Equal(AdminFailure.Forbidden, Refused(() => f.Api.UpdateRoom(Fixture.Edit(token, "t.start"))).Failure);
            Assert.Equal(AdminRole.Player, f.Api.GetSession(new TokenRequest { Token = token }).Role);
        }

        [Fact]
        public void APromotionAlsoTakesEffectOnTheNextCall()
        {
            var f = new Fixture();
            Person pat = f.AddPlayer("Pat");
            string token = f.Login("pat");
            Assert.Equal(AdminFailure.Forbidden, Refused(() => f.Api.ListRooms(new TokenRequest { Token = token })).Failure);
            pat.Roles.Creator = true;
            Assert.Equal(2, f.Api.ListRooms(new TokenRequest { Token = token }).Rooms.Count);
        }

        [Fact]
        public void ARetirementEndsTheSessionAtOnce()
        {
            var f = new Fixture();
            Person cris = f.AddPlayer("Cris", creator: true);
            string token = f.Login("cris");
            cris.Retired = true;
            Assert.Equal(AdminFailure.Unauthenticated, Refused(() => f.Api.ListRooms(new TokenRequest { Token = token })).Failure);
            Assert.Null(f.Sessions.Find(token)); // and the dead session is gone from the table
        }

        [Fact]
        public void ASessionWhosePlayerNoLongerExistsIsRefused()
        {
            var f = new Fixture();
            Person cris = f.AddPlayer("Cris", creator: true);
            string token = f.Login("cris");
            f.Players.Live.Remove(cris);
            Assert.Equal(AdminFailure.Unauthenticated, Refused(() => f.Api.GetSession(new TokenRequest { Token = token })).Failure);
        }

        [Fact]
        public void AMutatingCallIsRefusedWhenTheGameThreadDoesNotRunIt()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            string token = f.Login("cris");
            f.Host.GameThreadAvailable = false;
            Assert.Equal(AdminFailure.Unavailable, Refused(() => f.Api.UpdateRoom(Fixture.Edit(token, "t.start"))).Failure);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void ARetirementDoneThroughTheServiceRevokesEverySessionOfThatPlayerAndNobodyElses()
        {
            var f = new Fixture();
            f.AddPlayer("Adam", admin: true);
            f.AddPlayer("Cris", creator: true);
            string adam = f.Login("adam"), cris1 = f.Login("cris"), cris2 = f.Login("cris");
            UpdatePlayerReply reply = f.Api.UpdatePlayer(PlayerEdit(adam, "cris", retired: true));
            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Null(f.Sessions.Find(cris1));
            Assert.Null(f.Sessions.Find(cris2));
            Assert.NotNull(f.Sessions.Find(adam));
            Assert.Equal(AdminFailure.Unauthenticated, Refused(() => f.Api.ListRooms(new TokenRequest { Token = cris1 })).Failure);
        }

        // ------------------------------------------------------------------ change password

        [Fact]
        public void AnyPlayerCanChangeTheirOwnPasswordWithTheCurrentOne()
        {
            var f = new Fixture();
            Person pat = f.AddPlayer("Pat");
            string token = f.Login("pat");
            ChangePasswordReply reply = f.Api.ChangePassword(new ChangePasswordRequest { Token = token, CurrentPassword = "secret1", NewPassword = "another9", ConfirmPassword = "another9" });
            Assert.Equal(PasswordChangeOutcome.Changed, reply.Outcome);
            Assert.Equal("another9", pat.Password);
            Assert.Equal("another9", f.Players.Stored[pat.Id]["Password"].AsString);
        }

        [Theory]
        [InlineData("wrong", "another9", "another9", PasswordChangeOutcome.WrongCurrent)]
        [InlineData("secret1", "short", "short", PasswordChangeOutcome.Invalid)]
        [InlineData("secret1", "another9", "different", PasswordChangeOutcome.Mismatch)]
        [InlineData("secret1", "secret1", "secret1", PasswordChangeOutcome.Same)]
        public void AWrongOrUnacceptablePasswordChangeIsReportedAndChangesNothing(string current, string replacement, string confirm, PasswordChangeOutcome expected)
        {
            var f = new Fixture();
            Person pat = f.AddPlayer("Pat");
            string token = f.Login("pat");
            ChangePasswordReply reply = f.Api.ChangePassword(new ChangePasswordRequest { Token = token, CurrentPassword = current, NewPassword = replacement, ConfirmPassword = confirm });
            Assert.Equal(expected, reply.Outcome);
            Assert.Equal("secret1", pat.Password);
            Assert.DoesNotContain(replacement, reply.Message);
        }

        // ------------------------------------------------------------------ rooms

        [Fact]
        public void ListAndGetRoomReturnDefinitionFields()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            f.Rooms.Cache["t.hall"].X = 3;
            string token = f.Login("cris");
            GetRoomReply got = f.Api.GetRoom(new GetRoomRequest { Token = token, Key = "t.hall" });
            Assert.True(got.Found);
            Assert.Equal("room t.hall", got.Room.ShortDescription);
            Assert.True(got.Room.HasX);
            Assert.Equal(3, got.Room.X);
            Assert.False(got.Room.HasY); // unplaced stays unplaced, never 0
            Assert.Equal("t.start", got.Room.Exits.Single().To);
            Assert.False(f.Api.GetRoom(new GetRoomRequest { Token = token, Key = "t.nowhere" }).Found);
            Assert.Equal(new[] { "t.hall", "t.start" }, f.Api.ListRooms(new TokenRequest { Token = token }).Rooms.Select(r => r.Key).OrderBy(k => k).ToArray());
        }

        [Fact]
        public void UpdatingARoomChangesTheLiveRoomTheStoreAndWritesOneAuditRowPerChangedField()
        {
            var f = new Fixture();
            Person cris = f.AddPlayer("Cris", creator: true);
            var occupant = f.AddPlayer("Pat");
            f.Rooms.Cache["t.hall"].Occupants.Add(occupant);
            string token = f.Login("cris");
            RoomEditRequest edit = Fixture.Edit(token, "t.hall", "a grand hall", "A very long and grand hall.", "t", "north", "t.start");
            edit.X = 4;
            RoomEditReply reply = f.Api.UpdateRoom(edit);

            Assert.Equal(AdminResult.Ok, reply.Result);
            Room live = f.Rooms.Cache["t.hall"];
            Assert.Equal("a grand hall", live.ShortDescription);
            Assert.Equal(4, live.X);
            Assert.Null(live.Y);
            Assert.Equal("north", live.Exits.Single().Name);
            Assert.Equal("a grand hall", f.Rooms.Stored["t.hall"]["ShortDescription"].AsString);
            Assert.Equal("north", f.Rooms.Stored["t.hall"]["Exits"][0]["Name"].AsString);
            Assert.Contains("Something feels different...", occupant.Messages);

            Assert.Equal(new[] { "Exits", "LongDescription", "ShortDescription", "X" }, f.Audit.Rows.Select(r => r.Field).OrderBy(x => x).ToArray());
            Assert.All(f.Audit.Rows, r =>
            {
                Assert.Equal("room", r.Entity);
                Assert.Equal("t.hall", r.EntityId);
                Assert.Equal(cris.Id, r.ActorId);
                Assert.Equal("Cris", r.ActorName);
                Assert.Equal("Creator", r.ActorRole);
                Assert.Equal("web", r.Source);
                Assert.True(f.Audit.Outcomes[r.Id]);
            });
            AuditRecord shortRow = f.Audit.Rows.Single(r => r.Field == "ShortDescription");
            Assert.Equal("room t.hall", shortRow.Old.AsString);
            Assert.Equal("a grand hall", shortRow.New.AsString);
        }

        [Fact]
        public void TwoEditsInARowBothWinBecauseThereIsNoRevisionNumber()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            f.AddPlayer("Cleo", creator: true);
            string cris = f.Login("cris"), cleo = f.Login("cleo");
            Assert.Equal(AdminResult.Ok, f.Api.UpdateRoom(Fixture.Edit(cris, "t.hall", "first short", "first long", "t", "back", "t.start")).Result);
            Assert.Equal(AdminResult.Ok, f.Api.UpdateRoom(Fixture.Edit(cleo, "t.hall", "second short", "second long", "t", "back", "t.start")).Result);
            Assert.Equal("second short", f.Rooms.Cache["t.hall"].ShortDescription);
            Assert.Equal("second short", f.Rooms.Stored["t.hall"]["ShortDescription"].AsString);
            Assert.False(f.Rooms.Stored["t.hall"].Contains("Rev") && f.Rooms.Stored["t.hall"]["Rev"].AsInt32 != 0);
            // The history of both edits is in the audit.
            Assert.Equal(2, f.Audit.Rows.Count(r => r.Field == "ShortDescription"));
        }

        [Fact]
        public void AnUnchangedRoomIsNoChangeAndWritesNoAudit()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            string token = f.Login("cris");
            Room hall = f.Rooms.Cache["t.hall"];
            RoomEditRequest same = Fixture.Edit(token, "t.hall", hall.ShortDescription, hall.LongDescription, "t", hall.Exits[0].Name, "t.start");
            Assert.Equal(AdminResult.NoChange, f.Api.UpdateRoom(same).Result);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void InvalidRoomEditsAreRejectedByTheSharedRulesAndChangeNothing()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            string token = f.Login("cris");
            RoomEditReply reply = f.Api.UpdateRoom(Fixture.Edit(token, "t.hall", "", "ok long", "t"));
            Assert.Equal(AdminResult.Invalid, reply.Result);
            Assert.Contains(reply.Errors, e => e.Field == "Short");
            reply = f.Api.UpdateRoom(Fixture.Edit(token, "t.hall", "ok", "ok", "t", "bad dir1", "t.start"));
            Assert.Equal(AdminResult.Invalid, reply.Result);
            Assert.Contains(reply.Errors, e => e.Field == "Exits[0].Dir");
            reply = f.Api.UpdateRoom(Fixture.Edit(token, "t.hall", "ok", "ok", "wrong"));
            Assert.Equal(AdminResult.Invalid, reply.Result);
            Assert.Contains(reply.Errors, e => e.Field == "Area");
            Assert.Equal("room t.hall", f.Rooms.Cache["t.hall"].ShortDescription);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void AnExitToAMissingRoomNeedsConfirmation()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            string token = f.Login("cris");
            RoomEditRequest edit = Fixture.Edit(token, "t.hall", "ok", "ok", "t", "up", "t.missing");
            RoomEditReply reply = f.Api.UpdateRoom(edit);
            Assert.Equal(AdminResult.Invalid, reply.Result);
            Assert.Contains(reply.Errors, e => e.Field == "ConfirmDangling");
            edit.ConfirmDangling = true;
            Assert.Equal(AdminResult.Ok, f.Api.UpdateRoom(edit).Result);
        }

        [Fact]
        public void UpdatingAMissingRoomIsNotFound()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            string token = f.Login("cris");
            Assert.Equal(AdminResult.NotFound, f.Api.UpdateRoom(Fixture.Edit(token, "t.nowhere")).Result);
        }

        [Fact]
        public void IfTheAuditCannotBeWrittenTheEditIsRefusedAndNothingChanges()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            string token = f.Login("cris");
            f.Audit.FailInsert = true;
            RoomEditReply reply = f.Api.UpdateRoom(Fixture.Edit(token, "t.hall", "new short", "new long", "t", "back", "t.start"));
            Assert.Equal(AdminResult.AuditFailed, reply.Result);
            Assert.Equal("room t.hall", f.Rooms.Cache["t.hall"].ShortDescription);
            Assert.Equal("room t.hall", f.Rooms.Stored["t.hall"]["ShortDescription"].AsString);
        }

        [Fact]
        public void IfTheDatabaseRefusesTheWriteTheAuditRowsAreMarkedRejectedAndTheLiveRoomIsUntouched()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            string token = f.Login("cris");
            f.Rooms.Stored.Remove("t.hall"); // UpdateFields now reports no stored document
            RoomEditReply reply = f.Api.UpdateRoom(Fixture.Edit(token, "t.hall", "new short", "new long", "t", "back", "t.start"));
            Assert.Equal(AdminResult.Failed, reply.Result);
            Assert.Equal("room t.hall", f.Rooms.Cache["t.hall"].ShortDescription);
            Assert.NotEmpty(f.Audit.Rows);
            Assert.All(f.Audit.Rows, r => Assert.False(f.Audit.Outcomes[r.Id]));
        }

        [Fact]
        public void CreatingARoomStoresItCachesItAndAuditsEveryField()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            string token = f.Login("cris");
            RoomEditRequest create = Fixture.Edit(token, "t.cellar", "a cellar", "A damp cellar.", "t", "up", "t.hall");
            create.X = 0;
            RoomEditReply reply = f.Api.CreateRoom(create);
            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Equal("t.cellar", reply.Room.Key);
            Assert.NotNull(f.Rooms.Cache["t.cellar"]);
            Assert.Equal("a cellar", f.Rooms.Stored["t.cellar"]["ShortDescription"].AsString);
            Assert.Equal(7, f.Audit.Rows.Count(r => r.EntityId == "t.cellar"));
            Assert.Equal(0, f.Rooms.Cache["t.cellar"].X); // an explicit 0 is a placement, blank is not
            Assert.Null(f.Rooms.Cache["t.cellar"].Y);
        }

        [Fact]
        public void CreatingARoomNeedsAnAuthoredKeyAndRefusesAnExistingOne()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            string token = f.Login("cris");
            Assert.Equal(AdminResult.Invalid, f.Api.CreateRoom(Fixture.Edit(token, "cellar", "a cellar", "A cellar.", "")).Result);
            Assert.Equal(AdminResult.Invalid, f.Api.CreateRoom(Fixture.Edit(token, "t.Bad Key", "a cellar", "A cellar.", "t")).Result);
            Assert.Equal(AdminResult.AlreadyExists, f.Api.CreateRoom(Fixture.Edit(token, "t.hall", "x", "y", "t")).Result);
            Assert.False(f.Rooms.Cache.ContainsKey("cellar"));
        }

        [Fact]
        public void DeletingARoomMovesEveryoneToANeighbourStripsExitsToItAndAudits()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            Person pat = f.AddPlayer("Pat");
            Room cellar = f.AddRoom("t.cellar", "t.hall");
            f.Rooms.Cache["t.hall"].Exits.Add(new Exit { Name = "down", DestinationId = "t.cellar" });
            f.Rooms.Stored["t.hall"] = f.Rooms.Cache["t.hall"].ToBsonDocument();
            cellar.Occupants.Add(pat);
            pat.CurrentLocation = cellar;
            pat.CurrentLocationId = "t.cellar";
            string token = f.Login("cris");

            RoomEditReply reply = f.Api.DeleteRoom(new DeleteRoomRequest { Token = token, Key = "t.cellar" });

            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.False(f.Rooms.Stored.ContainsKey("t.cellar"));
            Assert.False(f.Rooms.Cache.ContainsKey("t.cellar"));
            Assert.Equal("t.hall", pat.CurrentLocationId);
            Assert.Contains(pat, f.Rooms.Cache["t.hall"].Occupants);
            Assert.Contains(pat.Messages, m => m.Contains("A sudden draft of magical wind deposits"));
            Assert.DoesNotContain(f.Rooms.Cache["t.hall"].Exits, e => e.DestinationId == "t.cellar");
            Assert.DoesNotContain(f.Rooms.Stored["t.hall"]["Exits"].AsBsonArray, e => e["DestinationId"].AsString == "t.cellar");
            Assert.Equal(7, f.Audit.Rows.Count(r => r.EntityId == "t.cellar"));
            Assert.Single(f.Audit.Rows.Where(r => r.EntityId == "t.hall" && r.Field == "Exits"));
            Assert.All(f.Audit.Rows, r => Assert.True(f.Audit.Outcomes[r.Id]));
        }

        [Fact]
        public void TheDefaultRoomIsNeverDeletedAndAMissingDefaultSettingRefusesDeletes()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            string token = f.Login("cris");
            RoomEditReply reply = f.Api.DeleteRoom(new DeleteRoomRequest { Token = token, Key = "t.start" });
            Assert.Equal(AdminResult.Refused, reply.Result);
            Assert.Equal(AdminApi.DefaultRoomCannotBeDeletedMessage, reply.Message);
            Assert.True(f.Rooms.Stored.ContainsKey("t.start"));

            f.Host.StartRoomKey = null;
            Assert.Equal(AdminResult.Refused, f.Api.DeleteRoom(new DeleteRoomRequest { Token = token, Key = "t.hall" }).Result);
            Assert.True(f.Rooms.Stored.ContainsKey("t.hall"));
        }

        [Fact]
        public void ARoomWithNowhereToSendItsContentsIsNotDeleted()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            f.Rooms.Cache.Remove("t.start");
            f.Rooms.Stored.Remove("t.start"); // no default room, and the island has no exit
            f.AddRoom("t.island");
            string token = f.Login("cris");
            RoomEditReply reply = f.Api.DeleteRoom(new DeleteRoomRequest { Token = token, Key = "t.island" });
            Assert.Equal(AdminResult.Refused, reply.Result);
            Assert.Equal(AdminApi.NothingCouldBeMovedMessage, reply.Message);
            Assert.True(f.Rooms.Stored.ContainsKey("t.island"));
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void DeletingAMissingRoomIsNotFound()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            string token = f.Login("cris");
            Assert.Equal(AdminResult.NotFound, f.Api.DeleteRoom(new DeleteRoomRequest { Token = token, Key = "t.nowhere" }).Result);
        }

        // ------------------------------------------------------------------ move

        [Fact]
        public void MovingARoomRekeysItAndRepointsExitsPlayersAndContents()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            f.AddRoom("t.cellar", "t.hall");
            f.Rooms.Cache["t.hall"].Exits.Add(new Exit { Name = "down", DestinationId = "t.cellar" });
            f.Rooms.Stored["t.hall"] = f.Rooms.Cache["t.hall"].ToBsonDocument();
            Person pat = f.AddPlayer("Pat");
            Room cellar = f.Rooms.Cache["t.cellar"];
            cellar.Occupants.Add(pat);
            pat.CurrentLocation = cellar;
            pat.CurrentLocationId = "t.cellar";
            Person away = f.AddPlayer("Away");
            away.CurrentLocationId = "t.cellar";
            string token = f.Login("cris");

            RoomEditReply reply = f.Api.MoveRoom(new MoveRoomRequest { Token = token, Key = "t.cellar", NewArea = "u", NewLocalKey = "vault" });

            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Equal("u.vault", reply.Room.Key);
            Assert.Equal("u", reply.Room.Area);
            Assert.False(f.Rooms.Stored.ContainsKey("t.cellar"));
            Assert.True(f.Rooms.Stored.ContainsKey("u.vault"));
            Assert.Contains(f.Rooms.Cache["t.hall"].Exits, e => e.DestinationId == "u.vault");
            Assert.DoesNotContain(f.Rooms.Cache["t.hall"].Exits, e => e.DestinationId == "t.cellar");
            Assert.Equal("u.vault", pat.CurrentLocationId);
            Assert.Contains(pat, f.Rooms.Cache["u.vault"].Occupants);
            Assert.Equal("u.vault", away.CurrentLocationId);
            Assert.Contains(f.Audit.Rows, r => r.EntityId == "t.cellar" && r.Field == "Key" && r.Applied);
            Assert.All(f.Audit.Rows, r => Assert.True(f.Audit.Outcomes[r.Id]));
        }

        [Fact]
        public void MovingOntoAnExistingKeyOrTheDefaultRoomIsRefused()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            string token = f.Login("cris");
            Assert.Equal(AdminResult.AlreadyExists, f.Api.MoveRoom(new MoveRoomRequest { Token = token, Key = "t.hall", NewArea = "t", NewLocalKey = "start" }).Result);
            RoomEditReply start = f.Api.MoveRoom(new MoveRoomRequest { Token = token, Key = "t.start", NewArea = "u", NewLocalKey = "x" });
            Assert.Equal(AdminResult.Refused, start.Result);
            Assert.Equal(AdminApi.DefaultRoomCannotBeMovedMessage, start.Message);
            Assert.True(f.Rooms.Stored.ContainsKey("t.start"));
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void MoveValidatesTheNewKeyAndReportsMissingRoomsAndNoChange()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            string token = f.Login("cris");
            Assert.Equal(AdminResult.Invalid, f.Api.MoveRoom(new MoveRoomRequest { Token = token, Key = "t.hall", NewArea = "Bad Area", NewLocalKey = "x" }).Result);
            Assert.Equal(AdminResult.Invalid, f.Api.MoveRoom(new MoveRoomRequest { Token = token, Key = "t.hall", NewArea = "u", NewLocalKey = "a.b" }).Result);
            Assert.Equal(AdminResult.NotFound, f.Api.MoveRoom(new MoveRoomRequest { Token = token, Key = "t.nowhere", NewArea = "u", NewLocalKey = "x" }).Result);
            Assert.Equal(AdminResult.NoChange, f.Api.MoveRoom(new MoveRoomRequest { Token = token, Key = "t.hall", NewArea = "t", NewLocalKey = "hall" }).Result);
            Assert.True(f.Rooms.Stored.ContainsKey("t.hall"));
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void MoveNeedsACreatorAndAnAuditRow()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            f.AddPlayer("Pat");
            string player = f.Login("pat");
            Refused(() => f.Api.MoveRoom(new MoveRoomRequest { Token = player, Key = "t.hall", NewArea = "u", NewLocalKey = "x" }));
            string token = f.Login("cris");
            f.Audit.FailInsert = true;
            Assert.Equal(AdminResult.AuditFailed, f.Api.MoveRoom(new MoveRoomRequest { Token = token, Key = "t.hall", NewArea = "u", NewLocalKey = "x" }).Result);
            Assert.True(f.Rooms.Stored.ContainsKey("t.hall"));
            Assert.False(f.Rooms.Stored.ContainsKey("u.x"));
        }

        // ------------------------------------------------------------------ players

        private static UpdatePlayerRequest PlayerEdit(string token, string nameKey, string shortText = null, bool? retired = null, AdminRole? role = null, string race = null)
        {
            var request = new UpdatePlayerRequest
            {
                Token = token,
                NameKey = nameKey,
                ShortDescription = shortText ?? (nameKey.Substring(0, 1).ToUpperInvariant() + nameKey.Substring(1)),
                LongDescription = "",
                Race = race ?? "human",
                Gender = "female",
                Class = "fighter"
            };
            if (retired.HasValue) { request.Retired = retired.Value; }
            if (role.HasValue) { request.Role = role.Value; }
            return request;
        }

        [Fact]
        public void ListPlayersFiltersSortsPagesAndNeverCarriesAPassword()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            f.AddPlayer("Anna");
            f.AddPlayer("Bob");
            string token = f.Login("cris");
            ListPlayersReply all = f.Api.ListPlayers(new ListPlayersRequest { Token = token, Page = 1, PageSize = 2 });
            Assert.Equal(3, all.Total);
            Assert.Equal(new[] { "anna", "bob" }, all.Items.Select(i => i.NameKey).ToArray());
            Assert.Equal(new[] { "cris" }, f.Api.ListPlayers(new ListPlayersRequest { Token = token, Page = 2, PageSize = 2 }).Items.Select(i => i.NameKey).ToArray());
            Assert.Equal(new[] { "cris", "bob", "anna" }, f.Api.ListPlayers(new ListPlayersRequest { Token = token, Descending = true }).Items.Select(i => i.NameKey).ToArray());
            Assert.Equal(new[] { "bob" }, f.Api.ListPlayers(new ListPlayersRequest { Token = token, NameFilter = "BO" }).Items.Select(i => i.NameKey).ToArray());
            Assert.Equal(AdminRole.Creator, all.Items.Concat(f.Api.ListPlayers(new ListPlayersRequest { Token = token, Page = 2, PageSize = 2 }).Items).Single(i => i.NameKey == "cris").Role);
            Assert.DoesNotContain("secret1", all.ToString());
        }

        [Fact]
        public void GetPlayerShowsTheEditableFieldsOnlyAndReportsAMissingPlayer()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            f.AddPlayer("Pat");
            string token = f.Login("cris");
            GetPlayerReply got = f.Api.GetPlayer(new GetPlayerRequest { Token = token, NameKey = "pat" });
            Assert.True(got.Found);
            Assert.Equal("Pat", got.Player.Name);
            Assert.Equal("human", got.Player.Race);
            Assert.Equal(AdminRole.Player, got.Player.Role);
            Assert.DoesNotContain("secret1", got.ToString());
            Assert.False(f.Api.GetPlayer(new GetPlayerRequest { Token = token, NameKey = "nobody" }).Found);
        }

        [Fact]
        public void GetOwnPlayerReturnsOnlyTheCallersOwnRecord_ForAnyRole_WithoutCredentials()
        {
            var f = new Fixture();
            f.AddPlayer("Pat");
            f.AddPlayer("Quinn");
            f.AddPlayer("Cris", creator: true);
            string pat = f.Login("pat");
            OwnPlayerReply own = f.Api.GetOwnPlayer(new TokenRequest { Token = pat });
            Assert.Equal("Pat", own.Name);
            Assert.Equal(AdminRole.Player, own.Role);
            Assert.Equal("human", own.Race);
            Assert.DoesNotContain("secret1", own.ToString());
            Assert.DoesNotContain("Quinn", own.ToString());
            Assert.Equal(AdminRole.Creator, f.Api.GetOwnPlayer(new TokenRequest { Token = f.Login("cris") }).Role);
        }

        [Fact]
        public void GetOwnPlayerRefusesAnUnknownTokenAndHasNoWayToNameAnotherPlayer()
        {
            var f = new Fixture();
            f.AddPlayer("Pat");
            Assert.Equal(AdminFailure.Unauthenticated, Refused(() => f.Api.GetOwnPlayer(new TokenRequest { Token = "nope" })).Failure);
            Assert.Equal(AdminFailure.Unauthenticated, Refused(() => f.Api.GetOwnPlayer(new TokenRequest { Token = "" })).Failure);
            Assert.DoesNotContain(OwnPlayerReply.Descriptor.Fields.InDeclarationOrder().Select(x => x.Name), n => n.Contains("password") || n.Contains("token"));
            Assert.DoesNotContain(TokenRequest.Descriptor.Fields.InDeclarationOrder().Select(x => x.Name), n => n.Contains("name"));
        }

        [Fact]
        public void ACreatorEditsAPlayersTextAndTheOnlinePlayerIsTold()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            Person pat = f.AddPlayer("Pat");
            f.Host.Online.Add(pat);
            string token = f.Login("cris");
            UpdatePlayerReply reply = f.Api.UpdatePlayer(PlayerEdit(token, "pat", shortText: "Pat the Bold", race: "elf"));
            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Equal("Pat the Bold", pat.ShortDescription);
            Assert.Equal("elf", pat.Race);
            Assert.Equal("elf", f.Players.Stored[pat.Id]["Race"].AsString);
            Assert.Equal("secret1", f.Players.Stored[pat.Id]["Password"].AsString);
            Assert.Contains("Something about you feels different...", pat.Messages);
            Assert.Equal(new[] { "Race", "ShortDescription" }, f.Audit.Rows.Select(r => r.Field).OrderBy(x => x).ToArray());
            Assert.All(f.Audit.Rows, r => { Assert.Equal("player", r.Entity); Assert.Equal("Pat", r.EntityName); Assert.True(f.Audit.Outcomes[r.Id]); });
        }

        [Fact]
        public void APlayerEditAfterTheGameChangedTheTextKeepsLastWriteWinsAndNeedsNoRevision()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            f.AddPlayer("Cleo", creator: true);
            Person pat = f.AddPlayer("Pat");
            string cris = f.Login("cris"), cleo = f.Login("cleo");
            Assert.Equal(AdminResult.Ok, f.Api.UpdatePlayer(PlayerEdit(cris, "pat", shortText: "first")).Result);
            Assert.Equal(AdminResult.Ok, f.Api.UpdatePlayer(PlayerEdit(cleo, "pat", shortText: "second")).Result);
            Assert.Equal("second", pat.ShortDescription);
        }

        [Fact]
        public void AnInvalidPlayerEditIsRejectedWithFieldErrors()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            Person pat = f.AddPlayer("Pat");
            string token = f.Login("cris");
            UpdatePlayerReply reply = f.Api.UpdatePlayer(PlayerEdit(token, "pat", shortText: "", race: new string('x', 33)));
            Assert.Equal(AdminResult.Invalid, reply.Result);
            Assert.Contains(reply.Errors, e => e.Field == "Short");
            Assert.Contains(reply.Errors, e => e.Field == "Race");
            Assert.Equal("Pat", pat.ShortDescription);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void ACreatorCannotEditAnotherCreatorOrAnAdminOrThemselves()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            f.AddPlayer("Cleo", creator: true);
            f.AddPlayer("Adam", admin: true);
            string token = f.Login("cris");
            Assert.Equal(AdminResult.Forbidden, f.Api.UpdatePlayer(PlayerEdit(token, "cleo")).Result);
            Assert.Equal(AdminResult.Forbidden, f.Api.UpdatePlayer(PlayerEdit(token, "adam")).Result);
            Assert.Equal(AdminResult.Forbidden, f.Api.UpdatePlayer(PlayerEdit(token, "cris")).Result);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void OnlyAnAdminMaySetARoleOrRetire()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            f.AddPlayer("Pat");
            string token = f.Login("cris");
            Assert.Equal(AdminResult.Forbidden, f.Api.UpdatePlayer(PlayerEdit(token, "pat", role: AdminRole.Creator)).Result);
            Assert.Equal(AdminResult.Forbidden, f.Api.UpdatePlayer(PlayerEdit(token, "pat", retired: true)).Result);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void AnAdminPromotesAndTheLivePlayerFollowsWithoutNeedingToLogInAgain()
        {
            var f = new Fixture();
            f.AddPlayer("Adam", admin: true);
            Person pat = f.AddPlayer("Pat");
            f.Host.Online.Add(pat);
            string adam = f.Login("adam"), patToken = f.Login("pat");
            Assert.Equal(AdminFailure.Forbidden, Refused(() => f.Api.ListRooms(new TokenRequest { Token = patToken })).Failure);
            UpdatePlayerReply reply = f.Api.UpdatePlayer(PlayerEdit(adam, "pat", role: AdminRole.Creator));
            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Equal(AdminRole.Creator, reply.Player.Role);
            Assert.True(pat.Roles.Creator);
            Assert.False(pat.Roles.Admin);
            Assert.Contains("Your status has changed.", pat.Messages);
            Assert.True(f.Players.Stored[pat.Id]["roles"]["creator"].AsBoolean);
            Assert.Equal(2, f.Api.ListRooms(new TokenRequest { Token = patToken }).Rooms.Count); // the same token, the new role
            Assert.Equal("Role", f.Audit.Rows.Single().Field);
            Assert.Equal("Player", f.Audit.Rows.Single().Old.AsString);
            Assert.Equal("Creator", f.Audit.Rows.Single().New.AsString);
        }

        [Fact]
        public void AnAdminCannotGrantAboveTheirOwnRoleOrEditAnotherAdminAndTheLastAdminIsGuarded()
        {
            var f = new Fixture();
            f.AddPlayer("Adam", admin: true);
            f.AddPlayer("Ada", admin: true);
            string adam = f.Login("adam");
            Assert.Equal(AdminResult.Forbidden, f.Api.UpdatePlayer(PlayerEdit(adam, "ada", role: AdminRole.Player)).Result);
            // Defensive guard: with one admin left the demotion is reported as the last admin, ahead of the generic rule.
            f.Players.Stored["p.ada"]["roles"]["admin"] = false;
            Assert.Equal(AdminResult.LastAdmin, f.Api.UpdatePlayer(PlayerEdit(adam, "adam", role: AdminRole.Player)).Result);
            Assert.Equal(AdminRole.Admin, f.Api.GetSession(new TokenRequest { Token = adam }).Role);
        }

        [Fact]
        public void RetiringAnOnlinePlayerTellsThemAndLogsThemOutThroughQuit()
        {
            var f = new Fixture();
            f.AddPlayer("Adam", admin: true);
            Person pat = f.AddPlayer("Pat");
            f.Host.Online.Add(pat);
            string adam = f.Login("adam");
            UpdatePlayerReply reply = f.Api.UpdatePlayer(PlayerEdit(adam, "pat", retired: true));
            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.True(pat.Retired);
            Assert.True(f.Players.Stored[pat.Id]["Retired"].AsBoolean);
            Assert.Contains("This character has been retired and can no longer be played.", pat.Messages);
            Assert.Contains("Thanks for playing!", pat.Messages); // the normal Quit path
            Assert.Equal(AuthResult.AuthRetired, f.Api.WebAuthenticate(new WebAuthRequest { Name = "pat", Password = "secret1", RemoteKey = "k" }).Result);
        }

        [Fact]
        public void ADemotionDoneThroughTheServiceEndsTheDemotedPlayersSessions()
        {
            var f = new Fixture();
            f.AddPlayer("Adam", admin: true);
            f.AddPlayer("Cris", creator: true);
            string adam = f.Login("adam"), cris = f.Login("cris");
            Assert.Equal(AdminResult.Ok, f.Api.UpdatePlayer(PlayerEdit(adam, "cris", role: AdminRole.Player)).Result);
            Assert.Null(f.Sessions.Find(cris));
        }

        [Fact]
        public void APlayerEditWithNothingChangedIsNoChange()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            f.AddPlayer("Pat");
            string token = f.Login("cris");
            Assert.Equal(AdminResult.NoChange, f.Api.UpdatePlayer(PlayerEdit(token, "pat")).Result);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void PlayerEditAuditFailureRefusesTheEditAndMissingPlayersAreNotFound()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            Person pat = f.AddPlayer("Pat");
            string token = f.Login("cris");
            Assert.Equal(AdminResult.NotFound, f.Api.UpdatePlayer(PlayerEdit(token, "nobody")).Result);
            f.Audit.FailInsert = true;
            Assert.Equal(AdminResult.AuditFailed, f.Api.UpdatePlayer(PlayerEdit(token, "pat", shortText: "changed")).Result);
            Assert.Equal("Pat", pat.ShortDescription);
        }

        // ------------------------------------------------------------------ audit listing

        [Fact]
        public void OnlyAnAdminReadsTheAuditAndItCanBeFiltered()
        {
            var f = new Fixture();
            f.AddPlayer("Adam", admin: true);
            f.AddPlayer("Pat");
            string adam = f.Login("adam");
            f.Api.UpdateRoom(Fixture.Edit(adam, "t.hall", "changed", "changed", "t", "back", "t.start"));
            f.Api.UpdatePlayer(PlayerEdit(adam, "pat", shortText: "Patty"));

            ListAuditReply everything = f.Api.ListAudit(new ListAuditRequest { Token = adam });
            Assert.Equal(4, everything.Total); // room: short, long, exits; player: short
            ListAuditReply players = f.Api.ListAudit(new ListAuditRequest { Token = adam, Entity = "player" });
            Assert.Equal("ShortDescription", players.Items.Single().Field);
            Assert.Equal("Pat", players.Items.Single().OldValue.Trim('"'));
            Assert.Equal("web", players.Items.Single().Source);
            Assert.True(players.Items.Single().Applied);
            Assert.Equal(1, f.Api.ListAudit(new ListAuditRequest { Token = adam, EntityId = "t.hall", PageSize = 1 }).Items.Count);
        }

        // ---- room features (card: Admin API room features)

        private static RoomFeature Feat(string shortText, string longText = "a long text", string roomLine = "", string detail = "", params string[] handles)
        {
            var f = new RoomFeature { Short = shortText, Long = longText, RoomLine = roomLine, Detail = detail };
            f.Handles.AddRange(handles);
            return f;
        }

        private static RoomEditRequest WithFeatures(Fixture f, string token, params RoomFeature[] features)
        {
            Room hall = f.Rooms.Cache["t.hall"];
            RoomEditRequest request = Fixture.Edit(token, "t.hall", hall.ShortDescription, hall.LongDescription, "t", hall.Exits[0].Name, "t.start");
            request.FeaturesPresent = true;
            request.Features.AddRange(features);
            return request;
        }

        private static Feature LiveFeature(string shortText, string roomLine = null, params string[] handles)
        {
            return new Feature { Id = "t.hall#" + shortText.ToLowerInvariant(), ShortDescription = shortText, LongDescription = "long " + shortText, RoomLine = roomLine, Handles = handles.ToList() };
        }

        [Fact]
        public void FeaturesCanBeAddedEditedRemovedAndReordered()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            string token = f.Login("cris");
            Room hall = f.Rooms.Cache["t.hall"];

            // add (empty room_line and detail are stored as null)
            RoomEditReply added = f.Api.UpdateRoom(WithFeatures(f, token, Feat("a table", "A table.", "", "", "table"), Feat("a statue", "A statue.", "A statue stands here.", "Cold stone.", "statue")));
            Assert.Equal(AdminResult.Ok, added.Result);
            Assert.Equal(new[] { "a table", "a statue" }, hall.Features.Select(x => x.ShortDescription).ToArray());
            Assert.Null(hall.Features[0].RoomLine);
            Assert.Null(hall.Features[0].DetailDescription);
            Assert.Equal("A statue stands here.", hall.Features[1].RoomLine);
            Assert.Equal(new[] { "table" }, hall.Features[0].Handles.ToArray());
            Assert.Equal(new[] { "a table", "a statue" }, added.Room.Features.Select(x => x.Short).ToArray());
            Assert.Equal(2, f.Rooms.Stored["t.hall"]["Features"].AsBsonArray.Count);
            Assert.True(f.Rooms.Stored["t.hall"]["Features"][0]["RoomLine"].IsBsonNull);

            // edit (matched case-insensitively by short, so the live object is kept)
            Feature tableObject = hall.Features[0];
            Assert.Equal(AdminResult.Ok, f.Api.UpdateRoom(WithFeatures(f, token, Feat("A TABLE", "A big table.", "", "", "table", "desk"), Feat("a statue", "A statue.", "A statue stands here.", "Cold stone.", "statue"))).Result);
            Assert.Same(tableObject, hall.Features[0]);
            Assert.Equal("A big table.", hall.Features[0].LongDescription);
            Assert.Equal(new[] { "table", "desk" }, hall.Features[0].Handles.ToArray());

            // reorder
            Assert.Equal(AdminResult.Ok, f.Api.UpdateRoom(WithFeatures(f, token, Feat("a statue", "A statue.", "A statue stands here.", "Cold stone.", "statue"), Feat("A TABLE", "A big table.", "", "", "table", "desk"))).Result);
            Assert.Equal(new[] { "a statue", "A TABLE" }, hall.Features.Select(x => x.ShortDescription).ToArray());
            Assert.Equal("a statue", f.Rooms.Stored["t.hall"]["Features"][0]["ShortDescription"].AsString);

            // remove one, then all (present with an empty list)
            Assert.Equal(AdminResult.Ok, f.Api.UpdateRoom(WithFeatures(f, token, Feat("a statue", "A statue.", "A statue stands here.", "Cold stone.", "statue"))).Result);
            Assert.Single(hall.Features);
            Assert.Equal(AdminResult.Ok, f.Api.UpdateRoom(WithFeatures(f, token)).Result);
            Assert.Empty(hall.Features);
            Assert.Empty(f.Rooms.Stored["t.hall"]["Features"].AsBsonArray);
        }

        [Fact]
        public void AFeatureEditNeverTouchesItemsOrOccupantsAndAuditsOldAndNew()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            Person pat = f.AddPlayer("Pat");
            Room hall = f.Rooms.Cache["t.hall"];
            hall.Occupants.Add(pat);
            hall.Items.Add(new InventoryItem());
            string token = f.Login("cris");

            Assert.Equal(AdminResult.Ok, f.Api.UpdateRoom(WithFeatures(f, token, Feat("a table", "A table.", "", "", "table"))).Result);
            Assert.Single(hall.Occupants);
            Assert.Single(hall.Items);
            Assert.Contains("Something feels different...", pat.Messages);

            AuditRecord row = f.Audit.Rows.Single();
            Assert.Equal("Features", row.Field);
            Assert.Equal("room", row.Entity);
            Assert.Equal("t.hall", row.EntityId);
            Assert.True(row.Old.IsBsonArray && row.Old.AsBsonArray.Count == 0);
            Assert.Equal("a table", row.New[0]["Short"].AsString);
            Assert.True(f.Audit.Outcomes[row.Id]);

            // a second edit records the old list too
            Assert.Equal(AdminResult.Ok, f.Api.UpdateRoom(WithFeatures(f, token, Feat("a table", "A new table.", "", "", "table"))).Result);
            AuditRecord second = f.Audit.Rows.Last();
            Assert.Equal("A table.", second.Old[0]["Long"].AsString);
            Assert.Equal("A new table.", second.New[0]["Long"].AsString);
        }

        [Fact]
        public void ANoOpFeatureListIsNoChange()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            string token = f.Login("cris");
            Assert.Equal(AdminResult.Ok, f.Api.UpdateRoom(WithFeatures(f, token, Feat("a table", "A table.", "", "", "table"))).Result);
            int rows = f.Audit.Rows.Count;
            Assert.Equal(AdminResult.NoChange, f.Api.UpdateRoom(WithFeatures(f, token, Feat("a table", "A table.", "", "", "table"))).Result);
            Assert.Equal(rows, f.Audit.Rows.Count);
        }

        [Fact]
        public void AnOlderCallerThatSendsNoFeaturesLeavesThemUnchanged()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            Room hall = f.Rooms.Cache["t.hall"];
            hall.Features.Add(LiveFeature("a table", "A table here.", "table"));
            f.Rooms.Stored["t.hall"] = hall.ToBsonDocument();
            string token = f.Login("cris");

            RoomEditRequest request = Fixture.Edit(token, "t.hall", "a grand hall", hall.LongDescription, "t", hall.Exits[0].Name, "t.start");
            Assert.False(request.FeaturesPresent);
            RoomEditReply reply = f.Api.UpdateRoom(request);
            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Equal("a table", hall.Features.Single().ShortDescription);
            Assert.Single(f.Rooms.Stored["t.hall"]["Features"].AsBsonArray);
            Assert.DoesNotContain(f.Audit.Rows, r => r.Field == "Features");
            Assert.Equal("a table", reply.Room.Features.Single().Short);
        }

        [Fact]
        public void FeatureBehaviorAndComponentConfigSurvivesAnEditByShort()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            Room hall = f.Rooms.Cache["t.hall"];
            Feature storm = LiveFeature("a wall of sand", "Sand swirls.", "sand");
            storm.SetComponent("desert.storm", new BsonDocument("endsAt", 99));
            hall.Features.Add(storm);
            f.Rooms.Stored["t.hall"] = hall.ToBsonDocument();
            string token = f.Login("cris");

            Assert.Equal(AdminResult.Ok, f.Api.UpdateRoom(WithFeatures(f, token, Feat("A Table", "A table.", "", "", "table"), Feat("a wall of sand", "Sand, edited.", "Sand swirls.", "", "sand", "wall"))).Result);
            Assert.Same(storm, hall.Features[1]);
            Assert.Equal(99, hall.Features[1].Components["desert"]["storm"]["endsAt"].AsInt32);
            Assert.Equal("Sand, edited.", hall.Features[1].LongDescription);
            BsonDocument stored = f.Rooms.Stored["t.hall"]["Features"][1].AsBsonDocument;
            Assert.Equal(99, stored["components"]["desert"]["storm"]["endsAt"].AsInt32);
            Assert.Equal("Sand, edited.", stored["LongDescription"].AsString);
            Assert.Equal("t.hall#a wall of sand", stored["_id"].ToString());
        }

        [Fact]
        public void InvalidFeaturesAreRefusedWithFieldErrorsAndChangeNothing()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            string token = f.Login("cris");
            Room hall = f.Rooms.Cache["t.hall"];

            var cases = new Dictionary<string, RoomFeature[]>
            {
                ["Features[0].Short"] = new[] { Feat("", "long") },
                ["Features[0].Long"] = new[] { Feat("a table", "  ") },
                ["Features[0].RoomLine"] = new[] { Feat("a table", "long", new string('x', 4001)) },
                ["Features[0].Detail"] = new[] { Feat("a table", "long", "", "bad\u0001text") },
                ["Features[0].Handles"] = new[] { Feat("a table", "long", "", "", "") },
                ["Features"] = new[] { Feat("a table"), Feat("A TABLE") }
            };
            foreach (var pair in cases)
            {
                RoomEditReply reply = f.Api.UpdateRoom(WithFeatures(f, token, pair.Value));
                Assert.Equal(AdminResult.Invalid, reply.Result);
                Assert.Contains(reply.Errors, e => e.Field == pair.Key);
            }
            RoomFeature tooLong = Feat(new string('s', 101));
            Assert.Equal(AdminResult.Invalid, f.Api.UpdateRoom(WithFeatures(f, token, tooLong)).Result);
            Assert.Empty(hall.Features);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void FeatureTextIsNormalisedAndTrailingWhitespaceIsDropped()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            string token = f.Login("cris");
            Assert.Equal(AdminResult.Ok, f.Api.UpdateRoom(WithFeatures(f, token, Feat("a table  ", "Line one.  \r\nLine two.  \r\n", "   ", "", "table"))).Result);
            Feature table = f.Rooms.Cache["t.hall"].Features.Single();
            Assert.Equal("a table", table.ShortDescription);
            Assert.Equal("Line one.\nLine two.", table.LongDescription);
            Assert.Null(table.RoomLine);
        }

        [Fact]
        public void GetRoomAndListRoomsCarryTheFeatures()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            f.Rooms.Cache["t.hall"].Features.Add(LiveFeature("a table", "Here.", "table"));
            f.Rooms.Stored["t.hall"] = f.Rooms.Cache["t.hall"].ToBsonDocument();
            string token = f.Login("cris");
            Assert.Equal("a table", f.Api.GetRoom(new GetRoomRequest { Token = token, Key = "t.hall" }).Room.Features.Single().Short);
            Assert.Equal("Here.", f.Api.GetRoom(new GetRoomRequest { Token = token, Key = "t.hall" }).Room.Features.Single().RoomLine);
            Assert.Equal("table", f.Api.ListRooms(new TokenRequest { Token = token }).Rooms.Single(r => r.Key == "t.hall").Features.Single().Handles.Single());
        }

        private static void AddBehaviorsAndSearch(Fixture f)
        {
            Room hall = f.Rooms.Cache["t.hall"];
            hall.Behaviors.Add(new MUD.Core.Components.BehaviorRef("room.entered", "desert", "Trap", 3, new BsonDocument { { "damage", 5 }, { "name", "a \"quoted\" \u00e9" }, { "tags", new BsonArray { "a", 1.5 } } }));
            hall.Behaviors.Add(new MUD.Core.Components.BehaviorRef("room.tick", "desert", "Plain"));
            hall.Search.Entries.Add(new MUD.Core.Components.SearchEntry { EntryId = "table", Target = "table", RefreshMilliseconds = 60000, Reward = new InventoryItem { ShortDescription = "a coin", LongDescription = "secret reward detail" } });
            hall.Search.Entries.Add(new MUD.Core.Components.SearchEntry { EntryId = "rug", Target = "rug", RefreshMilliseconds = 5 });
            f.Rooms.Stored["t.hall"] = hall.ToBsonDocument();
        }

        private static void AssertBehaviorsAndSearch(RoomDetail room)
        {
            Assert.Equal(2, room.Behaviors.Count);
            Assert.Equal("room.entered", room.Behaviors[0].Hook);
            Assert.Equal("desert", room.Behaviors[0].Module);
            Assert.Equal("Trap", room.Behaviors[0].Type);
            Assert.Equal(3, room.Behaviors[0].Priority);
            Assert.Equal("{\"damage\":5,\"name\":\"a \\u0022quoted\\u0022 \\u00E9\",\"tags\":[\"a\",1.5]}", room.Behaviors[0].ConfigJson);
            Assert.Equal("room.tick", room.Behaviors[1].Hook);
            Assert.Equal("{}", room.Behaviors[1].ConfigJson);
            Assert.Equal(2, room.Searches.Count);
            Assert.Equal(("table", "table", 60000, "a coin"), (room.Searches[0].Id, room.Searches[0].Target, room.Searches[0].RefreshMs, room.Searches[0].RewardShort));
            Assert.Equal(("rug", "rug", 5, ""), (room.Searches[1].Id, room.Searches[1].Target, room.Searches[1].RefreshMs, room.Searches[1].RewardShort));
            Assert.DoesNotContain("secret", room.ToString());
        }

        [Fact]
        public void GetRoomAndListRoomsCarryTheBehaviorsAndSearchEntriesFromALiveRoom()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            AddBehaviorsAndSearch(f);
            string token = f.Login("cris");
            AssertBehaviorsAndSearch(f.Api.GetRoom(new GetRoomRequest { Token = token, Key = "t.hall" }).Room);
            AssertBehaviorsAndSearch(f.Api.ListRooms(new TokenRequest { Token = token }).Rooms.Single(r => r.Key == "t.hall"));
            RoomDetail start = f.Api.GetRoom(new GetRoomRequest { Token = token, Key = "t.start" }).Room;
            Assert.Empty(start.Behaviors);
            Assert.Empty(start.Searches);
        }

        [Fact]
        public void ListRoomsCarriesTheBehaviorsAndSearchEntriesFromAStoredDocument()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            AddBehaviorsAndSearch(f);
            f.Rooms.Cache.Remove("t.hall"); // not loaded: ListRooms reads the stored document
            string token = f.Login("cris");
            AssertBehaviorsAndSearch(f.Api.ListRooms(new TokenRequest { Token = token }).Rooms.Single(r => r.Key == "t.hall"));
        }

        [Fact]
        public void ARoomWithoutTheCreatorRoleCannotReadBehaviors()
        {
            var f = new Fixture();
            f.AddPlayer("Pat");
            AddBehaviorsAndSearch(f);
            string token = f.Login("pat");
            Assert.Equal(AdminFailure.Forbidden, Refused(() => f.Api.GetRoom(new GetRoomRequest { Token = token, Key = "t.hall" })).Failure);
            Assert.Equal(AdminFailure.Forbidden, Refused(() => f.Api.ListRooms(new TokenRequest { Token = token })).Failure);
        }

        private static void AddDiscoverySearches(Fixture f)
        {
            Room hall = f.Rooms.Cache["t.hall"];
            hall.Search.Entries.Add(new MUD.Core.Components.SearchEntry { EntryId = "floor", Target = "floor", RefreshMilliseconds = 600000, Discovery = new MUD.Core.Components.SearchDiscovery { FlagId = "hidden_button", Found = "You press a button.", Again = "Nothing new." } });
            hall.Search.Entries.Add(new MUD.Core.Components.SearchEntry { EntryId = "chest", Target = "chest", RefreshMilliseconds = 1000, Reward = new InventoryItem { ShortDescription = "a key" }, Discovery = new MUD.Core.Components.SearchDiscovery { FlagId = "chest_seen" } });
            f.Rooms.Stored["t.hall"] = hall.ToBsonDocument();
        }

        private static void AssertDiscoverySearches(RoomDetail room)
        {
            Assert.Equal(2, room.Searches.Count);
            RoomSearchEntry floor = room.Searches[0];
            Assert.Equal(("floor", "floor", 600000, ""), (floor.Id, floor.Target, floor.RefreshMs, floor.RewardShort));
            Assert.Equal(("hidden_button", "You press a button.", "Nothing new."), (floor.Discovery.Id, floor.Discovery.Found, floor.Discovery.Again));
            RoomSearchEntry chest = room.Searches[1];
            Assert.Equal("a key", chest.RewardShort);
            Assert.Equal(("chest_seen", "", ""), (chest.Discovery.Id, chest.Discovery.Found, chest.Discovery.Again));
        }

        [Fact]
        public void SearchEntriesCarryTheirDiscoveryFromALiveRoomAndAStoredDocument()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            AddDiscoverySearches(f);
            string token = f.Login("cris");
            AssertDiscoverySearches(f.Api.GetRoom(new GetRoomRequest { Token = token, Key = "t.hall" }).Room);
            AssertDiscoverySearches(f.Api.ListRooms(new TokenRequest { Token = token }).Rooms.Single(r => r.Key == "t.hall"));
        }

        [Fact]
        public void EntriesWithoutADiscoveryLeaveItUnset()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            AddBehaviorsAndSearch(f);
            RoomDetail room = f.Api.GetRoom(new GetRoomRequest { Token = f.Login("cris"), Key = "t.hall" }).Room;
            Assert.All(room.Searches, s => Assert.Null(s.Discovery));
        }

        [Fact]
        public void UpdateRoomKeepsDiscoverySearchEntries()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            AddDiscoverySearches(f);
            Room hall = f.Rooms.Cache["t.hall"];
            string token = f.Login("cris");
            Assert.Equal(AdminResult.Ok, f.Api.UpdateRoom(Fixture.Edit(token, "t.hall", "edited short", hall.LongDescription, "t", hall.Exits[0].Name, "t.start")).Result);
            AssertDiscoverySearches(f.Api.GetRoom(new GetRoomRequest { Token = token, Key = "t.hall" }).Room);
            Assert.Equal("hidden_button", f.Rooms.Stored["t.hall"]["search"]["entries"][0]["discovery"]["id"].AsString);
        }

        [Fact]
        public void UpdateRoomIgnoresSentBehaviorsAndSearchesAndKeepsTheStoredOnes()
        {
            var f = new Fixture();
            f.AddPlayer("Cris", creator: true);
            AddBehaviorsAndSearch(f);
            Room hall = f.Rooms.Cache["t.hall"];
            string token = f.Login("cris");
            RoomEditRequest request = Fixture.Edit(token, "t.hall", "edited short", hall.LongDescription, "t", hall.Exits[0].Name, "t.start");
            // The request message has no behaviors or searches field; the web form sends the whole room, so only the editable fields count.
            Assert.Equal(AdminResult.Ok, f.Api.UpdateRoom(request).Result);

            Assert.Equal("edited short", hall.ShortDescription);
            Assert.Equal(2, hall.Behaviors.Count);
            Assert.Equal(2, hall.Search.Entries.Count);
            BsonDocument stored = f.Rooms.Stored["t.hall"];
            Assert.Equal(2, stored["behaviors"].AsBsonArray.Count);
            Assert.Equal(5, stored["behaviors"][0]["config"]["damage"].AsInt32);
            Assert.Equal(2, stored["search"]["entries"].AsBsonArray.Count);
            RoomDetail after = f.Api.GetRoom(new GetRoomRequest { Token = token, Key = "t.hall" }).Room;
            AssertBehaviorsAndSearch(after);
            Assert.DoesNotContain(f.Audit.Rows, r => r.Field != null && (r.Field.Contains("ehavior") || r.Field.Contains("earch")));
        }

        [Fact]
        public void ACallerWithoutTheCreatorRoleCannotEditFeatures()
        {
            var f = new Fixture();
            f.AddPlayer("Pat");
            string token = f.Login("pat");
            Assert.Equal(AdminFailure.Forbidden, Refused(() => f.Api.UpdateRoom(WithFeatures(f, token, Feat("a table")))).Failure);
            Assert.Empty(f.Rooms.Cache["t.hall"].Features);
        }

        // ------------------------------------------------------------------ modules

        private static Fixture ModuleFixture(out string anonymous, out string player, out string creator, out string admin)
        {
            var f = new Fixture();
            f.AddPlayer("Pat");
            f.AddPlayer("Cris", creator: true);
            f.AddPlayer("Adam", admin: true);
            f.Modules.Add("alpha", ModuleState.Ready)
                .Add("beta", ModuleState.Stopped)
                .Add("core-ish", ModuleState.Connected, required: true)
                .Add("broken", ModuleState.Failed, detail: "the process exited (exit code 1)")
                .Add("old", ModuleState.Stopped, problem: "contract version mismatch");
            anonymous = "no-such-token";
            player = f.Login("pat");
            creator = f.Login("cris");
            admin = f.Login("adam");
            return f;
        }

        [Fact]
        public void ListModulesIncludesStoppedAndNeverStartedModulesWithDerivedRunning()
        {
            Fixture f = ModuleFixture(out _, out _, out string creator, out _);
            ListModulesReply reply = f.Api.ListModules(new TokenRequest { Token = creator });
            Assert.Equal(new[] { "alpha", "beta", "core-ish", "broken", "old" }, reply.Modules.Select(m => m.Name).ToArray());
            Assert.True(reply.Modules.Single(m => m.Name == "alpha").Running);
            Assert.True(reply.Modules.Single(m => m.Name == "core-ish").Running);
            Assert.True(reply.Modules.Single(m => m.Name == "core-ish").Required);
            ModuleInfo beta = reply.Modules.Single(m => m.Name == "beta");
            Assert.False(beta.Running);
            Assert.Equal(ModuleRunState.Stopped, beta.State);
            Assert.Equal("1.0", beta.Version);
            ModuleInfo broken = reply.Modules.Single(m => m.Name == "broken");
            Assert.False(broken.Running);
            Assert.Equal(ModuleRunState.Failed, broken.State);
            Assert.Equal("the process exited (exit code 1)", broken.LastFailure);
            Assert.Equal("contract version mismatch", reply.Modules.Single(m => m.Name == "old").LastFailure);
        }

        [Fact]
        public void ListModulesIsEmptyWhenTheSupervisorIsNotRunning()
        {
            Fixture f = ModuleFixture(out _, out _, out string creator, out _);
            f.Modules.Running = false;
            Assert.Empty(f.Api.ListModules(new TokenRequest { Token = creator }).Modules);
        }

        [Fact]
        public void ModuleAuthorizationMatrix()
        {
            Fixture f = ModuleFixture(out string anonymous, out string player, out string creator, out string admin);
            // list: creator or admin
            Assert.Equal(AdminFailure.Unauthenticated, Refused(() => f.Api.ListModules(new TokenRequest { Token = anonymous })).Failure);
            Assert.Equal(AdminFailure.Forbidden, Refused(() => f.Api.ListModules(new TokenRequest { Token = player })).Failure);
            Assert.Equal(5, f.Api.ListModules(new TokenRequest { Token = creator }).Modules.Count);
            Assert.Equal(5, f.Api.ListModules(new TokenRequest { Token = admin }).Modules.Count);
            // start, stop and restart: admin only
            foreach (string token in new[] { anonymous, player, creator })
            {
                AdminFailure expected = token == anonymous ? AdminFailure.Unauthenticated : AdminFailure.Forbidden;
                Assert.Equal(expected, Refused(() => f.Api.StartModule(new ModuleRequest { Token = token, Name = "beta" })).Failure);
                Assert.Equal(expected, Refused(() => f.Api.StopModule(new ModuleRequest { Token = token, Name = "alpha" })).Failure);
                Assert.Equal(expected, Refused(() => f.Api.RestartModule(new ModuleRequest { Token = token, Name = "alpha" })).Failure);
            }
            Assert.Empty(f.Modules.Starts);
            Assert.Empty(f.Modules.Stops);
            Assert.Empty(f.Modules.Restarts);
            Assert.Empty(f.Audit.Rows);
            Assert.Equal(AdminResult.Ok, f.Api.StartModule(new ModuleRequest { Token = admin, Name = "beta" }).Result);
            Assert.Equal(AdminResult.Ok, f.Api.RestartModule(new ModuleRequest { Token = admin, Name = "alpha" }).Result);
        }

        [Fact]
        public void ModuleRoleIsReReadPerCall()
        {
            Fixture f = ModuleFixture(out _, out _, out _, out string admin);
            f.Players.Live.Single(p => p.NameKey == "adam").Roles.Admin = false;
            Assert.Equal(AdminFailure.Forbidden, Refused(() => f.Api.StartModule(new ModuleRequest { Token = admin, Name = "beta" })).Failure);
            Assert.Empty(f.Modules.Starts);
        }

        [Fact]
        public void StartQueuesTheStartOnTheSupervisorAndAudits()
        {
            Fixture f = ModuleFixture(out _, out _, out _, out string admin);
            ModuleActionReply reply = f.Api.StartModule(new ModuleRequest { Token = admin, Name = "beta" });
            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Equal("beta", reply.Module.Name);
            Assert.Equal(new[] { "beta" }, f.Modules.Starts);
            Assert.Empty(f.Modules.Stops);
            AuditRecord row = f.Audit.Rows.Single();
            Assert.Equal("module", row.Entity);
            Assert.Equal("beta", row.EntityId);
            Assert.Equal("Adam", row.ActorName);
            Assert.Equal("Action", row.Field);
            Assert.Equal("start", row.New.AsString);
            Assert.Equal("web", row.Source);
            Assert.True(row.Applied);
        }

        [Fact]
        public void StartOfAFailedModuleIsAllowed()
        {
            Fixture f = ModuleFixture(out _, out _, out _, out string admin);
            Assert.Equal(AdminResult.Ok, f.Api.StartModule(new ModuleRequest { Token = admin, Name = "broken" }).Result);
            Assert.Equal(new[] { "broken" }, f.Modules.Starts);
        }

        [Fact]
        public void StopQueuesTheStopOnTheSupervisorAndAudits()
        {
            Fixture f = ModuleFixture(out _, out _, out _, out string admin);
            ModuleActionReply reply = f.Api.StopModule(new ModuleRequest { Token = admin, Name = "alpha" });
            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Equal(new[] { "alpha" }, f.Modules.Stops);
            AuditRecord row = f.Audit.Rows.Single();
            Assert.Equal("alpha", row.EntityId);
            Assert.Equal("stop", row.New.AsString);
            Assert.Equal("Adam", row.ActorName);
            Assert.True(row.Applied);
        }

        [Fact]
        public void UnknownModuleIsNotFoundAndNothingIsAudited()
        {
            Fixture f = ModuleFixture(out _, out _, out _, out string admin);
            Assert.Equal(AdminResult.NotFound, f.Api.StartModule(new ModuleRequest { Token = admin, Name = "nope" }).Result);
            Assert.Equal(AdminResult.NotFound, f.Api.StopModule(new ModuleRequest { Token = admin, Name = "" }).Result);
            ModuleActionReply restart = f.Api.RestartModule(new ModuleRequest { Token = admin, Name = "nope" });
            Assert.Equal(AdminResult.NotFound, restart.Result);
            Assert.Equal(AdminApi.ModuleNotFoundMessage, restart.Message);
            Assert.Empty(f.Modules.Starts);
            Assert.Empty(f.Modules.Stops);
            Assert.Empty(f.Modules.Restarts);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void StoppingARequiredModuleIsAllowedAndAudited()
        {
            Fixture f = ModuleFixture(out _, out _, out _, out string admin);
            ModuleActionReply reply = f.Api.StopModule(new ModuleRequest { Token = admin, Name = "core-ish" });
            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.True(reply.Module.Required);
            Assert.Equal(new[] { "core-ish" }, f.Modules.Stops);
            AuditRecord row = f.Audit.Rows.Single();
            Assert.Equal("core-ish", row.EntityId);
            Assert.Equal("stop", row.New.AsString);
        }

        [Fact]
        public void RestartingARequiredModuleIsAllowed()
        {
            Fixture f = ModuleFixture(out _, out _, out _, out string admin);
            Assert.Equal(AdminResult.Ok, f.Api.RestartModule(new ModuleRequest { Token = admin, Name = "core-ish" }).Result);
            Assert.Equal(new[] { "core-ish" }, f.Modules.Restarts);
        }

        [Fact]
        public void RestartQueuesTheRestartOnTheSupervisorAndAudits()
        {
            Fixture f = ModuleFixture(out _, out _, out _, out string admin);
            ModuleActionReply reply = f.Api.RestartModule(new ModuleRequest { Token = admin, Name = "alpha" });
            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Equal(AdminApi.ModuleRestartQueuedMessage, reply.Message);
            Assert.Equal("alpha", reply.Module.Name);
            Assert.Equal(new[] { "alpha" }, f.Modules.Restarts);
            Assert.Empty(f.Modules.Starts);
            Assert.Empty(f.Modules.Stops);
            AuditRecord row = f.Audit.Rows.Single();
            Assert.Equal("module", row.Entity);
            Assert.Equal("alpha", row.EntityId);
            Assert.Equal("Adam", row.ActorName);
            Assert.Equal("Action", row.Field);
            Assert.Equal("restart", row.New.AsString);
            Assert.Equal("web", row.Source);
            Assert.True(row.Applied);
        }

        [Fact]
        public void RestartOfAStoppedModuleIsAStart()
        {
            Fixture f = ModuleFixture(out _, out _, out _, out string admin);
            ModuleActionReply reply = f.Api.RestartModule(new ModuleRequest { Token = admin, Name = "beta" });
            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Equal(AdminApi.ModuleStartQueuedMessage, reply.Message);
            Assert.Equal(new[] { "beta" }, f.Modules.Starts);
            Assert.Empty(f.Modules.Restarts);
            Assert.Equal("start", f.Audit.Rows.Single().New.AsString);
        }

        [Fact]
        public void RestartOfAFailedModuleIsAllowedButOneThatCannotStartIsRefused()
        {
            Fixture f = ModuleFixture(out _, out _, out _, out string admin);
            Assert.Equal(AdminResult.Ok, f.Api.RestartModule(new ModuleRequest { Token = admin, Name = "broken" }).Result);
            Assert.Equal(new[] { "broken" }, f.Modules.Restarts);
            f.Modules.Statuses.Single(s => s.Name == "old").State = ModuleState.Ready;
            ModuleActionReply reply = f.Api.RestartModule(new ModuleRequest { Token = admin, Name = "old" });
            Assert.Equal(AdminResult.Refused, reply.Result);
            Assert.Contains("contract version mismatch", reply.Message);
            Assert.Equal(new[] { "broken" }, f.Modules.Restarts);
        }

        [Fact]
        public void RestartAuditFailureRefusesAndNothingIsQueued()
        {
            Fixture f = ModuleFixture(out _, out _, out _, out string admin);
            f.Audit.FailInsert = true;
            Assert.Equal(AdminResult.AuditFailed, f.Api.RestartModule(new ModuleRequest { Token = admin, Name = "alpha" }).Result);
            Assert.Empty(f.Modules.Restarts);
        }

        [Fact]
        public void RestartIsRefusedWhenTheSupervisorIsNotRunningAndUnavailableWhenTheGameThreadIsDown()
        {
            Fixture f = ModuleFixture(out _, out _, out _, out string admin);
            f.Modules.Running = false;
            ModuleActionReply reply = f.Api.RestartModule(new ModuleRequest { Token = admin, Name = "alpha" });
            Assert.Equal(AdminResult.Refused, reply.Result);
            Assert.Equal(AdminApi.ModulesUnavailableMessage, reply.Message);
            Assert.Empty(f.Audit.Rows);
            f.Modules.Running = true;
            f.Host.GameThreadAvailable = false;
            Assert.Equal(AdminFailure.Unavailable, Refused(() => f.Api.RestartModule(new ModuleRequest { Token = admin, Name = "alpha" })).Failure);
            Assert.Empty(f.Modules.Restarts);
        }

        [Fact]
        public void AlreadyRunningAndAlreadyStoppedAreNoChangeWithAMessage()
        {
            Fixture f = ModuleFixture(out _, out _, out _, out string admin);
            ModuleActionReply running = f.Api.StartModule(new ModuleRequest { Token = admin, Name = "alpha" });
            Assert.Equal(AdminResult.NoChange, running.Result);
            Assert.Equal(AdminApi.ModuleAlreadyRunningMessage, running.Message);
            ModuleActionReply stopped = f.Api.StopModule(new ModuleRequest { Token = admin, Name = "beta" });
            Assert.Equal(AdminResult.NoChange, stopped.Result);
            Assert.Equal(AdminApi.ModuleAlreadyStoppedMessage, stopped.Message);
            Assert.Empty(f.Modules.Starts);
            Assert.Empty(f.Modules.Stops);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void AModuleThatCannotBeStartedIsRefusedWithItsProblem()
        {
            Fixture f = ModuleFixture(out _, out _, out _, out string admin);
            ModuleActionReply reply = f.Api.StartModule(new ModuleRequest { Token = admin, Name = "old" });
            Assert.Equal(AdminResult.Refused, reply.Result);
            Assert.Contains("contract version mismatch", reply.Message);
            Assert.Empty(f.Modules.Starts);
        }

        [Fact]
        public void AuditFailureRefusesTheModuleActionAndNothingIsQueued()
        {
            Fixture f = ModuleFixture(out _, out _, out _, out string admin);
            f.Audit.FailInsert = true;
            Assert.Equal(AdminResult.AuditFailed, f.Api.StartModule(new ModuleRequest { Token = admin, Name = "beta" }).Result);
            Assert.Equal(AdminResult.AuditFailed, f.Api.StopModule(new ModuleRequest { Token = admin, Name = "alpha" }).Result);
            Assert.Empty(f.Modules.Starts);
            Assert.Empty(f.Modules.Stops);
        }

        [Fact]
        public void ModuleActionsAreRefusedWhenTheSupervisorIsNotRunning()
        {
            Fixture f = ModuleFixture(out _, out _, out _, out string admin);
            f.Modules.Running = false;
            Assert.Equal(AdminResult.Refused, f.Api.StartModule(new ModuleRequest { Token = admin, Name = "beta" }).Result);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void ModuleActionsAreUnavailableWhenTheGameThreadDoesNotRun()
        {
            Fixture f = ModuleFixture(out _, out _, out _, out string admin);
            f.Host.GameThreadAvailable = false;
            Assert.Equal(AdminFailure.Unavailable, Refused(() => f.Api.StopModule(new ModuleRequest { Token = admin, Name = "alpha" })).Failure);
            Assert.Empty(f.Modules.Stops);
        }
    }
}
