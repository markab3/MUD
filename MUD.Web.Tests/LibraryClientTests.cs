using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MUD.Contracts;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// The terrain library calls of the web's client: GrpcAdminClient over a real Unix socket against a stand-in for core's Admin service,
// and the FakeAdminClient the page tests use. Every result branch of core's reply is passed through unchanged; the transport
// statuses map onto the two exceptions.
public class LibraryGrpcClientTests : IDisposable
{
    private sealed class StandIn : Admin.AdminBase
    {
        private static void RequireToken(string token)
        {
            if (token == "stale") { throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unauthenticated, "no")); }
            if (token == "low") { throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.PermissionDenied, "no")); }
            if (token == "busy") { throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "no")); }
        }

        // The id decides the answer, so one stand-in covers every result branch.
        private static LibraryEditReply Answer(string id) => id switch
        {
            "taken" => new LibraryEditReply { Result = AdminResult.AlreadyExists, Message = "exists" },
            "gone" => new LibraryEditReply { Result = AdminResult.NotFound, Message = "missing" },
            "refused" => new LibraryEditReply { Result = AdminResult.Refused, Message = "no library" },
            "bad" => new LibraryEditReply { Result = AdminResult.Invalid, Errors = { new FieldError { Field = "text", Message = "required" } } },
            _ => new LibraryEditReply { Result = AdminResult.Ok, LibraryVersion = 12 },
        };

        public override Task<ListFragmentsReply> ListFragments(ListLibraryRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            var reply = new ListFragmentsReply { LibraryVersion = 3 };
            reply.Fragments.Add(new FragmentRecord { Id = "f1", Slot = "ground", Text = "Grass.", Terrain = request.Terrain.Length > 0 ? request.Terrain : "a.grass", Area = request.Area });
            return Task.FromResult(reply);
        }

        public override Task<GetFragmentReply> GetFragment(LibraryIdRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            return Task.FromResult(request.Id == "f1" ? new GetFragmentReply { Found = true, Fragment = new FragmentRecord { Id = "f1" } } : new GetFragmentReply());
        }

        public override Task<LibraryEditReply> CreateFragment(SaveFragmentRequest request, Grpc.Core.ServerCallContext context) { RequireToken(request.Token); return Task.FromResult(Answer(request.Fragment.Id)); }
        public override Task<LibraryEditReply> UpdateFragment(SaveFragmentRequest request, Grpc.Core.ServerCallContext context) { RequireToken(request.Token); return Task.FromResult(Answer(request.Fragment.Id)); }
        public override Task<LibraryEditReply> DeleteFragment(LibraryIdRequest request, Grpc.Core.ServerCallContext context) { RequireToken(request.Token); return Task.FromResult(Answer(request.Id)); }

        public override Task<ListFeatureTemplatesReply> ListFeatureTemplates(ListLibraryRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            var reply = new ListFeatureTemplatesReply();
            reply.Templates.Add(new FeatureTemplateRecord { Id = "t1", Version = 4 });
            return Task.FromResult(reply);
        }

        public override Task<GetFeatureTemplateReply> GetFeatureTemplate(LibraryIdRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            return Task.FromResult(request.Id == "t1" ? new GetFeatureTemplateReply { Found = true, Template = new FeatureTemplateRecord { Id = "t1", Version = 4 } } : new GetFeatureTemplateReply());
        }

        public override Task<LibraryEditReply> CreateFeatureTemplate(SaveFeatureTemplateRequest request, Grpc.Core.ServerCallContext context) { RequireToken(request.Token); return Task.FromResult(Answer(request.Template.Id)); }
        public override Task<LibraryEditReply> UpdateFeatureTemplate(SaveFeatureTemplateRequest request, Grpc.Core.ServerCallContext context) { RequireToken(request.Token); return Task.FromResult(Answer(request.Template.Id)); }
        public override Task<LibraryEditReply> DeleteFeatureTemplate(LibraryIdRequest request, Grpc.Core.ServerCallContext context) { RequireToken(request.Token); return Task.FromResult(Answer(request.Id)); }

        public override Task<ListNeighborPhrasesReply> ListNeighborPhrases(ListLibraryRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            var reply = new ListNeighborPhrasesReply();
            reply.Phrases.Add(new NeighborPhraseRecord { Id = "p1", NeighborTerrain = "river", Text = "{dir}", Merge = true });
            return Task.FromResult(reply);
        }

        public override Task<GetNeighborPhraseReply> GetNeighborPhrase(LibraryIdRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            return Task.FromResult(request.Id == "p1" ? new GetNeighborPhraseReply { Found = true, Phrase = new NeighborPhraseRecord { Id = "p1" } } : new GetNeighborPhraseReply());
        }

        public override Task<LibraryEditReply> CreateNeighborPhrase(SaveNeighborPhraseRequest request, Grpc.Core.ServerCallContext context) { RequireToken(request.Token); return Task.FromResult(Answer(request.Phrase.Id)); }
        public override Task<LibraryEditReply> UpdateNeighborPhrase(SaveNeighborPhraseRequest request, Grpc.Core.ServerCallContext context) { RequireToken(request.Token); return Task.FromResult(Answer(request.Phrase.Id)); }
        public override Task<LibraryEditReply> DeleteNeighborPhrase(LibraryIdRequest request, Grpc.Core.ServerCallContext context) { RequireToken(request.Token); return Task.FromResult(Answer(request.Id)); }

        // Terrain types: the id decides the answer, as for the library entities.
        private static TerrainTypeEditReply TerrainTypeAnswer(string id) => id switch
        {
            "a.taken" => new TerrainTypeEditReply { Result = AdminResult.AlreadyExists, Message = "exists" },
            "a.gone" => new TerrainTypeEditReply { Result = AdminResult.NotFound, Message = "missing" },
            "a.used" => new TerrainTypeEditReply { Result = AdminResult.Refused, Message = "3 description fragments still use it" },
            "a.bad" => new TerrainTypeEditReply { Result = AdminResult.Invalid, Errors = { new FieldError { Field = "Name", Message = "required" } } },
            _ => new TerrainTypeEditReply { Result = AdminResult.Ok, TerrainType = new TerrainTypeRecord { Id = id, Area = "a", Name = "N" } },
        };

        public override Task<ListTerrainTypesReply> ListTerrainTypes(ListTerrainTypesRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            var reply = new ListTerrainTypesReply();
            reply.TerrainTypes.Add(new TerrainTypeRecord { Id = (request.Area.Length > 0 ? request.Area : "a") + ".grass", Area = request.Area.Length > 0 ? request.Area : "a", Name = "Grass" });
            return Task.FromResult(reply);
        }

        public override Task<GetTerrainTypeReply> GetTerrainType(TerrainTypeIdRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            return Task.FromResult(request.Id == "a.grass" ? new GetTerrainTypeReply { Found = true, TerrainType = new TerrainTypeRecord { Id = "a.grass", Area = "a", Name = "Grass" } } : new GetTerrainTypeReply());
        }

        public override Task<TerrainTypeEditReply> CreateTerrainType(SaveTerrainTypeRequest request, Grpc.Core.ServerCallContext context) { RequireToken(request.Token); return Task.FromResult(TerrainTypeAnswer(request.TerrainType.Id)); }
        public override Task<TerrainTypeEditReply> UpdateTerrainType(SaveTerrainTypeRequest request, Grpc.Core.ServerCallContext context) { RequireToken(request.Token); return Task.FromResult(TerrainTypeAnswer(request.TerrainType.Id)); }
        public override Task<TerrainTypeEditReply> DeleteTerrainType(TerrainTypeIdRequest request, Grpc.Core.ServerCallContext context) { RequireToken(request.Token); return Task.FromResult(TerrainTypeAnswer(request.Id)); }
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wl" + Guid.NewGuid().ToString("N").Substring(0, 8));
    private WebApplication? _app;

    private async Task<GrpcAdminClient> Start()
    {
        Directory.CreateDirectory(_directory);
        var socket = Path.Combine(_directory, "f.sock");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(socket, o => o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2));
        builder.Services.AddGrpc();
        _app = builder.Build();
        _app.MapGrpcService<StandIn>();
        await _app.StartAsync();
        return new GrpcAdminClient(new Admin.AdminClient(CoreSocket.OpenChannel(socket)));
    }

    public void Dispose()
    {
        _app?.StopAsync().GetAwaiter().GetResult();
        try { Directory.Delete(_directory, true); } catch (Exception) { }
    }

    [Fact]
    public async Task ListAndGet_WorkForAllThreeKinds_AndGetOfAnUnknownIdIsNull()
    {
        var client = await Start();
        Assert.Equal("f1", Assert.Single(await client.ListFragmentsAsync("tok")).Id);
        Assert.Equal("f1", (await client.GetFragmentAsync("tok", "f1"))!.Id);
        Assert.Null(await client.GetFragmentAsync("tok", "zz"));
        Assert.Equal(4, Assert.Single(await client.ListFeatureTemplatesAsync("tok")).Version);
        Assert.Equal(4, (await client.GetFeatureTemplateAsync("tok", "t1"))!.Version);
        Assert.Null(await client.GetFeatureTemplateAsync("tok", "zz"));
        Assert.Equal("river", Assert.Single(await client.ListNeighborPhrasesAsync("tok")).NeighborTerrain);
        Assert.NotNull(await client.GetNeighborPhraseAsync("tok", "p1"));
        Assert.Null(await client.GetNeighborPhraseAsync("tok", "zz"));
    }

    [Theory]
    [InlineData("ok", AdminResult.Ok)]
    [InlineData("taken", AdminResult.AlreadyExists)]
    [InlineData("gone", AdminResult.NotFound)]
    [InlineData("refused", AdminResult.Refused)]
    [InlineData("bad", AdminResult.Invalid)]
    public async Task WriteCalls_PassEveryResultBranchThrough_ForAllThreeKinds(string id, AdminResult expected)
    {
        var client = await Start();
        Assert.Equal(expected, (await client.CreateFragmentAsync("tok", new FragmentRecord { Id = id })).Result);
        Assert.Equal(expected, (await client.UpdateFragmentAsync("tok", new FragmentRecord { Id = id })).Result);
        Assert.Equal(expected, (await client.DeleteFragmentAsync("tok", id)).Result);
        Assert.Equal(expected, (await client.CreateFeatureTemplateAsync("tok", new FeatureTemplateRecord { Id = id })).Result);
        Assert.Equal(expected, (await client.UpdateFeatureTemplateAsync("tok", new FeatureTemplateRecord { Id = id })).Result);
        Assert.Equal(expected, (await client.DeleteFeatureTemplateAsync("tok", id)).Result);
        Assert.Equal(expected, (await client.CreateNeighborPhraseAsync("tok", new NeighborPhraseRecord { Id = id })).Result);
        Assert.Equal(expected, (await client.UpdateNeighborPhraseAsync("tok", new NeighborPhraseRecord { Id = id })).Result);
        Assert.Equal(expected, (await client.DeleteNeighborPhraseAsync("tok", id)).Result);
    }

    [Fact]
    public async Task ListFilters_TerrainAndArea_ReachCore()
    {
        var client = await Start();
        Assert.Equal("a.forest", Assert.Single(await client.ListFragmentsAsync("tok", terrain: "a.forest")).Terrain);
        Assert.Equal("b", Assert.Single(await client.ListFragmentsAsync("tok", area: "b")).Area);
        Assert.Equal("b.grass", Assert.Single(await client.ListTerrainTypesAsync("tok", "b")).Id);
    }

    [Fact]
    public async Task TerrainTypes_ListGet_AndGetOfAnUnknownIdIsNull()
    {
        var client = await Start();
        Assert.Equal("a.grass", Assert.Single(await client.ListTerrainTypesAsync("tok")).Id);
        Assert.Equal("a", (await client.GetTerrainTypeAsync("tok", "a.grass"))!.Area);
        Assert.Null(await client.GetTerrainTypeAsync("tok", "a.nope"));
    }

    [Theory]
    [InlineData("a.ok", AdminResult.Ok)]
    [InlineData("a.taken", AdminResult.AlreadyExists)]
    [InlineData("a.gone", AdminResult.NotFound)]
    [InlineData("a.used", AdminResult.Refused)]
    [InlineData("a.bad", AdminResult.Invalid)]
    public async Task TerrainTypeWrites_PassEveryResultBranchThrough(string id, AdminResult expected)
    {
        var client = await Start();
        Assert.Equal(expected, (await client.CreateTerrainTypeAsync("tok", new TerrainTypeRecord { Id = id })).Result);
        Assert.Equal(expected, (await client.UpdateTerrainTypeAsync("tok", new TerrainTypeRecord { Id = id })).Result);
        Assert.Equal(expected, (await client.DeleteTerrainTypeAsync("tok", id)).Result);
    }

    [Fact]
    public async Task TerrainTypeStatuses_MapOntoTheTwoExceptions_AndWhenNothingListens()
    {
        var client = await Start();
        Assert.True((await Assert.ThrowsAsync<CoreRefusedException>(() => client.ListTerrainTypesAsync("stale"))).SessionEnded);
        Assert.False((await Assert.ThrowsAsync<CoreRefusedException>(() => client.CreateTerrainTypeAsync("low", new TerrainTypeRecord { Id = "a.x" }))).SessionEnded);
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.DeleteTerrainTypeAsync("busy", "a.x"));

        var none = new GrpcAdminClient(new Admin.AdminClient(CoreSocket.OpenChannel(Path.Combine(_directory, "none.sock"))));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => none.ListTerrainTypesAsync("tok"));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => none.UpdateTerrainTypeAsync("tok", new TerrainTypeRecord { Id = "a.x" }));
    }

    [Fact]
    public async Task AnInvalidReplyCarriesItsFieldErrors_AndAnOkReplyItsVersion()
    {
        var client = await Start();
        var invalid = await client.CreateFragmentAsync("tok", new FragmentRecord { Id = "bad" });
        Assert.Equal(("text", "required"), (invalid.Errors[0].Field, invalid.Errors[0].Message));
        Assert.Equal(12, (await client.CreateFragmentAsync("tok", new FragmentRecord { Id = "new" })).LibraryVersion);
    }

    [Fact]
    public async Task CoresStatuses_MapOntoTheTwoExceptions_ForListGetAndWrite()
    {
        var client = await Start();
        Assert.True((await Assert.ThrowsAsync<CoreRefusedException>(() => client.ListFragmentsAsync("stale"))).SessionEnded);
        Assert.False((await Assert.ThrowsAsync<CoreRefusedException>(() => client.GetFeatureTemplateAsync("low", "t1"))).SessionEnded);
        Assert.False((await Assert.ThrowsAsync<CoreRefusedException>(() => client.CreateNeighborPhraseAsync("low", new NeighborPhraseRecord { Id = "x" }))).SessionEnded);
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.ListNeighborPhrasesAsync("busy"));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.DeleteFragmentAsync("busy", "x"));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.UpdateFeatureTemplateAsync("busy", new FeatureTemplateRecord { Id = "x" }));
    }

    [Fact]
    public async Task WhenNothingListens_EveryLibraryCallReportsCoreUnavailable()
    {
        Directory.CreateDirectory(_directory);
        var client = new GrpcAdminClient(new Admin.AdminClient(CoreSocket.OpenChannel(Path.Combine(_directory, "none.sock"))));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.ListFragmentsAsync("tok"));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.GetFragmentAsync("tok", "x"));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.CreateFeatureTemplateAsync("tok", new FeatureTemplateRecord { Id = "x" }));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.DeleteNeighborPhraseAsync("tok", "x"));
    }
}

