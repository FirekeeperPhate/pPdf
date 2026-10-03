using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using pPdf.Services;

namespace pPdf.Epub;

/// <summary>
/// Shows an EPUB by turning it into a PDF: the book is unpacked, its chapters joined into one HTML page, and the Edge WebView2 engine
/// (already part of Windows) lays that page out into book-sized pages with a real outline. The PDF is kept in a cache, so the next time
/// the same book is opened it appears at once, and everything pPdf does with a PDF (search, selection, notes, print) just works.
/// </summary>
public static class EpubConverter
{
    /// <summary>Changes when the layout changes, so books are laid out again.</summary>
    const int LayoutVersion = 1;
    const int MaxCachedBooks = 12;

    public static bool IsEpub(string path) => string.Equals(Path.GetExtension(path), ".epub", StringComparison.OrdinalIgnoreCase);

    static string CacheFolder => Path.Combine(AppSettings.CacheFolder, "epub");

    /// <summary>Where the PDF of a book is (or will be) kept: it depends on the file, its size and date, and the layout version.</summary>
    public static string CachePathFor(string epubPath)
    {
        var info = new FileInfo(epubPath);
        string key = $"{Path.GetFullPath(epubPath).ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{LayoutVersion}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        return Path.Combine(CacheFolder, hash + ".pdf");
    }

    /// <summary>
    /// The PDF of a book. Runs on the UI thread (the WebView2 engine needs one): the heavy parts are awaited, never blocked on.
    /// <paramref name="owner"/> is the window handle the hidden browser is attached to.
    /// </summary>
    public static async Task<string> ConvertAsync(string epubPath, IntPtr owner, IProgress<string>? progress = null)
    {
        string pdfPath = CachePathFor(epubPath);
        if (File.Exists(pdfPath) && new FileInfo(pdfPath).Length > 0)
        {
            try { File.SetLastWriteTimeUtc(pdfPath, DateTime.UtcNow); } catch (IOException) { }
            return pdfPath;
        }

        string version;
        try { version = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            throw new EpubException("Reading EPUB books needs the Microsoft Edge WebView2 Runtime, which is not installed on this PC.\n" +
                                    "It is free: https://developer.microsoft.com/microsoft-edge/webview2/");
        }
        if (string.IsNullOrEmpty(version)) throw new EpubException("The Microsoft Edge WebView2 Runtime is not available.");

        string work = Path.Combine(Path.GetTempPath(), "pPdf-epub-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            progress?.Report("Reading the book...");
            var book = await Task.Run(() =>
            {
                Directory.CreateDirectory(work);
                var b = EpubBook.Open(epubPath, work);
                File.WriteAllText(Path.Combine(work, EpubBook.PageName), b.BuildHtml(), new UTF8Encoding(false));
                return b;
            });

            progress?.Report("Laying out the pages...");
            byte[] pdf;
            try { pdf = await PrintAsync(work, owner); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException
                                           or WebView2RuntimeNotFoundException or System.ComponentModel.Win32Exception or JsonException or FormatException)
            {
                throw new EpubException("The book could not be laid out: " + ex.Message);
            }

            Directory.CreateDirectory(CacheFolder);
            string tmp = pdfPath + ".tmp";
            await File.WriteAllBytesAsync(tmp, pdf);
            File.Move(tmp, pdfPath, overwrite: true);
            _ = book;
            return pdfPath;
        }
        finally
        {
            _ = Task.Run(() => TryDeleteFolder(work));
        }
    }

    static async Task<byte[]> PrintAsync(string folder, IntPtr owner)
    {
        string userData = Path.Combine(AppSettings.CacheFolder, "webview");
        var env = await CoreWebView2Environment.CreateAsync(null, userData, new CoreWebView2EnvironmentOptions("--disable-gpu --disable-features=msSmartScreenProtection"));
        var controller = await env.CreateCoreWebView2ControllerAsync(owner);
        try
        {
            controller.IsVisible = false;
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 900, 700);
            var web = controller.CoreWebView2;
            web.Settings.IsScriptEnabled = false;          // a book is text and pictures: its scripts never run
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.Settings.IsStatusBarEnabled = false;
            web.SetVirtualHostNameToFolderMapping(EpubBook.Host, folder, CoreWebView2HostResourceAccessKind.Allow);

            // nothing leaves the PC: only the unpacked book is served, every other address (remote images, fonts, trackers) is refused
            web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            web.WebResourceRequested += (_, e) =>
            {
                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || !string.Equals(uri.Host, EpubBook.Host, StringComparison.OrdinalIgnoreCase))
                    e.Response = web.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            };

            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            web.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            web.Navigate($"https://{EpubBook.Host}/{EpubBook.PageName}");
            if (await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(3))) != loaded.Task || !loaded.Task.Result)
                throw new EpubException("The book could not be laid out.");

            string args = """
                {"printBackground":true,"preferCSSPageSize":true,"generateDocumentOutline":true,"displayHeaderFooter":false,"transferMode":"ReturnAsStream"}
                """;
            string reply = await web.CallDevToolsProtocolMethodAsync("Page.printToPDF", args);
            using var doc = JsonDocument.Parse(reply);
            if (!doc.RootElement.TryGetProperty("stream", out var streamId))
                throw new EpubException("The book could not be turned into pages.");
            return await ReadStreamAsync(web, streamId.GetString()!);
        }
        finally
        {
            controller.Close();
        }
    }

    /// <summary>Reads a DevTools IO stream in pieces (a long book is tens of megabytes: not something to receive as one message).</summary>
    static async Task<byte[]> ReadStreamAsync(CoreWebView2 web, string handle)
    {
        using var result = new MemoryStream();
        while (true)
        {
            string reply = await web.CallDevToolsProtocolMethodAsync("IO.read", $"{{\"handle\":\"{handle}\",\"size\":1048576}}");
            using var piece = JsonDocument.Parse(reply);
            var root = piece.RootElement;
            string data = root.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";
            bool base64 = root.TryGetProperty("base64Encoded", out var b) && b.GetBoolean();
            byte[] bytes = base64 ? Convert.FromBase64String(data) : Encoding.Latin1.GetBytes(data);
            result.Write(bytes);
            if (root.TryGetProperty("eof", out var eof) && eof.GetBoolean()) break;
        }
        await web.CallDevToolsProtocolMethodAsync("IO.close", $"{{\"handle\":\"{handle}\"}}");
        return result.ToArray();
    }

    // ------------------------------------------------------------------ housekeeping

    static void TryDeleteFolder(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Forgets the oldest converted books beyond <see cref="MaxCachedBooks"/>, leftovers of interrupted conversions, and the browser's scratch folder.</summary>
    public static void PruneCache()
    {
        try
        {
            if (Directory.Exists(CacheFolder))
            {
                var files = new DirectoryInfo(CacheFolder).EnumerateFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToList();
                for (int i = 0; i < files.Count; i++)
                    if (i >= MaxCachedBooks || files[i].Extension == ".tmp") try { files[i].Delete(); } catch (IOException) { }
            }
            string scratch = Path.Combine(AppSettings.CacheFolder, "webview");
            TryDeleteFolder(scratch);
            foreach (string leftover in Directory.EnumerateDirectories(Path.GetTempPath(), "pPdf-epub-*"))
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(leftover) > TimeSpan.FromHours(1)) TryDeleteFolder(leftover);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
