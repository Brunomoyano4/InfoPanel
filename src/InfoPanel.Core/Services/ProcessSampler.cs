using System.Diagnostics;

namespace InfoPanel.Services;

/// <summary>
/// Procesos con su uso de CPU (% del total del equipo entre dos lecturas) y memoria. Igual en Windows y Linux:
/// .NET lee el tiempo de CPU de cada proceso de la API del sistema (en Linux, de /proc).
/// </summary>
public sealed class ProcessSampler
{
    private readonly Dictionary<int, TimeSpan> _lastCpu = new();
    private DateTime _lastSample = DateTime.MinValue;

    public List<ProcessDto> Sample()
    {
        var now = DateTime.UtcNow;
        var elapsed = _lastSample == DateTime.MinValue ? 0 : (now - _lastSample).TotalMilliseconds;
        _lastSample = now;
        var cores = Environment.ProcessorCount;
        var seen = new HashSet<int>();
        var list = new List<ProcessDto>();

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == 0) continue;
                    var cpu = p.TotalProcessorTime;
                    seen.Add(p.Id);
                    double pct = 0;
                    if (elapsed > 0 && _lastCpu.TryGetValue(p.Id, out var prev))
                        pct = (cpu - prev).TotalMilliseconds / (elapsed * cores) * 100;
                    _lastCpu[p.Id] = cpu;
                    list.Add(new ProcessDto(p.Id, p.ProcessName, Math.Round(pct, 1), p.WorkingSet64 / 1024 / 1024));
                }
                catch
                {
                    // Procesos protegidos o que terminaron durante la lectura
                }
            }
        }

        foreach (var pid in _lastCpu.Keys.Where(k => !seen.Contains(k)).ToList())
            _lastCpu.Remove(pid);

        // Se envían todos para que el panel pueda ordenar por cualquier columna
        return list.OrderByDescending(p => p.Cpu).ThenByDescending(p => p.MemoryMb).ToList();
    }
}