public class FakeLibraryClientTests
{
    private static FragmentRecord Fragment(string id) => new() { Id = id, Slot = "ground", Text = "x" };

    [Fact]
    public async Task OkWrites_ChangeTheFakesStores_AndRecordTheTokenAndCall()
    {
        var core = new FakeAdminClient();
        Assert.Equal(AdminResult.Ok, (await core.CreateFragmentAsync("tok", Fragment("a"))).Result);
        Assert.Equal("a", Assert.Single(await core.ListFragmentsAsync("tok")).Id);
        Assert.Equal(AdminResult.Ok, (await core.DeleteFragmentAsync("tok", "a")).Result);
        Assert.Empty(await core.ListFragmentsAsync("tok"));
        Assert.Equal(new[] { "CreateFragment", "ListFragments", "DeleteFragment", "ListFragments" }, core.LibraryCalls);
        Assert.All(core.LibraryTokens, t => Assert.Equal("tok", t));
    }

    [Theory]
    [InlineData(AdminResult.AlreadyExists)]
    [InlineData(AdminResult.NotFound)]
    [InlineData(AdminResult.Refused)]
    [InlineData(AdminResult.Invalid)]
    public async Task ANonOkResult_IsAnsweredAndTheStoreIsLeftAlone(AdminResult result)
    {
        var core = new FakeAdminClient { LibraryResult = result, LibraryMessage = "fixed" };
        var reply = await core.CreateNeighborPhraseAsync("tok", new NeighborPhraseRecord { Id = "p" });
        Assert.Equal((result, "fixed"), (reply.Result, reply.Message));
        Assert.Empty(core.NeighborPhrases);
    }

