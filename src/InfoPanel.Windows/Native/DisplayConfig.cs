using System.Runtime.InteropServices;

namespace InfoPanel.Native;

// API CCD de Windows (QueryDisplayConfig / SetDisplayConfig).
// Se usa DllImport porque las estructuras con cadenas fijas no son compatibles con LibraryImport.

[StructLayout(LayoutKind.Sequential)]
public struct LUID
{
    public uint Low;
    public int High;
}

[StructLayout(LayoutKind.Sequential)]
public struct Rational
{
    public uint Numerator;
    public uint Denominator;
}

[StructLayout(LayoutKind.Sequential)]
public struct PathSourceInfo
{
    public LUID AdapterId;
    public uint Id;
    public uint ModeInfoIdx;
    public uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential)]
public struct PathTargetInfo
{
    public LUID AdapterId;
    public uint Id;
    public uint ModeInfoIdx;
    public uint OutputTechnology;
    public uint Rotation;
    public uint Scaling;
    public Rational RefreshRate;
    public uint ScanLineOrdering;
    public int TargetAvailable;
    public uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential)]
public struct PathInfo
{
    public PathSourceInfo Source;
    public PathTargetInfo Target;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
public struct SourceMode
{
    public uint Width;
    public uint Height;
    public uint PixelFormat;
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct ModeInfo
{
    public const uint TypeSource = 1;
    public const uint TypeTarget = 2;

    [FieldOffset(0)] public uint InfoType;
    [FieldOffset(4)] public uint Id;
    [FieldOffset(8)] public LUID AdapterId;
    [FieldOffset(16)] public SourceMode Source;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct TargetDeviceName
{
    public uint Type;
    public uint Size;
    public LUID AdapterId;
    public uint Id;
    public uint Flags;
    public uint OutputTechnology;
    public ushort EdidManufactureId;
    public ushort EdidProductCodeId;
    public uint ConnectorInstance;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct SourceDeviceName
{
    public uint Type;
    public uint Size;
    public LUID AdapterId;
    public uint Id;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiDeviceName;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct DEVMODE
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    public short SpecVersion;
    public short DriverVersion;
    public short Size;
    public short DriverExtra;
    public int Fields;
    public int PositionX;
    public int PositionY;
    public int DisplayOrientation;
    public int DisplayFixedOutput;
    public short Color;
    public short Duplex;
    public short YResolution;
    public short TTOption;
    public short Collate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
    public short LogPixels;
    public int BitsPerPel;
    public int PelsWidth;
    public int PelsHeight;
    public int DisplayFlags;
    public int DisplayFrequency;
    public int ICMMethod;
    public int ICMIntent;
    public int MediaType;
    public int DitherType;
    public int Reserved1;
    public int Reserved2;
    public int PanningWidth;
    public int PanningHeight;
}

internal static class DisplayConfig
{
    public const uint GET_SOURCE_NAME = 1;
    public const int ENUM_CURRENT_SETTINGS = -1;
    public const int DM_PELSWIDTH = 0x80000;
    public const int DM_PELSHEIGHT = 0x100000;
    public const int DM_DISPLAYFREQUENCY = 0x400000;
    public const uint CDS_UPDATEREGISTRY = 0x1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr param);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName request);

    public const uint QDC_ALL_PATHS = 0x1;
    public const uint QDC_ONLY_ACTIVE_PATHS = 0x2;

    public const uint SDC_TOPOLOGY_INTERNAL = 0x1;
    public const uint SDC_TOPOLOGY_CLONE = 0x2;
    public const uint SDC_TOPOLOGY_EXTEND = 0x4;
    public const uint SDC_TOPOLOGY_EXTERNAL = 0x8;
    public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
    public const uint SDC_VALIDATE = 0x40;
    public const uint SDC_APPLY = 0x80;
    public const uint SDC_SAVE_TO_DATABASE = 0x200;
    public const uint SDC_ALLOW_CHANGES = 0x400;

    public const uint GET_TARGET_NAME = 2;
    public const uint OUTPUT_INTERNAL = 0x80000000;
    public const uint INVALID_INDEX = 0xFFFFFFFF;

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] ModeInfo[] modes, IntPtr topologyId);

    [DllImport("user32.dll")]
    public static extern int SetDisplayConfig(uint numPaths, [In] PathInfo[]? paths, uint numModes, [In] ModeInfo[]? modes, uint flags);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName request);
}
