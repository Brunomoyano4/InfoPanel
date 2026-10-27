using InfoPanel.Native;
using Microsoft.Win32.SafeHandles;

namespace InfoPanel.Controls;

/// <summary>
/// Acceso al driver ACPI de ASUS (\\.\ATKACPI), el mismo que usan Armoury Crate y G-Helper.
/// Permite modos de rendimiento, curvas de ventilador, RPM y límites de potencia (PPT).
/// </summary>
public sealed class AsusAcpi : IDisposable
{
    private const uint ControlCode = 0x0022240C;
    private const uint DSTS = 0x53545344; // leer
    private const uint DEVS = 0x53564544; // escribir

    public const uint PerformanceMode = 0x00120075;
    public const uint CpuFanSpeed = 0x00110013;
    public const uint GpuFanSpeed = 0x00110014;
    public const uint MidFanSpeed = 0x00110031;
    public const uint CpuFanCurve = 0x00110024;
    public const uint GpuFanCurve = 0x00110025;
    public const uint MidFanCurve = 0x00110032;

    public const uint BatteryLimit = 0x00120057;

    // Límites de potencia AMD (watts)
    public const uint PptSpl = 0x001200A3;  // sostenido
    public const uint PptSppt = 0x001200A0; // boost largo
    public const uint PptFppt = 0x001200C1; // boost corto

    private readonly SafeFileHandle? _handle;
    private readonly object _lock = new();

    public bool IsAvailable => _handle is { IsInvalid: false };

    public AsusAcpi()
    {
        var h = Win32.CreateFile(@"\\.\ATKACPI", Win32.GENERIC_READ | Win32.GENERIC_WRITE,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (h.IsInvalid) h.Dispose();
        else _handle = h;
    }

    private byte[] CallMethod(uint methodId, byte[] args)
    {
        var input = new byte[8 + args.Length];
        BitConverter.GetBytes(methodId).CopyTo(input, 0);
        BitConverter.GetBytes((uint)args.Length).CopyTo(input, 4);
        args.CopyTo(input, 8);
        var output = new byte[16];
        if (!IsAvailable) return output;
        lock (_lock)
            Win32.DeviceIoControl(_handle!, ControlCode, input, (uint)input.Length, output, (uint)output.Length, out _, IntPtr.Zero);
        return output;
    }

    /// <summary>Devuelve el valor del dispositivo, o un número negativo si no existe.</summary>
    public int DeviceGet(uint deviceId)
    {
        var args = new byte[8];
        BitConverter.GetBytes(deviceId).CopyTo(args, 0);
        return BitConverter.ToInt32(CallMethod(DSTS, args), 0) - 65536;
    }

    public byte[] DeviceGetBuffer(uint deviceId, uint status = 0)
    {
        var args = new byte[8];
        BitConverter.GetBytes(deviceId).CopyTo(args, 0);
        BitConverter.GetBytes(status).CopyTo(args, 4);
        return CallMethod(DSTS, args);
    }

    /// <summary>Devuelve 1 si la operación fue aceptada.</summary>
    public int DeviceSet(uint deviceId, int value)
    {
        var args = new byte[8];
        BitConverter.GetBytes(deviceId).CopyTo(args, 0);
        BitConverter.GetBytes(value).CopyTo(args, 4);
        return BitConverter.ToInt32(CallMethod(DEVS, args), 0);
    }

    public int DeviceSet(uint deviceId, byte[] data)
    {
        var args = new byte[4 + data.Length];
        BitConverter.GetBytes(deviceId).CopyTo(args, 0);
        data.CopyTo(args, 4);
        return BitConverter.ToInt32(CallMethod(DEVS, args), 0);
    }

    public void Dispose() => _handle?.Dispose();
}