    [Fact]
    public async Task UnavailableAndRefusedAreRaisedByEveryLibraryCall()
    {
        var down = new FakeAdminClient { Unavailable = true };
        await Assert.ThrowsAsync<CoreUnavailableException>(() => down.ListFeatureTemplatesAsync("tok"));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => down.UpdateFragmentAsync("tok", Fragment("a")));

        var refusing = new FakeAdminClient { RefuseLibraryCalls = true, RefuseBecauseSessionEndedForLibrary = true };
        Assert.True((await Assert.ThrowsAsync<CoreRefusedException>(() => refusing.GetNeighborPhraseAsync("tok", "p"))).SessionEnded);
    }

    [Fact]
    public async Task TemplateVersionIsOwnedByTheFake_LikeCore()
    {
        var core = new FakeAdminClient();
        await core.CreateFeatureTemplateAsync("tok", new FeatureTemplateRecord { Id = "t", Short = "s", Long = "l", Version = 99 });
        Assert.Equal(1, (await core.GetFeatureTemplateAsync("tok", "t"))!.Version);
        await core.UpdateFeatureTemplateAsync("tok", new FeatureTemplateRecord { Id = "t", Short = "s", Long = "changed", Version = 99 });
        Assert.Equal(2, (await core.GetFeatureTemplateAsync("tok", "t"))!.Version);
    }
}
