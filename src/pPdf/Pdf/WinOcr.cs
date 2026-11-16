using System.Runtime.InteropServices;

namespace pPdf.Pdf;

/// <summary>
/// Windows' own text recognition (Windows.Media.Ocr, in the languages installed for the user), called through its COM interfaces
/// directly: the WinRT projection would add about 25 MB to the program for these few calls. Every call happens on the thread that
/// calls <see cref="Recognize"/>, which must not be a UI thread (it waits for the result).
/// </summary>
public static unsafe class WinOcr
{
    /// <summary>A recognized word with its box, in pixels of the picture it was read from.</summary>
    public readonly record struct Word(string Text, float X, float Y, float Width, float Height);

    static readonly Guid IID_IOcrEngineStatics = new("5BFFA85A-3384-3540-9940-699120D428A8");
    static readonly Guid IID_ISoftwareBitmapFactory = new("C99FEB69-2D62-4D47-A6B3-4FDB6A07FDF8");
    static readonly Guid IID_IAsyncInfo = new("00000036-0000-0000-C000-000000000046");
    static readonly Guid IID_IMemoryBuffer = new("FBC4DD2A-245B-11E4-AF98-689423260CF8");
    static readonly Guid IID_IMemoryBufferByteAccess = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");
    static readonly Guid IID_IClosable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");

    const int BitmapPixelFormatBgra8 = 87;
    const int BitmapBufferAccessModeWrite = 2;

    /// <summary>
    /// Reads the lines of text of a 32-bit BGRA / BGRx picture. Returns the lines (words in reading order), or null when Windows
    /// has no recognizer for the user's languages. Throws when the recognition itself fails.
    /// </summary>
    public static List<List<Word>>? Recognize(byte[] pixels, int width, int height, int stride, TimeSpan timeout)
    {
        IntPtr statics = IntPtr.Zero, engine = IntPtr.Zero, bitmapFactory = IntPtr.Zero, bitmap = IntPtr.Zero, op = IntPtr.Zero, result = IntPtr.Zero;
        // the thread joins the multithreaded apartment (a thread that is already in one keeps it)
        int init = RoInitialize(1);
        try
        {
            statics = Factory("Windows.Media.Ocr.OcrEngine", IID_IOcrEngineStatics);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(statics, 10))(statics, &engine)); // TryCreateFromUserProfileLanguages
            if (engine == IntPtr.Zero) return null;

