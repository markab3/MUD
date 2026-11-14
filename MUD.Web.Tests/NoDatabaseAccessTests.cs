using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace MUD.Web.Tests;

// Card P6-5: core is the only process that talks to MongoDB, so the web project carries no database package, no connection
// string setting and no reference to a Mongo assembly.
public class NoDatabaseAccessTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "MUD.sln"))) { directory = directory.Parent; }
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    [Fact]
    public void TheWebAssembly_ReferencesNoMongoAssembly()
    {
        var web = typeof(MUD.Web.Services.IAdminClient).Assembly;
        Assert.DoesNotContain(web.GetReferencedAssemblies(), a => a.Name != null && a.Name.StartsWith("MongoDB", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheWebProjectFile_HasNoMongoPackage()
    {
        var project = XDocument.Load(Path.Combine(RepositoryRoot(), "MUD.Web", "MUD.Web.csproj"));
        var packages = project.Descendants("PackageReference").Select(p => (string?)p.Attribute("Include") ?? string.Empty);
        Assert.DoesNotContain(packages, name => name.StartsWith("MongoDB", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheWebSources_NeverReadTheConnectionStringOrNameAMongoType()
    {
        var web = Path.Combine(RepositoryRoot(), "MUD.Web");
        foreach (var file in Directory.EnumerateFiles(web, "*.cs*", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) { continue; }
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("MUD_MONGO_URI", text);
            Assert.DoesNotContain("MongoDB", text);
            Assert.DoesNotContain("IMongoClient", text);
        }
    }

    [Fact]
    public void TheWebServiceUnit_DoesNotReadMongoEnv()
    {
        var unit = File.ReadAllText(Path.Combine(RepositoryRoot(), "planar-realms-web.service"));
        Assert.DoesNotContain("mongo.env", unit);
        Assert.DoesNotContain("MUD_MONGO_URI", unit);
    }
}
