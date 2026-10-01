using System.Text.Json;
using pPdf.Services;
using pPdf.Viewer;

namespace pPdf.Tests;

public class PositionTests
{
    [Fact]
    public void Saved_position_comes_back_and_the_newest_is_first()
    {
        var s = new AppSettings();
        s.SavePosition(new DocPosition { Path = @"C:\a.pdf", Page = 3, OffsetY = 0.4, ZoomMode = ZoomMode.Custom, Zoom = 1.5, Rotation = 1 });
        s.SavePosition(new DocPosition { Path = @"C:\b.pdf", Page = 9 });
        s.SavePosition(new DocPosition { Path = @"C:\A.PDF", Page = 5, OffsetY = 0.8 }); // same file, other spelling: replaces
        Assert.Equal(2, s.Positions.Count);
        Assert.Equal(@"C:\A.PDF", s.Positions[0].Path);
        var found = s.FindPosition(@"c:\a.pdf")!;
        Assert.Equal(5, found.Page);
        Assert.Equal(0.8, found.OffsetY, 3);
        Assert.Null(s.FindPosition(@"C:\nothing.pdf"));
    }

    [Fact]
    public void Positions_survive_the_json_round_trip_with_their_view_state()
    {
        var s = new AppSettings { WindowLeft = 0, WindowTop = 0 }; // the defaults are NaN, which JSON cannot hold (the app always sets them before saving)
        s.SavePosition(new DocPosition { Path = @"C:\a.pdf", Page = 3, OffsetY = 0.25, ZoomMode = ZoomMode.Custom, Zoom = 1.75, Rotation = 3 });
        string json = JsonSerializer.Serialize(s, new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        var back = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        var p = back.FindPosition(@"C:\a.pdf")!;
        Assert.Equal(3, p.Page);
        Assert.Equal(0.25, p.OffsetY, 3);
        Assert.Equal(ZoomMode.Custom, p.ZoomMode);
        Assert.Equal(1.75, p.Zoom, 3);
        Assert.Equal(3, p.Rotation);
    }

    [Fact]
    public void A_position_from_an_older_version_gives_the_page_and_leaves_the_zoom_alone()
    {
        var s = new AppSettings();
        s.LastPages[@"C:\old.pdf"] = 17;
        var p = s.FindPosition(@"C:\old.pdf")!;
        Assert.Equal(17, p.Page);
        Assert.Null(p.ZoomMode);
        Assert.Null(p.Rotation);
        s.SavePosition(new DocPosition { Path = @"C:\old.pdf", Page = 18 });
        Assert.False(s.LastPages.ContainsKey(@"C:\old.pdf")); // migrated
    }

    [Fact]
    public void Only_the_most_recent_positions_are_kept()
    {
        var s = new AppSettings();
        for (int i = 0; i < AppSettings.MaxPositions + 30; i++) s.SavePosition(new DocPosition { Path = $@"C:\f{i}.pdf", Page = i });
        Assert.Equal(AppSettings.MaxPositions, s.Positions.Count);
        Assert.NotNull(s.FindPosition($@"C:\f{AppSettings.MaxPositions + 29}.pdf"));
        Assert.Null(s.FindPosition(@"C:\f0.pdf"));
    }
}
