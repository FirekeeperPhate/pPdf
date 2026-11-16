using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace pPdf.Pdf;

/// <summary>One page of the document being put together: which file (0 = the open document), which page of it, and the extra turns.</summary>
public sealed record PagePick(int Source, int Index, int QuarterTurns = 0);

/// <summary>A PDF the pages may come from: its bytes and, if it is protected, its password.</summary>
public sealed record PageSource(byte[] Bytes, string? Password);

/// <summary>Builds a new PDF out of the pages of one or more documents: in any order, some left out, some turned.</summary>
public static class PageOrganizer
{
    /// <summary>
    /// The pages of <paramref name="picks"/> in that order. The first source is opened for modification and keeps what belongs to the
    /// document (its outline, its form, its metadata) for the pages that stay; pages of the other sources are copied in.
    /// </summary>
    public static byte[] Build(IReadOnlyList<PageSource> sources, IReadOnlyList<PagePick> picks)
    {
        if (sources.Count == 0) throw new ArgumentException("No document.", nameof(sources));
        if (picks.Count == 0) throw new ArgumentException("No pages.", nameof(picks));

        using var mainStream = new MemoryStream(sources[0].Bytes, writable: false);
        using var main = Open(mainStream, sources[0].Password, PdfDocumentOpenMode.Modify);
        var others = new Dictionary<int, (MemoryStream Stream, PdfDocument Doc)>();
        try
        {
            var originals = Enumerable.Range(0, main.PageCount).Select(i => main.Pages[i]).ToList();
            foreach (var p in picks)
            {
                int count = p.Source == 0 ? originals.Count : OtherDoc(p.Source).PageCount;
                if (p.Index < 0 || p.Index >= count) throw new ArgumentOutOfRangeException(nameof(picks), "A page that the document does not have.");
            }

            // a page of the open document picked twice: the second one is a copy of it
            var usedOnce = new HashSet<int>();
            var plan = picks.Select(p => (Pick: p, Own: p.Source == 0 && usedOnce.Add(p.Index))).ToList();

            // the pages of the open document that stay out
            var keep = plan.Where(x => x.Own).Select(x => x.Pick.Index).ToHashSet();
            for (int i = originals.Count - 1; i >= 0; i--)
                if (!keep.Contains(i)) main.Pages.Remove(originals[i]);

            // then each place in turn gets its page: moved there, or copied in from the other files
            Dictionary<int, (MemoryStream, PdfDocument)>? copies = null;
            for (int k = 0; k < plan.Count; k++)
            {
                var (pick, own) = plan[k];
                PdfPage page;
                if (own)
                {
                    page = originals[pick.Index];
                    int at = IndexOf(main, page);
                    if (at != k) main.Pages.MovePage(at, k);
                }
                else
                {
                    PdfDocument from;
                    if (pick.Source == 0)
                    {
                        // a second copy of an own page comes from a fresh import of the same bytes
                        copies ??= [];
                        if (!copies.TryGetValue(0, out var c))
                        {
                            var s = new MemoryStream(sources[0].Bytes, writable: false);
                            c = (s, Open(s, sources[0].Password, PdfDocumentOpenMode.Import));
                            copies[0] = c;
                        }
                        from = c.Item2;
                    }
                    else from = OtherDoc(pick.Source);
                    page = main.Pages.Insert(k, from.Pages[pick.Index]);
                }
                if (pick.QuarterTurns % 4 != 0)
                    page.Rotate = (((page.Rotate + pick.QuarterTurns * 90) % 360) + 360) % 360;
            }

            using var output = new MemoryStream();
            main.Save(output, false);
            foreach (var (s, d) in copies?.Values ?? Enumerable.Empty<(MemoryStream, PdfDocument)>()) { d.Dispose(); s.Dispose(); }
            return output.ToArray();
        }
        finally
        {
            foreach (var (s, d) in others.Values) { d.Dispose(); s.Dispose(); }
        }

        PdfDocument OtherDoc(int source)
        {
            if (source <= 0 || source >= sources.Count) throw new ArgumentOutOfRangeException(nameof(picks), "A file that is not in the list.");
            if (!others.TryGetValue(source, out var o))
            {
                var s = new MemoryStream(sources[source].Bytes, writable: false);
                o = (s, Open(s, sources[source].Password, PdfDocumentOpenMode.Import));
                others[source] = o;
            }
            return o.Doc;
        }
    }

    static PdfDocument Open(Stream s, string? password, PdfDocumentOpenMode mode)
        => password == null ? PdfReader.Open(s, mode) : PdfReader.Open(s, password, mode);

    static int IndexOf(PdfDocument doc, PdfPage page)
    {
        for (int i = 0; i < doc.PageCount; i++)
            if (ReferenceEquals(doc.Pages[i], page) || (page.Reference != null && doc.Pages[i].Reference == page.Reference)) return i;
        throw new InvalidOperationException("The page is not in the document any more.");
    }
}
