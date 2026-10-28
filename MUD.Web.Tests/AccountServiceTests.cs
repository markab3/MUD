using MongoDB.Bson;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

public class FakeAccountStore : IAccountStore
{
    private readonly Dictionary<string, StoredAccount> _byKey = new();
    public FakeAccountStore Add(string name, string? password, AccountRole role = AccountRole.Player)
    {
        _byKey[name.Trim().ToLowerInvariant()] = new StoredAccount(name, password, role);
        return this;
    }
    public StoredAccount? FindByNameKey(string nameKey) => _byKey.TryGetValue(nameKey, out var a) ? a : null;
    public List<(string Key, string Password)> Updates { get; } = new();
    public bool UpdateResult { get; set; } = true;
    public bool UpdatePassword(string nameKey, string newPassword)
    {
        Updates.Add((nameKey, newPassword));
        if (!UpdateResult || !_byKey.TryGetValue(nameKey, out var a)) { return false; }
        _byKey[nameKey] = a with { Password = newPassword };
        return true;
    }
}

public class AccountServiceTests
{
    private static AccountService Service() => new(new FakeAccountStore()
        .Add("Holen", "secret", AccountRole.Admin)
        .Add("Bob", "pw", AccountRole.Creator)
        .Add("Bobby", "other")
        .Add("Nopass", null));

    [Fact]
    public void Authenticate_Success_ReturnsAccountWithDisplayName()
    {
        var a = Service().Authenticate("Holen", "secret");
        Assert.NotNull(a);
        Assert.Equal("Holen", a!.PlayerName);
        Assert.Equal(AccountRole.Admin, a.Role);
    }

    [Fact]
    public void Authenticate_WrongPassword_ReturnsNull() => Assert.Null(Service().Authenticate("Holen", "Secret"));

    [Fact]
    public void Authenticate_UnknownUser_ReturnsNull() => Assert.Null(Service().Authenticate("nobody", "secret"));

    [Fact]
    public void Authenticate_NameIsCaseInsensitiveAndTrimmed()
    {
        Assert.Equal("Holen", Service().Authenticate("  hOLEN ", "secret")!.PlayerName);
        Assert.Equal("Holen", Service().GetByName("HOLEN")!.PlayerName);
    }

    [Fact]
    public void Lookup_IsExactNotSubstring()
    {
        Assert.Null(Service().GetByName("Hol"));
        Assert.Null(Service().GetByName("Holene"));
        Assert.Equal("Bob", Service().GetByName("bob")!.PlayerName);
        Assert.Null(Service().Authenticate("Bo", "pw"));
    }

    [Fact]
    public void Authenticate_EmptyNameOrMissingStoredPassword_ReturnsNull()
    {
        Assert.Null(Service().Authenticate("", ""));
        Assert.Null(Service().Authenticate("   ", "x"));
        Assert.Null(Service().Authenticate("Nopass", ""));
    }

    [Fact]
    public void GetByName_ReturnsRole()
    {
        Assert.Equal(AccountRole.Creator, Service().GetByName("bob")!.Role);
        Assert.Equal(AccountRole.Player, Service().GetByName("bobby")!.Role);
    }

    [Fact]
    public void PasswordsEqual_ComparesContent()
    {
        Assert.True(AccountService.PasswordsEqual("abc", "abc"));
        Assert.False(AccountService.PasswordsEqual("abc", "abcd"));
        Assert.False(AccountService.PasswordsEqual("abc", ""));
    }
}

public class RoleMappingTests
{
    private static BsonDocument Doc(BsonValue roles) => new() { { "roles", roles } };

    private static BsonDocument Roles(bool creator, bool admin) => new() { { "creator", creator }, { "admin", admin }, { "currentDirectory", BsonNull.Value } };

    [Fact]
    public void NoRolesComponent_IsPlayer() => Assert.Equal(AccountRole.Player, MongoAccountStore.RoleFrom(new BsonDocument()));

    [Fact]
    public void EmptyOrMalformedRoles_IsPlayer()
    {
        Assert.Equal(AccountRole.Player, MongoAccountStore.RoleFrom(Doc(new BsonDocument())));
        Assert.Equal(AccountRole.Player, MongoAccountStore.RoleFrom(Doc("admin")));
        Assert.Equal(AccountRole.Player, MongoAccountStore.RoleFrom(Doc(new BsonDocument { { "creator", "true" }, { "admin", 1 } })));
    }

    [Fact]
    public void BothFlagsFalse_IsPlayer() => Assert.Equal(AccountRole.Player, MongoAccountStore.RoleFrom(Doc(Roles(false, false))));

    [Fact]
    public void Creator() => Assert.Equal(AccountRole.Creator, MongoAccountStore.RoleFrom(Doc(Roles(true, false))));

    [Fact]
    public void Admin() => Assert.Equal(AccountRole.Admin, MongoAccountStore.RoleFrom(Doc(Roles(false, true))));

    [Fact]
    public void AdminWinsOverCreator() => Assert.Equal(AccountRole.Admin, MongoAccountStore.RoleFrom(Doc(Roles(true, true))));

    [Fact]
    public void TheOldExtendedPropertiesShapeIsNotRead()
    {
        // No dual-read of the old shape (decision D1): a document with only the old role elements is a plain player.
        var old = new BsonDocument { { "ExtendedProperties", new BsonArray { new BsonDocument("_t", new BsonArray { "Entity", "ExtendedProperty", "AdminProperty" }) } } };
        Assert.Equal(AccountRole.Player, MongoAccountStore.RoleFrom(old));
    }
}

public class LoginThrottleTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void BlocksAfterMaxFailuresThenExpires()
    {
        var clock = new Clock();
        var t = new LoginThrottle(clock);
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            Assert.False(t.IsBlocked("1.1.1.1"));
            t.RecordFailure("1.1.1.1");
        }
        Assert.True(t.IsBlocked("1.1.1.1"));
        Assert.False(t.IsBlocked("2.2.2.2"));
        clock.Now += LoginThrottle.Window + TimeSpan.FromSeconds(1);
        Assert.False(t.IsBlocked("1.1.1.1"));
    }

    [Fact]
    public void ResetClears()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++) t.RecordFailure("x");
        t.Reset("x");
        Assert.False(t.IsBlocked("x"));
    }
}
