using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core.Administration;
using MUD.Core.GameObjects;
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
            public AdminApi Api;

            public Fixture()
            {
                var queries = new FakeQueries { Players = Players, Rooms = Rooms };
                Api = new AdminApi(Host, Players, Rooms, queries, Audit, Sessions, new FakeClock(), Log.Add, max => 0);
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
    }
}
