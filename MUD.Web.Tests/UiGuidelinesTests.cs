using System.Text.RegularExpressions;
using Xunit;

namespace MUD.Web.Tests;

// The "Website UI guidelines" of specs/WEB_BLAZOR.md, enforced on the sources: the pinned Save bar on edit pages, no old sticky rule, no Cancel on
// edit forms, standalone buttons full width. The rendered behaviour of each page is covered in the page tests; this fails when a new page drifts.
public class UiGuidelinesTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MUD.sln"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static IEnumerable<string> Razor(string folder) =>
        Directory.EnumerateFiles(Path.Combine(Root(), "MUD.Web", "Components", folder), "*.razor");

    // page -> id of its edit form (the pinned bar must target exactly this form)
    public static IEnumerable<object[]> EditPages() => new[]
    {
        new object[] { "RoomEdit", "re-form" },
        new object[] { "TerrainEdit", "te-form" },
        new object[] { "ItemTemplateEdit", "it-form" },
        new object[] { "PlayerEdit", "pe-form" },
        new object[] { "Creator", "cr-form" },
        new object[] { "TerrainList", "tl-type-form" },
    };

    [Theory]
    [MemberData(nameof(EditPages))]
    public void EditPage_HasOnePinnedSave_TargetingItsForm_AndTheFormExists(string page, string formId)
    {
        var src = Read("MUD.Web/Components/Pages/" + page + ".razor");
        var bars = Regex.Matches(src, "<PinnedSave\\b[^>]*>");
        Assert.Single(bars);
        Assert.Contains("FormId=\"" + formId + "\"", bars[0].Value);
        Assert.Contains("<form id=\"" + formId + "\"", src);
        // The bar is the last element of the page markup (before @code): it comes after everything else.
        var markup = src.Substring(0, src.IndexOf("@code", StringComparison.Ordinal));
        Assert.True(markup.LastIndexOf("<PinnedSave", StringComparison.Ordinal) > markup.LastIndexOf("</form>", StringComparison.Ordinal));
    }

    [Fact]
    public void PinnedSave_IsOneButton_SubmittingTheNamedForm_DisabledWhileBusy_AtLeast44px_FullWidth()
    {
        var src = Read("MUD.Web/Components/Shared/PinnedSave.razor");
        Assert.Single(Regex.Matches(src, "<button\\b"));
        Assert.Contains("type=\"submit\" form=\"@FormId\"", src);
        Assert.Contains("disabled=\"@Busy\"", src);
        Assert.Contains("mud-btn-primary mud-btn-block mud-save", src);
        var css = Read("MUD.Web/wwwroot/css/mobile.css");
        Assert.Matches(@"\.mud-btn-block \{ display: flex; width: 100%; min-height: var\(--mud-hit\);", css);
    }

    [Fact]
    public void PinnedBarCss_IsFixedToTheBottom_AsWideAsTheContentColumn_WithSafeArea_AndBelowDialogsAndTheDrawer()
    {
        var css = Read("MUD.Web/wwwroot/css/mobile.css");
        var bar = Regex.Match(css, @"^\.mud-pinbar \{[^}]*\}", RegexOptions.Multiline).Value;
        Assert.Contains("position: fixed; bottom: 0;", bar);
        Assert.Contains("width: min(100%, 1100px)", bar);     // the .mud-main max-width
        Assert.Contains("env(safe-area-inset-bottom)", bar);
        Assert.Contains("max-width: 1100px", Regex.Match(css, @"^\.mud-main \{[^}]*\}", RegexOptions.Multiline).Value);
        Assert.Contains("scroll-padding-bottom", css);
        Assert.Contains(".mud-pinbar-space { height: calc(var(--mud-pinbar-h)", css);
        int Z(string rule) => int.Parse(Regex.Match(Regex.Match(css, @"^" + Regex.Escape(rule) + @" \{[^}]*\}", RegexOptions.Multiline).Value, @"z-index: (\d+)").Groups[1].Value);
        Assert.True(Z(".mud-pinbar") < Z(".mud-confirm"));
        Assert.True(Z(".mud-confirm") < Z(".mud-drawer-wrap"));
        Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,6}\b", bar);
    }

    [Fact]
    public void NoPageOrComponent_UsesTheOldStickyActions_AndTheRuleIsGone()
    {
        foreach (var file in Razor("Pages").Concat(Razor("Shared")).Concat(Razor("Layout")))
        {
            Assert.DoesNotContain("mud-sticky-actions", File.ReadAllText(file));
        }
        Assert.DoesNotContain("mud-sticky-actions", Read("MUD.Web/wwwroot/css/mobile.css"));
    }

    // Documented exceptions: confirmation dialogs keep Cancel (it is the safe way to say no to a destructive action).
    private static readonly string[] CancelAllowed = { "ConfirmDialog.razor" };

    [Fact]
    public void NoCancelButtonOnAnEditForm_OnlyTheConfirmationDialogsKeepOne()
    {
        foreach (var file in Razor("Pages").Concat(Razor("Shared")))
        {
            var name = Path.GetFileName(file);
            var src = File.ReadAllText(file);
            var cancels = Regex.Matches(src, "<button\\b[^>]*>\\s*Cancel\\s*</button>").Count;
            if (CancelAllowed.Contains(name)) { Assert.Equal(1, cancels); continue; }
            if (name == "RoomEdit.razor")
            {
                // The Regen confirmation sits inside the page; it is the only Cancel and it answers a destructive-action question.
                Assert.Equal(1, cancels);
                Assert.Contains("CancelRegen", src);
                continue;
            }
            Assert.True(cancels == 0, name + " has a Cancel button");
        }
        Assert.False(File.Exists(Path.Combine(Root(), "MUD.Web", "Components", "Shared", "TerrainTypeForm.razor")));
    }

    [Fact]
    public void StandaloneButtons_AreFullWidth()
    {
        // Pages whose "alone on its line" buttons must carry mud-btn-block (or sit alone in a .mud-actions row, which spans it).
        string[] expected =
        {
            "Pages/AreaList.razor:id=\"ar-new\"", "Pages/AreaList.razor:id=\"ar-save\"", "Pages/Password.razor:Change password",
            "Pages/RoomEdit.razor:id=\"re-back\"", "Pages/TerrainEdit.razor:id=\"te-back\"", "Pages/ItemTemplateEdit.razor:id=\"it-back\"",
            "Pages/PlayerEdit.razor:id=\"pe-back\"", "Pages/TerrainList.razor:id=\"tl-deltype\"", "Pages/TerrainList.razor:id=\"tl-newtype-save\"",
        };
        foreach (var e in expected)
        {
            var (file, marker) = (e.Substring(0, e.IndexOf(':')), e.Substring(e.IndexOf(':') + 1));
            var src = Read("MUD.Web/Components/" + file);
            var tag = Regex.Matches(src, "<(?:a|button)\\b[^>]*>").Select(m => m.Value).FirstOrDefault(t => t.Contains(marker)) ??
                      Regex.Matches(src, "<(?:a|button)\\b[^>]*>[^<]*").Select(m => m.Value).First(t => t.Contains(marker));
            Assert.True(tag.Contains("mud-btn-block"), file + " " + marker + ": " + tag);
        }
        // A button alone in a .mud-actions row spans it (and a pair shares it evenly).
        Assert.Contains(".mud-actions > .mud-btn { flex: 1 1 0; }", Read("MUD.Web/wwwroot/css/mobile.css"));
    }

    [Fact]
    public void NoHardCodedColourInThePinnedBarOrGuidelineRules()
    {
        var css = Read("MUD.Web/wwwroot/css/mobile.css");
        foreach (var line in css.Split('\n').Where(l => l.StartsWith(".mud-pinbar") || l.StartsWith(".mud-actions > ") || l.StartsWith(".mud-btn-block")))
        {
            Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,6}\b|\brgb\(", line);
        }
    }

    [Fact]
    public void TheGuidelinesAreWrittenDown()
    {
        var spec = Read("specs/WEB_BLAZOR.md");
        Assert.Contains("Website UI guidelines", spec);
        foreach (var rule in new[] { "pinned Save bar", "full width", "No Cancel", "<details>", "native `<select>`", "palette", "44px" })
        {
            Assert.Contains(rule, spec);
        }
    }
}
