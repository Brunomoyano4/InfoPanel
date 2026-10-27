using System.Runtime.InteropServices;

namespace InfoPanel.Native;

// Interfaz COM no documentada de Windows para cambiar el dispositivo de audio predeterminado.
// Solo se usa SetDefaultEndpoint; el resto de los métodos se declaran para respetar el orden de la vtable.
[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat(string deviceId, IntPtr format);
    [PreserveSig] int GetDeviceFormat(string deviceId, int isDefault, IntPtr format);
    [PreserveSig] int ResetDeviceFormat(string deviceId);
    [PreserveSig] int SetDeviceFormat(string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
    [PreserveSig] int GetProcessingPeriod(string deviceId, int isDefault, IntPtr defaultPeriod, IntPtr minPeriod);
    [PreserveSig] int SetProcessingPeriod(string deviceId, IntPtr period);
    [PreserveSig] int GetShareMode(string deviceId, IntPtr mode);
    [PreserveSig] int SetShareMode(string deviceId, IntPtr mode);
    [PreserveSig] int GetPropertyValue(string deviceId, int store, IntPtr key, IntPtr value);
    [PreserveSig] int SetPropertyValue(string deviceId, int store, IntPtr key, IntPtr value);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    [PreserveSig] int SetEndpointVisibility(string deviceId, int visible);
}

[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class PolicyConfigClient { }

internal static class AudioPolicy
{
    /// <summary>Hace predeterminado el dispositivo para los tres roles (consola, multimedia, comunicaciones).</summary>
    public static bool SetDefault(string deviceId)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            var ok = true;
            for (int role = 0; role < 3; role++)
                ok &= policy.SetDefaultEndpoint(deviceId, role) == 0;
            return ok;
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }
}
