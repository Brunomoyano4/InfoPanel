using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace InfoPanel.Services;

/// <summary>
/// Inicio automático con Windows mediante una tarea programada con privilegios elevados.
/// La carpeta "Inicio" o la clave Run no sirven: Windows no lanza ahí apps que requieren administrador.
/// </summary>
public sealed class AutoStartService
{
    private const string TaskName = "InfoPanel";
    public const string Arg = "--autostart";

    public bool IsEnabled { get; private set; }

    public AutoStartService() => IsEnabled = Schtasks($"/Query /TN \"{TaskName}\"") == 0;

    public string? SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            if (IsEnabled && Schtasks($"/Delete /TN \"{TaskName}\" /F") != 0) return "No se pudo quitar la tarea";
            IsEnabled = false;
            return null;
        }

        var exe = Environment.ProcessPath!;
        var user = WindowsIdentity.GetCurrent().Name;
        // Sin las opciones de batería, la tarea no arranca en el Ally desenchufado; sin límite, se cortaría a las 72 h
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>InfoPanel: telemetría y control remoto de la PC</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId><Delay>PT10S</Delay></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>{Arg}</Arguments>
                  <WorkingDirectory>{SecurityElement.Escape(AppContext.BaseDirectory)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        var file = Path.Combine(Path.GetTempPath(), "infopanel-task.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        try
        {
            if (Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F") != 0) return "No se pudo crear la tarea (¿InfoPanel corre como administrador?)";
        }
        finally
        {
            File.Delete(file);
        }
        IsEnabled = true;
        return null;
    }

    private static int Schtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(10000);
        return p.ExitCode;
    }
}
