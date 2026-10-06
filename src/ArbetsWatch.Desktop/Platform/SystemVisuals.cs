using System.Runtime.InteropServices;

namespace ArbetsWatch.Desktop.Platform;

/// <summary>OS display preferences that affect visuals (from Repo Watch). Conservative defaults elsewhere.</summary>
public static partial class SystemVisuals
{
    private const uint SpiGetHighContrast = 0x0042;
    private const uint SpiGetClientAreaAnimation = 0x1042;
    private const int SmRemoteSession = 0x1000;
    private const uint HcfHighContrastOn = 0x1;

    /// <summary>False when Windows "Show animations" is off.</summary>
    public static bool AnimationsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return true;
            }

            var enabled = 1;
            return !SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref enabled, 0) || enabled != 0;
        }
    }

    /// <summary>True when a Windows high-contrast theme is active (transparency is then disabled).</summary>
    public static bool HighContrast
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            var info = new HighContrastInfo { Size = (uint)Marshal.SizeOf<HighContrastInfo>() };
            return SystemParametersInfo(SpiGetHighContrast, info.Size, ref info, 0) && (info.Flags & HcfHighContrastOn) != 0;
        }
    }

    /// <summary>True in a Remote Desktop session, where blur effects are expensive or unavailable.</summary>
    public static bool RemoteSession => OperatingSystem.IsWindows() && GetSystemMetrics(SmRemoteSession) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastInfo
    {
        public uint Size;
        public uint Flags;
        public IntPtr DefaultScheme;
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, ref HighContrastInfo value, uint winIni);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
}