            bitmapFactory = Factory("Windows.Graphics.Imaging.SoftwareBitmap", IID_ISoftwareBitmapFactory);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int, int, IntPtr*, int>)Slot(bitmapFactory, 6))(bitmapFactory, BitmapPixelFormatBgra8, width, height, &bitmap));
            Fill(bitmap, pixels, width, height, stride);

            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(engine, 6))(engine, bitmap, &op)); // RecognizeAsync
            Wait(op, timeout);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(op, 8))(op, &result)); // GetResults
            return ReadLines(result);
        }
        finally
        {
            Release(result); Release(op); Release(bitmap); Release(bitmapFactory); Release(engine); Release(statics);
            if (init >= 0) RoUninitialize(); // S_OK or S_FALSE: this call joined, so it leaves
        }
    }

    /// <summary>The largest side, in pixels, a picture given to the recognizer may have.</summary>
    public static int MaxImageDimension
    {
        get
        {
            int init = RoInitialize(1);
            IntPtr statics = IntPtr.Zero;
            try
            {
                statics = Factory("Windows.Media.Ocr.OcrEngine", IID_IOcrEngineStatics);
                uint max = 0;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(statics, 6))(statics, &max));
                return max > 0 ? (int)max : 2600;
            }
            finally
            {
                Release(statics);
                if (init >= 0) RoUninitialize();
            }
        }
    }

    static void Fill(IntPtr bitmap, byte[] pixels, int width, int height, int stride)
    {
        IntPtr buffer = IntPtr.Zero, memory = IntPtr.Zero, reference = IntPtr.Zero, access = IntPtr.Zero;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)Slot(bitmap, 15))(bitmap, BitmapBufferAccessModeWrite, &buffer)); // LockBuffer
            PlaneDescription plane;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int, PlaneDescription*, int>)Slot(buffer, 7))(buffer, 0, &plane)); // GetPlaneDescription
            memory = QueryInterface(buffer, IID_IMemoryBuffer);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(memory, 6))(memory, &reference)); // CreateReference
            access = QueryInterface(reference, IID_IMemoryBufferByteAccess);
            byte* data; uint capacity;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, int>)Slot(access, 3))(access, &data, &capacity)); // GetBuffer
            int rowBytes = width * 4;
            if (plane.Stride < rowBytes || (long)plane.StartIndex + (long)plane.Stride * (height - 1) + rowBytes > capacity)
                throw new InvalidOperationException("The picture buffer is smaller than expected.");
            fixed (byte* src = pixels)
                for (int y = 0; y < height; y++)
                    Buffer.MemoryCopy(src + (long)y * stride, data + plane.StartIndex + (long)y * plane.Stride, rowBytes, rowBytes);
        }
        finally
        {
            Release(access);
            Close(reference); Release(reference);
            Release(memory);
            Close(buffer); Release(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PlaneDescription { public int StartIndex, Width, Height, Stride; }

    [StructLayout(LayoutKind.Sequential)]
    struct RectF4 { public float X, Y, Width, Height; }

    static void Wait(IntPtr op, TimeSpan timeout)
    {
        IntPtr info = QueryInterface(op, IID_IAsyncInfo);
        try
        {
            var until = DateTime.UtcNow + timeout;
            while (true)
            {
                int status;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, 7))(info, &status)); // get_Status
                if (status == 1) return; // Completed
                if (status == 2) throw new OperationCanceledException();
                if (status == 3)
                {
                    int error;
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, 8))(info, &error));
                    Marshal.ThrowExceptionForHR(error);
                    throw new InvalidOperationException("Text recognition failed.");
                }
                if (DateTime.UtcNow > until)
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(info, 9))(info); // Cancel
                    throw new TimeoutException("Text recognition took too long.");
                }
                Thread.Sleep(8);
            }
        }
        finally { Release(info); }
    }

    static List<List<Word>> ReadLines(IntPtr result)
    {
        var lines = new List<List<Word>>();
        IntPtr lineView = IntPtr.Zero;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(result, 6))(result, &lineView)); // get_Lines
            uint lineCount = Size(lineView);
            for (uint i = 0; i < lineCount; i++)
            {
                IntPtr line = At(lineView, i), wordView = IntPtr.Zero;
                try
                {
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(line, 6))(line, &wordView)); // get_Words
                    var words = new List<Word>();
                    uint wordCount = Size(wordView);
                    for (uint j = 0; j < wordCount; j++)
                    {
                        IntPtr word = At(wordView, j);
                        try
                        {
                            RectF4 r;
                            Check(((delegate* unmanaged[Stdcall]<IntPtr, RectF4*, int>)Slot(word, 6))(word, &r)); // get_BoundingRect
                            IntPtr h;
                            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(word, 7))(word, &h)); // get_Text
                            string text = TakeString(h);
                            if (text.Length > 0) words.Add(new Word(text, r.X, r.Y, r.Width, r.Height));
                        }
                        finally { Release(word); }
                    }
                    if (words.Count > 0) lines.Add(words);
                }
                finally { Release(wordView); Release(line); }
            }
        }
        finally { Release(lineView); }
        return lines;
    }

    // ------------------------------------------------------------------ COM / WinRT plumbing

    static IntPtr Slot(IntPtr obj, int index) => (*(IntPtr**)obj)[index];

    static uint Size(IntPtr view)
    {
        uint n;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(view, 7))(view, &n)); // IVectorView.get_Size
        return n;
    }

    static IntPtr At(IntPtr view, uint index)
    {
        IntPtr item;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(view, 6))(view, index, &item)); // IVectorView.GetAt
        return item;
    }

    static IntPtr Factory(string runtimeClass, Guid iid)
    {
        Check(WindowsCreateString(runtimeClass, (uint)runtimeClass.Length, out IntPtr name));
        try
        {
            Check(RoGetActivationFactory(name, ref iid, out IntPtr factory));
            return factory;
        }
        finally { WindowsDeleteString(name); }
    }

    static IntPtr QueryInterface(IntPtr obj, Guid iid)
    {
        Check(Marshal.QueryInterface(obj, in iid, out IntPtr result));
        return result;
    }

    static void Close(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        if (Marshal.QueryInterface(obj, in IID_IClosable, out IntPtr closable) != 0) return;
        try { ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(closable, 6))(closable); }
        finally { Release(closable); }
    }

    static string TakeString(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        try
        {
            char* p = WindowsGetStringRawBuffer(h, out uint length);
            return p == null ? "" : new string(p, 0, (int)length);
        }
        finally { WindowsDeleteString(h); }
    }

    static void Release(IntPtr p) { if (p != IntPtr.Zero) Marshal.Release(p); }

    static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    [DllImport("combase.dll", PreserveSig = true)]
    static extern int RoInitialize(int initType);

    [DllImport("combase.dll", PreserveSig = true)]
    static extern void RoUninitialize();

    [DllImport("combase.dll", PreserveSig = true)]
    static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int WindowsCreateString(string source, uint length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    static extern char* WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
}
