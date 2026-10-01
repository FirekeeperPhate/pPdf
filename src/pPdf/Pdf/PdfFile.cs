using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFiumCore;

namespace pPdf.Pdf;

public class PdfException(string message) : Exception(message);

public sealed class PdfPasswordException(string message) : PdfException(message);

/// <summary>Size of a page in points (1/72 in) as it is displayed, i.e. after the page's own /Rotate.</summary>
public readonly record struct PageSize(double Width, double Height);

/// <summary>One entry of the document outline (bookmarks).</summary>
public sealed class OutlineNode
{
    public string Title { get; init; } = "";
    public int PageIndex { get; init; } = -1;
    public List<OutlineNode> Children { get; } = [];
}

/// <summary>
/// A PDF document backed by PDFium. PDFium is not thread safe: every call into it happens under <see cref="Sync"/>,
/// and the UI thread never takes that lock (it works on the results cached by the background loaders).
/// Normal files are read into memory, so the original can be overwritten while it is open; very big ones are read from disk on demand.
/// </summary>
public sealed class PdfFile : IDisposable
{
    internal static readonly object Sync = new();
    static bool _initialized;

    /// <summary>Files bigger than this are read from disk when PDFium needs a part of them, instead of being held in memory.</summary>
    internal static long OnDemandThreshold = 64L << 20;

    readonly byte[]? _data;
    // on-demand files: the open file, and the callback object PDFium keeps calling (both must outlive the document)
    readonly SafeFileHandle? _file;
    readonly FPDF_FILEACCESS? _access;
    readonly PDFiumCore.Delegates.Func_int___IntPtr_uint_bytePtr_uint? _getBlock;
    FpdfDocumentT? _doc;

    public string? Path { get; }
    public string? Password { get; }
    public int PageCount { get; }
    public PageSize[] Pages { get; }

    /// <summary>True when the file stays on disk and is read on demand (it cannot be overwritten while it is open).</summary>
    public bool IsOnDemand => _file != null;

    /// <summary>The bytes of the document (for saving a copy): the in-memory copy, or the file read again.</summary>
    public byte[] GetBytes() => _data ?? File.ReadAllBytes(Path!);

    PdfFile(string? path, byte[]? data, SafeFileHandle? file, FPDF_FILEACCESS? access, PDFiumCore.Delegates.Func_int___IntPtr_uint_bytePtr_uint? getBlock,
        FpdfDocumentT doc, string? password)
    {
        Path = path;
        _data = data;
        _file = file;
        _access = access;
        _getBlock = getBlock;
        _doc = doc;
        Password = password;
        lock (Sync)
        {
            PageCount = fpdfview.FPDF_GetPageCount(doc);
            Pages = new PageSize[PageCount];
            for (int i = 0; i < PageCount; i++)
            {
                double w = 612, h = 792;
                fpdfview.FPDF_GetPageSizeByIndex(doc, i, ref w, ref h);
                if (!(w > 0) || !(h > 0)) { w = 612; h = 792; }
                Pages[i] = new PageSize(w, h);
            }
        }
    }

    static void EnsureLibrary()
    {
        lock (Sync)
        {
            if (_initialized) return;
            fpdfview.FPDF_InitLibrary();
            _initialized = true;
        }
    }

