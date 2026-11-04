using AutoAudioPan.Core;
using System.Text.Json;
using Xunit;

namespace AutoAudioPan.Tests;

public class KnownAppsTests
{
    [Fact]
    public void SeedAddsOnceAndIsCaseInsensitive()
    {
        var k = new KnownApps(new List<KnownAppEntry>());
        Assert.True(k.Seed());
        Assert.False(k.Seed());
        Assert.True(k.IsKnown("Chrome.EXE"));
    }

    [Fact]
    public void RegisterAndRemove()
    {
        var k = new KnownApps(new List<KnownAppEntry>());
        Assert.True(k.Register("foo.exe", KnownSource.Learned));
        Assert.False(k.Register("FOO.exe", KnownSource.User));
        Assert.True(k.Remove("foo.exe"));
        Assert.False(k.IsKnown("foo.exe"));
    }

    [Fact]
    public void GroupingDefaultsToExeAndSupportsUserGroups()
    {
        Assert.Equal("chrome.exe", Grouping.GroupId("Chrome.exe", null));
        var id = Grouping.NewUserGroupId("Work");
        Assert.Equal(id, Grouping.GroupId("x.exe", id));
        var renames = new Dictionary<string, string>();
        Assert.Equal("Work", Grouping.DisplayName(id, "X", renames));
        Assert.Equal("X", Grouping.DisplayName("x.exe", "X", renames));
        renames["x.exe"] = "Mine";
        Assert.Equal("Mine", Grouping.DisplayName("x.exe", "X", renames));
    }

    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, true)]
    [InlineData(false, true, true, false, false)]
    [InlineData(false, true, true, true, true)]
    public void RowsRequireSessionSoundUnlessHiddenAppsAreShown(
        bool active,
        bool producedSound,
        bool hidden,
        bool showHidden,
        bool visible)
    {
        Assert.Equal(visible, RowVisibility.ShouldDisplay(active, producedSound, hidden, showHidden));
    }

    [Fact]
    public void HiddenStateIsStoredSeparatelyFromSessionSound()
    {
        var setting = new AppSetting { Hidden = true };
        var roundTrip = JsonSerializer.Deserialize<AppSetting>(JsonSerializer.Serialize(setting));

        Assert.NotNull(roundTrip);
        Assert.True(roundTrip!.Hidden);
    }
}
