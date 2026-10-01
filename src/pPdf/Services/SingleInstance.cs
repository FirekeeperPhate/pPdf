using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace pPdf.Services;

/// <summary>
/// One pPdf process for everything: starting pPdf again (a double-click on another PDF in Explorer) hands the file to the
/// running one, which opens it in a window of its own, instead of paying for a whole second process (runtime, WPF, caches).
/// </summary>
public static class SingleInstance
{
    static Mutex? _mutex;
    static string? _suffix;

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Separate per settings folder, so a test run with its own data folder never talks to the real app.</summary>
    static string Suffix => _suffix ??= Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppSettings.Folder.ToLowerInvariant())))[..12];

    static string MutexName => @"Local\pPdf.Instance." + Suffix;
    static string PipeName => "pPdf.Pipe." + Suffix;

    /// <summary>True for the first pPdf: it keeps the lock for as long as it lives.</summary>
    public static bool TryBecomePrimary()
    {
        _mutex = new Mutex(true, MutexName, out bool created);
        return created;
    }

    /// <summary>Sends the files to the primary instance (an empty list just brings its window forward). False if it did not answer.</summary>
    public static bool Forward(IReadOnlyList<string> files)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            // this process was just started by the user, so it may give the foreground to the one that receives the work
            AllowSetForegroundWindow(-1);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            foreach (string f in files) writer.WriteLine(Path.GetFullPath(f));
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Listens (on a background thread) for the files other pPdf starts hand over; <paramref name="onFiles"/> runs on that thread.</summary>
    public static void StartServer(Action<string[]> onFiles)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server, new UTF8Encoding(false));
                    var files = new List<string>();
                    string? line;
                    while ((line = reader.ReadLine()) != null && files.Count < 64)
                        if (line.Length is > 0 and < 32768) files.Add(line);
                    onFiles(files.ToArray());
                }
                catch (IOException) { Thread.Sleep(200); }
                catch (UnauthorizedAccessException) { return; }
            }
        })
        { IsBackground = true, Name = "pPdf instance pipe" };
        thread.Start();
    }
}
