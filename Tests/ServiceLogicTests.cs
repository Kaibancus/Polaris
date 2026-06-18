using System;
using System.Windows.Media;
using Polaris.Models;
using Polaris.Services;
using Xunit;

namespace Polaris.Tests;

/// <summary>
/// Unit tests for the pure (UI- and OS-free) logic in the Services layer. These
/// are the seams worth pinning: colour/version/title parsing, shell-token
/// classification, dock-mirror sizing and AUMID family matching.
/// </summary>
public class ServiceLogicTests
{
    // ---- ColorUtil.Parse -------------------------------------------------

    [Fact]
    public void Parse_HexColor_ReturnsParsed()
    {
        Assert.Equal(Color.FromRgb(0xFF, 0x00, 0x00), ColorUtil.Parse("#FF0000", Colors.Black));
    }

    [Fact]
    public void Parse_NamedColor_ReturnsParsed()
    {
        Assert.Equal(Colors.Red, ColorUtil.Parse("Red", Colors.Black));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-color")]
    public void Parse_BlankOrInvalid_ReturnsFallback(string input)
    {
        Assert.Equal(Colors.Blue, ColorUtil.Parse(input, Colors.Blue));
    }

    // ---- AttentionService.ParseUnread ------------------------------------

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("Inbox", 0)]
    [InlineData("(0) Inbox", 0)]          // zero is not an unread count
    [InlineData("(3) Inbox", 3)]          // leading parens
    [InlineData("[12] Messages", 12)]     // leading brackets
    [InlineData("WhatsApp (9) | Web", 9)] // count before a separator
    [InlineData("Slack (5) - team", 5)]
    public void ParseUnread_ExtractsCount(string? title, int expected)
    {
        Assert.Equal(expected, AttentionService.ParseUnread(title));
    }

    // ---- UpdateService.ExtractVersion ------------------------------------

    [Theory]
    [InlineData("v1.8.7", "1.8.7")]
    [InlineData("1.2", "1.2")]
    [InlineData("release-3", "3.0")]      // lone integer -> x.0
    public void ExtractVersion_ParsesTag(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdateService.ExtractVersion(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-digits-here")]
    public void ExtractVersion_NoVersion_ReturnsNull(string? tag)
    {
        Assert.Null(UpdateService.ExtractVersion(tag));
    }

    // ---- ShellNamespace.IsShellToken / NormalizeAppsFolderPath -----------

    [Theory]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", true)] // This PC CLSID
    [InlineData("shell:AppsFolder\\Microsoft.WindowsCalculator", true)]
    [InlineData("SHELL:RecycleBinFolder", true)]                   // case-insensitive
    [InlineData("C:\\Windows\\notepad.exe", false)]
    [InlineData("", false)]
    public void IsShellToken_Classifies(string token, bool expected)
    {
        Assert.Equal(expected, ShellNamespace.IsShellToken(token));
    }

    [Theory]
    [InlineData("C:\\Windows\\notepad.exe")]                       // path-like: unchanged
    [InlineData("shell:AppsFolder\\Already.Normalized")]           // already a shell token
    public void NormalizeAppsFolderPath_LeavesNonAumidPathsUnchanged(string path)
    {
        Assert.Equal(path, ShellNamespace.NormalizeAppsFolderPath(path));
    }

    // ---- DockSync --------------------------------------------------------

    [Fact]
    public void ResidentCount_AutoZero_UsesMaxCap()
    {
        var cfg = new AppConfig();
        cfg.Settings.Ring0Count = 0;
        Assert.Equal(DockSync.MaxResidentCount, DockSync.ResidentCount(cfg));
    }

    [Fact]
    public void ResidentCount_ExplicitValue_HonouredWithinCap()
    {
        var cfg = new AppConfig();
        cfg.Settings.Ring0Count = 3;
        Assert.Equal(3, DockSync.ResidentCount(cfg));
    }

    [Fact]
    public void ResidentCount_AboveCap_ClampedToMax()
    {
        var cfg = new AppConfig();
        cfg.Settings.Ring0Count = DockSync.MaxResidentCount + 50;
        Assert.Equal(DockSync.MaxResidentCount, DockSync.ResidentCount(cfg));
    }

    [Fact]
    public void Matches_SameTargetCaseInsensitive_True()
    {
        var a = new AppEntry { Path = @"C:\Apps\foo.exe", Arguments = "" };
        var b = new AppEntry { Path = @"c:\apps\FOO.EXE", Arguments = "" };
        Assert.True(DockSync.Matches(a, b));
    }

    [Fact]
    public void Matches_DifferentArguments_False()
    {
        var a = new AppEntry { Path = @"C:\Apps\foo.exe", Arguments = "" };
        var b = new AppEntry { Path = @"C:\Apps\foo.exe", Arguments = "--profile=2" };
        Assert.False(DockSync.Matches(a, b));
    }

    [Fact]
    public void AppendResident_AddsEntryToApps()
    {
        var cfg = new AppConfig();
        var entry = new AppEntry { Path = @"C:\Apps\new.exe" };
        DockSync.AppendResident(cfg, entry);
        Assert.Contains(entry, cfg.Apps);
    }

    // ---- LiquidGlassTheme.RowsFor ----------------------------------------

    [Theory]
    [InlineData(0, 4)]    // empty -> minimum visible rows
    [InlineData(7, 4)]    // one full row still clamps up to VisibleRows
    [InlineData(28, 4)]   // 4 rows
    [InlineData(29, 5)]   // spills onto a 5th row
    [InlineData(1000, 6)] // clamped to MaxRows
    public void RowsFor_ClampsBetweenVisibleAndMax(int count, int expected)
    {
        Assert.Equal(expected, LiquidGlassTheme.RowsFor(count));
    }

    // ---- WindowPreviewService.AumidFamilyMatches -------------------------

    [Fact]
    public void AumidFamilyMatches_SameFamilyDifferentApp_True()
    {
        Assert.True(WindowPreviewService.AumidFamilyMatches(
            "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            "Microsoft.WindowsCalculator_8wekyb3d8bbwe!OtherApp"));
    }

    [Fact]
    public void AumidFamilyMatches_DifferentFamily_False()
    {
        Assert.False(WindowPreviewService.AumidFamilyMatches(
            "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            "Microsoft.Paint_8wekyb3d8bbwe!App"));
    }

    [Theory]
    [InlineData(null, "x")]
    [InlineData("x", null)]
    [InlineData("", "")]
    public void AumidFamilyMatches_NullOrEmpty_False(string? a, string? b)
    {
        Assert.False(WindowPreviewService.AumidFamilyMatches(a, b));
    }
}
