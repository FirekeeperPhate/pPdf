using System.Windows;
using System.Windows.Media;
using pPdf.Annotations;

namespace pPdf.Tests;

public sealed class PersistenceTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ppdf-persist-" + Guid.NewGuid().ToString("N"));
    AnnotationPersistence P => new(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    static byte[] Png(Color c)
    {
        var px = new byte[16 * 8 * 4];
        for (int i = 0; i < 16 * 8; i++) { px[i * 4] = c.B; px[i * 4 + 1] = c.G; px[i * 4 + 2] = c.R; px[i * 4 + 3] = 255; }
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(16, 8, 96, 96, PixelFormats.Bgra32, null, px, 64)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Text_image_and_markup_come_back_equal_and_editable()
    {
        const string pdf = @"C:\docs\book.pdf";
        var text = new TextAnnotation { Page = 2, X = 10, Y = 20, Text = "Ciao\nsecondo \u00e8", FontFamily = "Times New Roman", FontSize = 22, Bold = true, Italic = true, Foreground = Colors.Red, Background = Color.FromRgb(255, 255, 160), Width = 90, Height = 50 };
        var img = ImageAnnotation.FromBytes(Png(Colors.Green)); img.Page = 0; img.X = 5; img.Y = 6; img.Width = 40; img.Height = 20;
        var mark = MarkupAnnotation.Create(1, MarkupKind.Underline, Colors.Blue, [new Rect(10, 10, 100, 12), new Rect(10, 30, 60, 12)]);
        P.Save(pdf, [text, img, mark], new Dictionary<string, string> { ["name"] = "Marco", ["agree"] = "On" });

        var back = P.Load(pdf);
        Assert.Equal(3, back.Annotations.Count);
        var t = Assert.IsType<TextAnnotation>(back.Annotations.First(a => a is TextAnnotation));
        Assert.True(text.SameAs(t) || (t.Text == text.Text && t.FontFamily == text.FontFamily && t.FontSize == 22 && t.Bold && t.Italic && t.Foreground == Colors.Red && t.Background == Color.FromRgb(255, 255, 160) && t.Page == 2 && t.X == 10 && t.Y == 20));
        var i = Assert.IsType<ImageAnnotation>(back.Annotations.First(a => a is ImageAnnotation));
        Assert.Equal(img.Data, i.Data);
        Assert.Equal((40, 20, 5, 6), (i.Width, i.Height, i.X, i.Y));
        var m = Assert.IsType<MarkupAnnotation>(back.Annotations.First(a => a is MarkupAnnotation));
        Assert.Equal(MarkupKind.Underline, m.Kind);
        Assert.Equal(2, m.Rects.Count);
        Assert.Equal(1, m.Page);
        Assert.Equal("Marco", back.FormValues["name"]);
    }

    [Fact]
    public void Documents_are_kept_apart_by_path_and_nothing_is_kept_for_an_empty_list()
    {
        var a = new TextAnnotation { Page = 0, Text = "A" };
        P.Save(@"C:\one.pdf", [a]);
        Assert.Single(P.Load(@"C:\ONE.pdf").Annotations);       // same file, other spelling
        Assert.Empty(P.Load(@"C:\two.pdf").Annotations);
        P.Save(@"C:\one.pdf", []);                              // all annotations deleted
        Assert.Empty(P.Load(@"C:\one.pdf").Annotations);
        Assert.False(Directory.Exists(P.DirectoryFor(@"C:\one.pdf")));
    }

    [Fact]
    public void Unused_images_are_removed_and_a_damaged_file_gives_nothing_instead_of_crashing()
    {
        var img = ImageAnnotation.FromBytes(Png(Colors.Red)); img.Width = 10; img.Height = 10;
        P.Save(@"C:\x.pdf", [img]);
        Assert.Single(Directory.GetFiles(P.DirectoryFor(@"C:\x.pdf"), "img-*"));
        P.Save(@"C:\x.pdf", [new TextAnnotation { Text = "only text" }]);
        Assert.Empty(Directory.GetFiles(P.DirectoryFor(@"C:\x.pdf"), "img-*"));

        File.WriteAllText(Path.Combine(P.DirectoryFor(@"C:\x.pdf"), "annotations.json"), "{ not json");
        Assert.True(P.Load(@"C:\x.pdf").IsEmpty);
    }

    [Fact]
    public void A_missing_image_loses_only_that_annotation()
    {
        var img = ImageAnnotation.FromBytes(Png(Colors.Red)); img.Width = 10; img.Height = 10;
        var text = new TextAnnotation { Text = "stays" };
        P.Save(@"C:\y.pdf", [img, text]);
        foreach (var f in Directory.GetFiles(P.DirectoryFor(@"C:\y.pdf"), "img-*")) File.Delete(f);
        var back = P.Load(@"C:\y.pdf");
        Assert.Single(back.Annotations);
        Assert.IsType<TextAnnotation>(back.Annotations[0]);
    }

    [Fact]
    public void Prune_keeps_the_most_recent_documents()
    {
        for (int n = 0; n < 6; n++)
        {
            P.Save($@"C:\p{n}.pdf", [new TextAnnotation { Text = "x" }]);
            Directory.SetLastWriteTimeUtc(P.DirectoryFor($@"C:\p{n}.pdf"), DateTime.UtcNow.AddMinutes(-60 + n));
        }
        P.Prune(keep: 3);
        Assert.Equal(3, Directory.GetDirectories(_root).Length);
        Assert.NotEmpty(P.Load(@"C:\p5.pdf").Annotations);
        Assert.Empty(P.Load(@"C:\p0.pdf").Annotations);
    }

    [Fact]
    public void Restoring_kept_annotations_leaves_the_store_clean_and_without_undo_steps()
    {
        var store = new AnnotationStore();
        store.LoadKept([new TextAnnotation { Text = "kept" }]);
        Assert.Single(store.Items);
        Assert.False(store.CanUndo);
        Assert.False(store.IsDirty);
        store.Add(new TextAnnotation { Text = "new" });
        Assert.True(store.IsDirty);
        Assert.True(store.CanUndo);
    }
}