    public static PdfFile Open(string path, string? password = null)
    {
        long length = new FileInfo(path).Length;
        if (length > OnDemandThreshold && length <= uint.MaxValue) return OpenOnDemand(path, length, password);

        // read straight into the pinned buffer PDFium will keep using: no second copy of a big file
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var pinned = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length), pinned: true);
        stream.ReadExactly(pinned);
        return OpenPinned(pinned, password, path);
    }

    public static PdfFile Open(byte[] bytes, string? password = null, string? path = null)
    {
        var pinned = GC.AllocateUninitializedArray<byte>(bytes.Length, pinned: true);
        bytes.CopyTo(pinned, 0);
        return OpenPinned(pinned, password, path);
    }

    static PdfFile OpenPinned(byte[] pinned, string? password, string? path)
    {
        EnsureLibrary();
        FpdfDocumentT? doc;
        lock (Sync)
        {
            var ptr = Marshal.UnsafeAddrOfPinnedArrayElement(pinned, 0);
            doc = fpdfview.FPDF_LoadMemDocument64(ptr, (ulong)pinned.Length, password);
            if (doc == null) throw LoadError(password);
        }
        return new PdfFile(path, pinned, null, null, null, doc, password);
    }

    /// <summary>A big file is not loaded: PDFium asks for the blocks it needs (the xref, the pages on screen).</summary>
    static unsafe PdfFile OpenOnDemand(string path, long length, string? password)
    {
        EnsureLibrary();
        var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        PDFiumCore.Delegates.Func_int___IntPtr_uint_bytePtr_uint getBlock = (param, position, buffer, size) =>
        {
            try
            {
                uint done = 0;
                while (done < size)
                {
                    int n = RandomAccess.Read(file, new Span<byte>(buffer + done, (int)(size - done)), position + done);
                    if (n <= 0) return 0;
                    done += (uint)n;
                }
                return 1;
            }
            catch (Exception) { return 0; } // never let an exception cross into native code
        };
        var access = new FPDF_FILEACCESS { MFileLen = (uint)length, MGetBlock = getBlock, MParam = IntPtr.Zero };
        FpdfDocumentT? doc;
        lock (Sync)
        {
            doc = fpdfview.FPDF_LoadCustomDocument(access, password);
            if (doc == null)
            {
                var error = LoadError(password);
                file.Dispose();
                throw error;
            }
        }
        return new PdfFile(path, null, file, access, getBlock, doc, password);
    }

    static PdfException LoadError(string? password)
    {
        uint err = fpdfview.FPDF_GetLastError();
        return err switch
        {
            4 => new PdfPasswordException(password == null ? "This document is password protected." : "Wrong password."),
            3 => new PdfException("The file is not a valid PDF document."),
            2 => new PdfException("The file could not be read."),
            5 => new PdfException("The document uses an unsupported security handler."),
            _ => new PdfException("The document could not be opened (error " + err + ")."),
        };
    }

    public void Dispose()
    {
        lock (Sync)
        {
            if (_doc == null) return;
            fpdfview.FPDF_CloseDocument(_doc);
            _doc = null;
        }
        _file?.Dispose();
        GC.KeepAlive(_access);
        GC.KeepAlive(_getBlock);
    }

    public bool IsDisposed => _doc == null;

    // ------------------------------------------------------------------ rendering

    const int FlagAnnot = 0x01;
    const int FlagLcdText = 0x02;
    const int FlagPrinting = 0x800;

    /// <summary>
    /// Renders a region of a page. The page is laid out as if it were drawn at <paramref name="fullWidth"/> x <paramref name="fullHeight"/>
    /// pixels (already rotated by <paramref name="rotation"/> quarter turns clockwise); only <paramref name="region"/> of that area is
    /// rasterised, which keeps deep zooms cheap. Returns a frozen bitmap, or null if the document was closed.
    /// </summary>
    public BitmapSource? Render(int pageIndex, int fullWidth, int fullHeight, Int32Rect region, int rotation, bool invert, bool printing = false)
    {
        if (region.Width <= 0 || region.Height <= 0) return null;
        lock (Sync)
        {
            if (_doc == null) return null;
            var page = fpdfview.FPDF_LoadPage(_doc, pageIndex);
            if (page == null) return null;
            var bitmap = fpdfview.FPDFBitmapCreate(region.Width, region.Height, 0);
            if (bitmap == null) { fpdfview.FPDF_ClosePage(page); return null; }
            try
            {
                fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, region.Width, region.Height, 0xFFFFFFFF);
                int flags = FlagAnnot | (printing ? FlagPrinting : 0);
                fpdfview.FPDF_RenderPageBitmap(bitmap, page, -region.X, -region.Y, fullWidth, fullHeight, rotation & 3, flags);
                var buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
                int stride = fpdfview.FPDFBitmapGetStride(bitmap);
                if (invert) InvertPixels(buffer, stride, region.Width, region.Height);
                var src = BitmapSource.Create(region.Width, region.Height, 96, 96, PixelFormats.Bgr32, null, buffer, stride * region.Height, stride);
                src.Freeze();
                return src;
            }
            finally
            {
                fpdfview.FPDFBitmapDestroy(bitmap);
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>
    /// Night reading: flips the lightness but keeps the hues (the "invert + hue-rotate 180" trick), so photos and colored charts
    /// stay recognisable instead of turning into negatives. White paper becomes a soft dark gray, black text a soft light gray.
    /// </summary>
    static unsafe void InvertPixels(IntPtr buffer, int stride, int width, int height)
    {
        // 255 - H*c with H the 180 degree hue rotation (luminance weights), in 10 bit fixed point; rows sum to exactly 1024
        const int Rr = -588, Rg = 1464, Rb = 148;
        const int Gr = 436, Gg = 440, Gb = 148;
        const int Br = 436, Bg = 1464, Bb = -876;
        // map 0..255 onto 24..232
        var table = stackalloc byte[256];
        for (int i = 0; i < 256; i++) table[i] = (byte)(24 + i * 208 / 255);
        for (int y = 0; y < height; y++)
        {
            uint* row = (uint*)((byte*)buffer + (long)y * stride);
            for (int x = 0; x < width; x++)
            {
                uint p = row[x];
                int b = (int)(p & 0xFF), g = (int)((p >> 8) & 0xFF), r = (int)((p >> 16) & 0xFF);
                int nr = 255 - Math.Clamp((Rr * r + Rg * g + Rb * b) >> 10, 0, 255);
                int ng = 255 - Math.Clamp((Gr * r + Gg * g + Gb * b) >> 10, 0, 255);
                int nb = 255 - Math.Clamp((Br * r + Bg * g + Bb * b) >> 10, 0, 255);
                row[x] = 0xFF000000u | ((uint)table[nr] << 16) | ((uint)table[ng] << 8) | table[nb];
            }
        }
    }

    // ------------------------------------------------------------------ text

    /// <summary>Extracts the text of a page and the box of every character, in display space (points, top-left origin).</summary>
    public PageText? LoadText(int pageIndex, bool withBoxes)
    {
        lock (Sync)
        {
            if (_doc == null) return null;
            var page = fpdfview.FPDF_LoadPage(_doc, pageIndex);
            if (page == null) return null;
            FpdfTextpageT? tp = null;
            try
            {
                tp = fpdf_text.FPDFTextLoadPage(page);
                if (tp == null) return new PageText(pageIndex, "", withBoxes ? [] : null);
                int n = fpdf_text.FPDFTextCountChars(tp);
                var sb = new StringBuilder(n + 16);
                var boxes = withBoxes ? new List<RectF>(n + 16) : null;

                var map = withBoxes ? PageToDisplay(page, Pages[pageIndex]) : default;
                for (int i = 0; i < n; i++)
                {
                    uint u = fpdf_text.FPDFTextGetUnicode(tp, i);
                    RectF box = default;
                    if (withBoxes)
                    {
                        double l = 0, r = 0, b = 0, t = 0;
                        if (fpdf_text.FPDFTextGetCharBox(tp, i, ref l, ref r, ref b, ref t) != 0)
                            box = map.Transform(l, b, r, t);
                    }
                    if (u == 0) u = ' ';
                    if (u > 0xFFFF)
                    {
                        // a supplementary-plane character: two UTF-16 units that share one box
                        string s = char.ConvertFromUtf32((int)u);
                        sb.Append(s);
                        boxes?.Add(box);
                        boxes?.Add(box);
                    }
                    else
                    {
                        sb.Append((char)u);
                        boxes?.Add(box);
                    }
                }
                return new PageText(pageIndex, sb.ToString(), boxes?.ToArray());
            }
            finally
            {
                if (tp != null) fpdf_text.FPDFTextClosePage(tp);
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>Affine map from PDF page space (bottom-left origin) to display space (points, top-left origin).</summary>
    readonly record struct DisplayMap(double A, double B, double C, double D, double E, double F)
    {
        public RectF Transform(double left, double bottom, double right, double top)
        {
            (double x1, double y1) = Apply(left, bottom);
            (double x2, double y2) = Apply(right, top);
            return new RectF((float)Math.Min(x1, x2), (float)Math.Min(y1, y2), (float)Math.Abs(x2 - x1), (float)Math.Abs(y2 - y1));
        }

        public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
    }

    static DisplayMap PageToDisplay(FpdfPageT page, PageSize size)
    {
        // PDFium only converts to integer device pixels: ask for 100 device units per point and for points far apart
        const int K = 100;
        const double Span = 1000;
        int sx = (int)Math.Round(size.Width * K), sy = (int)Math.Round(size.Height * K);
        int x0 = 0, y0 = 0, x1 = 0, y1 = 0, x2 = 0, y2 = 0;
        fpdfview.FPDF_PageToDevice(page, 0, 0, sx, sy, 0, 0, 0, ref x0, ref y0);
        fpdfview.FPDF_PageToDevice(page, 0, 0, sx, sy, 0, Span, 0, ref x1, ref y1);
        fpdfview.FPDF_PageToDevice(page, 0, 0, sx, sy, 0, 0, Span, ref x2, ref y2);
        double a = (x1 - x0) / (Span * K), b = (y1 - y0) / (Span * K);
        double c = (x2 - x0) / (Span * K), d = (y2 - y0) / (Span * K);
        return new DisplayMap(a, b, c, d, x0 / (double)K, y0 / (double)K);
    }


    // ------------------------------------------------------------------ document information

    public PdfInfo ReadInfo()
    {
        lock (Sync)
        {
            if (_doc == null) return new PdfInfo("", "", "", "", "", "", null, null, "", false, false);
            string Meta(string tag)
            {
                uint bytes = fpdf_doc.FPDF_GetMetaText(_doc, tag, IntPtr.Zero, 0);
                if (bytes <= 2) return "";
                var mem = Marshal.AllocHGlobal((int)bytes);
                try
                {
                    fpdf_doc.FPDF_GetMetaText(_doc, tag, mem, bytes);
                    return Marshal.PtrToStringUni(mem)?.Trim() ?? "";
                }
                finally { Marshal.FreeHGlobal(mem); }
            }
            int version = 0;
            string versionText = fpdfview.FPDF_GetFileVersion(_doc, ref version) != 0 && version > 0 ? $"{version / 10}.{version % 10}" : "";
            bool encrypted = fpdfview.FPDF_GetSecurityHandlerRevision(_doc) >= 0;
            int formType = fpdf_formfill.FPDF_GetFormType(_doc);
            return new PdfInfo(Meta("Title"), Meta("Author"), Meta("Subject"), Meta("Keywords"), Meta("Creator"), Meta("Producer"),
                PdfInfo.ParseDate(Meta("CreationDate")), PdfInfo.ParseDate(Meta("ModDate")), versionText, encrypted, formType > 0);
        }
    }

    /// <summary>1 = AcroForm, 2 / 3 = XFA; 0 = no form.</summary>
    public int FormType
    {
        get
        {
            lock (Sync) return _doc == null ? 0 : Math.Max(0, fpdf_formfill.FPDF_GetFormType(_doc));
        }
    }

    // ------------------------------------------------------------------ links

    /// <summary>Links of a page (internal destinations and URIs), plus plain-text URLs, in display space.</summary>
    public List<PageLink> LoadLinks(int pageIndex)
    {
        var result = new List<PageLink>();
        lock (Sync)
        {
            if (_doc == null) return result;
            var page = fpdfview.FPDF_LoadPage(_doc, pageIndex);
            if (page == null) return result;
            try
            {
                var map = PageToDisplay(page, Pages[pageIndex]);
                int count = fpdf_annot.FPDFPageGetAnnotCount(page);
                for (int ai = 0; ai < count; ai++)
                {
                    var annot = fpdf_annot.FPDFPageGetAnnot(page, ai);
                    if (annot == null) continue;
                    try
                    {
                        if (fpdf_annot.FPDFAnnotGetSubtype(annot) != 2) continue; // 2 = link
                        var holder = fpdf_annot.FPDFAnnotGetLink(annot);
                        if (holder == null) continue;
                        var rect = new FS_RECTF_();
                        if (fpdf_annot.FPDFAnnotGetRect(annot, rect) == 0) continue;
                        var box = map.Transform(rect.Left, rect.Bottom, rect.Right, rect.Top);
                        AddLink(result, holder, box);
                    }
                    finally { fpdf_annot.FPDFPageCloseAnnot(annot); }
                }
            }
            catch
            {
                // links are a convenience: a document with a broken link table still opens
            }
            finally { fpdfview.FPDF_ClosePage(page); }
        }
        return result;
    }

    void AddLink(List<PageLink> result, FpdfLinkT holder, RectF box)
    {
        int dest = -1;
        string? uri = null;
        var action = fpdf_doc.FPDFLinkGetAction(holder);
        if (action != null)
        {
            uint type = fpdf_doc.FPDFActionGetType(action);
            if (type == 3) // URI
            {
                uint len = fpdf_doc.FPDFActionGetURIPath(_doc, action, IntPtr.Zero, 0);
                if (len > 1)
                {
                    var mem = Marshal.AllocHGlobal((int)len);
                    try
                    {
                        fpdf_doc.FPDFActionGetURIPath(_doc, action, mem, len);
                        uri = Marshal.PtrToStringAnsi(mem);
                    }
                    finally { Marshal.FreeHGlobal(mem); }
                }
            }
            else
            {
                var d = fpdf_doc.FPDFActionGetDest(_doc, action);
                if (d != null) dest = fpdf_doc.FPDFDestGetDestPageIndex(_doc, d);
            }
        }
        if (dest < 0 && uri == null)
        {
            var d = fpdf_doc.FPDFLinkGetDest(_doc, holder);
            if (d != null) dest = fpdf_doc.FPDFDestGetDestPageIndex(_doc, d);
        }
        if (dest >= 0 || !string.IsNullOrWhiteSpace(uri))
            result.Add(new PageLink(box, dest, uri));
    }

    // ------------------------------------------------------------------ outline

    public List<OutlineNode> LoadOutline()
    {
        var roots = new List<OutlineNode>();
        lock (Sync)
        {
            if (_doc == null) return roots;
            try { ReadBookmarks(null, roots, 0); }
            catch { /* a corrupt outline just means no outline */ }
        }
        return roots;
    }

    void ReadBookmarks(FpdfBookmarkT? parent, List<OutlineNode> into, int depth)
    {
        if (depth > 32) return;
        var bm = fpdf_doc.FPDFBookmarkGetFirstChild(_doc, parent);
        int guard = 0;
        while (bm != null && guard++ < 100000)
        {
            var node = new OutlineNode
            {
                Title = BookmarkTitle(bm),
                PageIndex = BookmarkPage(bm),
            };
            ReadBookmarks(bm, node.Children, depth + 1);
            into.Add(node);
            bm = fpdf_doc.FPDFBookmarkGetNextSibling(_doc, bm);
        }
    }

    static string BookmarkTitle(FpdfBookmarkT bm)
    {
        uint bytes = fpdf_doc.FPDFBookmarkGetTitle(bm, IntPtr.Zero, 0);
        if (bytes <= 2) return "";
        var mem = Marshal.AllocHGlobal((int)bytes);
        try
        {
            fpdf_doc.FPDFBookmarkGetTitle(bm, mem, bytes);
            return Marshal.PtrToStringUni(mem) ?? "";
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    int BookmarkPage(FpdfBookmarkT bm)
    {
        var dest = fpdf_doc.FPDFBookmarkGetDest(_doc, bm);
        if (dest == null)
        {
            var action = fpdf_doc.FPDFBookmarkGetAction(bm);
            if (action != null) dest = fpdf_doc.FPDFActionGetDest(_doc, action);
        }
        return dest == null ? -1 : fpdf_doc.FPDFDestGetDestPageIndex(_doc, dest);
    }
}

public readonly record struct PageLink(RectF Box, int PageIndex, string? Uri);
