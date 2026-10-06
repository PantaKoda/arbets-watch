using System.Runtime.InteropServices;

namespace ArbetsWatch.Desktop.Platform;

/// <summary>
/// A plain system message box for errors that stop ArbetsWatch before its window exists, so a failed start is
/// never silent. Elsewhere the message goes to standard error.
/// </summary>
public static partial class FatalMessage
{
    private const uint MbIconError = 0x10;

    public static void Show(string text)
    {
        if (OperatingSystem.IsWindows())
        {
            _ = MessageBox(IntPtr.Zero, text, "ArbetsWatch", MbIconError);
        }
        else
        {
            Console.Error.WriteLine(text);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr owner, string text, string caption, uint type);
}
