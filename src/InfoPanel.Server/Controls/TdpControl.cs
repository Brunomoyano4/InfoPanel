using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace InfoPanel.Controls;

public record TdpDto(string Backend, int Min, int Max, int? Spl, int? Sppt, int? Fppt);

/// <summary>
/// Límites de potencia (TDP) en watts. Usa el ACPI de ASUS si está disponible;
/// si no, ryzenadj.exe ubicado en la carpeta tools/ junto al ejecutable (CPUs AMD Ryzen).
/// </summary>
public sealed class TdpControl
{
    private static readonly string RyzenAdjPath = Path.Combine(AppContext.BaseDirectory, "tools", "ryzenadj.exe");

    private readonly AsusAcpi _acpi;
    private readonly int _min, _max;
    private int? _spl, _sppt, _fppt;

    public TdpControl(AsusAcpi acpi, AsusControl asus, Services.AuthService auth)
    {
        _acpi = acpi;
        (_min, _max) = (auth.Config.TdpMin, auth.Config.TdpMax);
        // Un cambio de modo ASUS vuelve los límites a los de fábrica, que no se pueden leer
        asus.ModeChanged += () => (_spl, _sppt, _fppt) = ((int?)null, (int?)null, (int?)null);
    }

    public string Backend =>
        _acpi.IsAvailable && _acpi.DeviceGet(AsusAcpi.PptSppt) >= 0 ? "asus"
        : File.Exists(RyzenAdjPath) ? "ryzenadj"
        : "none";

    public TdpDto GetState()
    {
        var backend = Backend;
        if (backend == "ryzenadj") ReadRyzenAdj();
        return new TdpDto(backend, _min, _max, _spl, _sppt, _fppt);
    }

    public string? Set(int spl, int sppt, int fppt)
    {
        spl = Math.Clamp(spl, _min, _max);
        sppt = Math.Clamp(Math.Max(sppt, spl), _min, _max);
        fppt = Math.Clamp(Math.Max(fppt, sppt), _min, _max);

        switch (Backend)
        {
            case "asus":
                var ok = _acpi.DeviceSet(AsusAcpi.PptSpl, spl) == 1
                       & _acpi.DeviceSet(AsusAcpi.PptSppt, sppt) == 1
                       & _acpi.DeviceSet(AsusAcpi.PptFppt, fppt) == 1;
                if (!ok) return "El equipo rechazó alguno de los límites";
                break;
            case "ryzenadj":
                var (code, output) = RunRyzenAdj($"--stapm-limit={spl * 1000} --slow-limit={sppt * 1000} --fast-limit={fppt * 1000}");
                if (code != 0) return "ryzenadj falló: " + output.Trim();
                break;
            default:
                return "No hay forma de controlar el TDP en este equipo";
        }

        (_spl, _sppt, _fppt) = (spl, sppt, fppt);
        return null;
    }

    private void ReadRyzenAdj()
    {
        var (code, output) = RunRyzenAdj("-i");
        if (code != 0) return;
        _spl = ParseLimit(output, "STAPM LIMIT") ?? _spl;
        _sppt = ParseLimit(output, "PPT LIMIT SLOW") ?? _sppt;
        _fppt = ParseLimit(output, "PPT LIMIT FAST") ?? _fppt;
    }

    private static int? ParseLimit(string output, string name)
    {
        var m = Regex.Match(output, $@"\|\s*{name}\s*\|\s*([\d.]+)");
        return m.Success ? (int)Math.Round(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) : null;
    }

    private static (int Code, string Output) RunRyzenAdj(string args)
    {
        var psi = new ProcessStartInfo(RyzenAdjPath, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(5000);
        return (p.ExitCode, output);
    }
}
