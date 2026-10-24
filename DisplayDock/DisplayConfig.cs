using System.Runtime.InteropServices;

namespace DisplayDock;

/// <summary>
/// Win+P menüsündeki modların aynısı (SetDisplayConfig topolojileri).
/// </summary>
public enum DisplayMode : uint
{
    Unknown = 0,
    Internal = 0x1,   // Sadece 1. ekran  (Win+P: "Yalnızca bilgisayar ekranı")
    Clone = 0x2,      // Çoğalt
    Extend = 0x4,     // Genişlet (iki ekran da açık)
    External = 0x8,   // Sadece 2. ekran  (Win+P: "Yalnızca ikinci ekran")
}

internal static class DisplayConfig
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const uint QDC_DATABASE_CURRENT = 0x4;
    private const uint SDC_APPLY = 0x80;

    private const int PathInfoSize = 72;  // sizeof(DISPLAYCONFIG_PATH_INFO)
    private const int ModeInfoSize = 64;  // sizeof(DISPLAYCONFIG_MODE_INFO)

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint numPathArrayElements, IntPtr pathArray,
        uint numModeInfoArrayElements, IntPtr modeInfoArray, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
    private static extern int QueryDisplayConfigWithTopology(uint flags, ref uint numPathArrayElements, IntPtr pathArray,
        ref uint numModeInfoArrayElements, IntPtr modeInfoArray, out uint currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
    private static extern int QueryDisplayConfigNoTopology(uint flags, ref uint numPathArrayElements, IntPtr pathArray,
        ref uint numModeInfoArrayElements, IntPtr modeInfoArray, IntPtr currentTopologyId);

    /// <summary>Win+P ile aynı şekilde ekran modunu değiştirir.</summary>
    public static bool Apply(DisplayMode mode)
    {
        if (mode == DisplayMode.Unknown) return false;
        return SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | (uint)mode) == 0;
    }

    /// <summary>Şu anki aktif topolojiyi döndürür.</summary>
    public static DisplayMode GetCurrentMode()
    {
        if (GetDisplayConfigBufferSizes(QDC_DATABASE_CURRENT, out uint nPath, out uint nMode) != 0)
            return DisplayMode.Unknown;

        IntPtr paths = Marshal.AllocHGlobal((int)Math.Max(1, nPath) * PathInfoSize);
        IntPtr modes = Marshal.AllocHGlobal((int)Math.Max(1, nMode) * ModeInfoSize);
        try
        {
            int rc = QueryDisplayConfigWithTopology(QDC_DATABASE_CURRENT, ref nPath, paths, ref nMode, modes, out uint topo);
            return rc == 0 ? (DisplayMode)topo : DisplayMode.Unknown;
        }
        finally
        {
            Marshal.FreeHGlobal(paths);
            Marshal.FreeHGlobal(modes);
        }
    }

    /// <summary>Şu an açık (aktif) ekran sayısı.</summary>
    public static int GetActiveDisplayCount()
    {
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint nPath, out uint nMode) != 0)
            return 0;

        IntPtr paths = Marshal.AllocHGlobal((int)Math.Max(1, nPath) * PathInfoSize);
        IntPtr modes = Marshal.AllocHGlobal((int)Math.Max(1, nMode) * ModeInfoSize);
        try
        {
            int rc = QueryDisplayConfigNoTopology(QDC_ONLY_ACTIVE_PATHS, ref nPath, paths, ref nMode, modes, IntPtr.Zero);
            return rc == 0 ? (int)nPath : 0;
        }
        finally
        {
            Marshal.FreeHGlobal(paths);
            Marshal.FreeHGlobal(modes);
        }
    }
}
