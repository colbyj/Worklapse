using System.Runtime.InteropServices;

namespace Worklapse.Capture.Windows;

// The few parts of Windows' OCR (Windows.Media.Ocr, a WinRT API) that reading a picture needs, called through COM as
// UiAutomationInterop.cs calls UI Automation: reaching WinRT the usual way needs a Windows-only target framework, which
// moves the build to another folder. Interface IDs and method order are from the Windows SDK headers
// (windows.media.ocr.h, windows.graphics.imaging.h, windows.security.cryptography.h, windows.foundation.h).
//
// WinRT interfaces derive from IInspectable, so each starts with its three methods (placeholders here, never called)
// before its own; delegates derive from IUnknown alone. COM vtables are positional: every method up to the last one
// used is declared, in order.

[ComImport, Guid("5bffa85a-3384-3540-9940-699120d428a8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOcrEngineStatics
{
    void GetIids();
    void GetRuntimeClassName();
    void GetTrustLevel();

    uint GetMaxImageDimension();
    void GetAvailableRecognizerLanguages();
    void IsLanguageSupported();
    void TryCreateFromLanguage();

    /// <summary>An engine for the first of the user's languages that can be read, or null when none can.</summary>
    IOcrEngine? TryCreateFromUserProfileLanguages();
}

[ComImport, Guid("5a14bc41-5b76-3140-b680-8825562683ac"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOcrEngine
{
    void GetIids();
    void GetRuntimeClassName();
    void GetTrustLevel();

    IOcrResultOperation RecognizeAsync(ISoftwareBitmap bitmap);
}

/// <summary>IAsyncOperation&lt;OcrResult&gt;: the reading of one picture, under way.</summary>
[ComImport, Guid("c7d7118e-ae36-59c0-ac76-7badee711c8b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOcrResultOperation
{
    void GetIids();
    void GetRuntimeClassName();
    void GetTrustLevel();

    /// <summary>Called once the reading ends; at once if it already has.</summary>
    void SetCompleted(IOcrResultCompletedHandler handler);

    void GetCompleted();

    /// <summary>The result; throws the reading's error if it failed.</summary>
    IOcrResult GetResults();
}

/// <summary>AsyncOperationCompletedHandler&lt;OcrResult&gt;, implemented here (a delegate: IUnknown only).</summary>
[ComImport, Guid("989c1371-444a-5e7e-b197-9eaaf9d2829a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOcrResultCompletedHandler
{
    /// <summary>The operation is passed as a bare pointer: it's already in hand, so no wrapper is made for it.</summary>
    void Invoke(nint operation, int status);
}

[ComImport, Guid("9bd235b2-175b-3d6a-92e2-388c206e2f63"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOcrResult
{
    void GetIids();
    void GetRuntimeClassName();
    void GetTrustLevel();

    void GetLines();
    void GetTextAngle();

    /// <summary>All the text read, as an HSTRING the caller deletes.</summary>
    nint GetText();
}

[ComImport, Guid("df0385db-672f-4a9d-806e-c2442f343e86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISoftwareBitmapStatics
{
    void GetIids();
    void GetRuntimeClassName();
    void GetTrustLevel();

    void Copy();
    void Convert();
    void ConvertWithAlpha();
    ISoftwareBitmap CreateCopyFromBuffer(IBuffer source, int format, int width, int height);
}

[ComImport, Guid("689e0708-7eef-483f-963f-da938818e073"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISoftwareBitmap;

[ComImport, Guid("320b7e22-3cb0-4cdf-8663-1d28910065eb"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICryptographicBufferStatics
{
    void GetIids();
    void GetRuntimeClassName();
    void GetTrustLevel();

    void Compare();
    void GenerateRandom();
    void GenerateRandomNumber();

    /// <summary>A WinRT buffer holding a copy of the bytes.</summary>
    IBuffer CreateFromByteArray(uint length, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] byte[] value);
}

[ComImport, Guid("905a0fe0-bc53-11df-8c49-001e4fc686da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IBuffer;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static partial class WinRT
{
    public const string OcrEngineClass = "Windows.Media.Ocr.OcrEngine";
    public const string SoftwareBitmapClass = "Windows.Graphics.Imaging.SoftwareBitmap";
    public const string CryptographicBufferClass = "Windows.Security.Cryptography.CryptographicBuffer";

    /// <summary>BitmapPixelFormat.Gray8: one byte per pixel.</summary>
    public const int Gray8PixelFormat = 62;

    private const int InitMultithreaded = 1;
    private const int ChangedMode = unchecked((int)0x80010106);   // RPC_E_CHANGED_MODE: the thread is already in an STA

    /// <summary>The activation factory (the statics) of a WinRT class, as the interface asked for.</summary>
    public static T Factory<T>(string className) where T : class
    {
        var classId = CreateString(className);
        try
        {
            var iid = typeof(T).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(classId, iid, out var factory));
            try
            {
                return (T)Marshal.GetObjectForIUnknown(factory);
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        finally
        {
            WindowsDeleteString(classId);
        }
    }

    /// <summary>
    /// Readies the calling thread for WinRT: the multithreaded apartment, unless it's in a single-threaded one already
    /// (the UI thread), which works too.
    /// </summary>
    public static void InitializeThread()
    {
        var result = RoInitialize(InitMultithreaded);
        if (result != ChangedMode)
            Marshal.ThrowExceptionForHR(result);
    }

    /// <summary>An HSTRING's text, deleting the HSTRING.</summary>
    public static unsafe string TakeString(nint hstring)
    {
        try
        {
            var chars = WindowsGetStringRawBuffer(hstring, out var length);
            return new string(chars, 0, (int)length);
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private static nint CreateString(string text)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(text, (uint)text.Length, out var hstring));
        return hstring;
    }

    [LibraryImport("combase.dll")]
    private static partial int RoInitialize(int initType);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string sourceString, uint length, out nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    private static unsafe partial char* WindowsGetStringRawBuffer(nint hstring, out uint length);
}
