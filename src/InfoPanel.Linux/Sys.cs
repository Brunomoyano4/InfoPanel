using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace InfoPanel;

/// <summary>Lectura de archivos de /sys y /proc y ejecución de herramientas del sistema, sin excepciones.</summary>
public static class Sys
{
    /// <summary>Contenido del archivo sin espacios al final, o null si no existe o no se puede leer.</summary>
    public static string? Read(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch { return null; }
    }

    public static long? ReadLong(string path) =>
        long.TryParse(Read(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static bool Write(string path, string value)
    {
        try { File.WriteAllText(path, value); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Ruta real (realpath de libc) de un enlace de /sys, por ejemplo el dispositivo PCI de una placa de video.
    /// Los enlaces de /sys son relativos a carpetas que también son enlaces: .NET no los resuelve bien solo.
    /// </summary>
    public static string? RealPath(string path)
    {
        try
        {
            var p = realpath(path, IntPtr.Zero);
            if (p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUTF8(p); }
            finally { free(p); }
        }
        catch { return null; }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

    [DllImport("libc")]
    private static extern void free(IntPtr p);

    public static IEnumerable<string> Dirs(string path, string pattern = "*")
    {
        try { return Directory.GetFileSystemEntries(path, pattern).Order(StringComparer.Ordinal).ToList(); }
        catch { return Enumerable.Empty<string>(); }
    }

    private static readonly Dictionary<string, string?> WhichCache = new();

    /// <summary>Ruta de un comando en el PATH (o en las carpetas de sistema), o null si no está instalado.</summary>
    public static string? Which(string command)
    {
        lock (WhichCache)
        {
            if (WhichCache.TryGetValue(command, out var cached)) return cached;
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':')
                .Concat(new[] { "/usr/local/sbin", "/usr/local/bin", "/usr/sbin", "/usr/bin", "/sbin", "/bin" });
            var found = dirs.Where(d => d.Length > 0).Select(d => Path.Combine(d, command)).FirstOrDefault(File.Exists);
            WhichCache[command] = found;
            return found;
        }
    }

    /// <summary>Ejecuta un comando y devuelve su código de salida y lo que escribió (salida y errores).</summary>
    public static (int Code, string Output) Run(string file, IEnumerable<string> args, int timeoutMs = 5000, IDictionary<string, string>? env = null)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (env is not null)
                foreach (var (k, v) in env) psi.Environment[k] = v;
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return (-1, "");
            }
            return (p.ExitCode, stdout.Result + stderr.Result);
        }
        catch
        {
            return (-1, "");
        }
    }

    public static (int Code, string Output) Run(string file, params string[] args) => Run(file, (IEnumerable<string>)args);

    /// <summary>Arranca un comando sin esperar (para apagar o reiniciar: la respuesta HTTP sale antes).</summary>
    public static bool Start(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static float? ParseFloat(string? s) =>
        float.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static bool IsRoot => Environment.UserName == "root";
}
