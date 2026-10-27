using System.Diagnostics;
using InfoPanel.Platform;
using InfoPanel.Services;

namespace InfoPanel;

/// <summary>Lo que ofrece Windows y cómo se reinicia InfoPanel al actualizar.</summary>
public sealed class WindowsPlatform : IPlatformInfo, IUpdatePlatform
{
    public PlatformCapsDto Caps { get; } = new("windows", PowerPlans: true, Displays: true, Media: true,
        new() { "lock", "sleep", "hibernate", "restart", "shutdown" }, AutoStartLabel: "Windows");

    public string AssetName => "InfoPanel.exe";

    // El .exe nuevo espera a que este proceso suelte la instancia; la bandeja cierra este al recibir RestartRequested
    public void Relaunch(string exePath) =>
        Process.Start(new ProcessStartInfo(exePath, $"{UpdateService.AfterUpdateArg} {AutoStartService.Arg}")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        })?.Dispose();
}
