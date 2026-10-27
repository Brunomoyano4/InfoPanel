using System.Text.Json;
using System.Text.Json.Nodes;

namespace InfoPanel.Services;

/// <summary>
/// Dashboards personalizados guardados en dashboards.json junto al ejecutable.
/// El servidor solo valida la estructura básica; el contenido de cada widget lo interpreta la web.
/// </summary>
public sealed class DashboardService
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "dashboards.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private const int MaxDashboards = 30;
    private const int MaxWidgets = 100;

    private readonly object _lock = new();
    private readonly JsonArray _dashboards;

    public DashboardService()
    {
        try
        {
            _dashboards = File.Exists(FilePath) ? JsonNode.Parse(File.ReadAllText(FilePath)) as JsonArray ?? new() : new();
        }
        catch
        {
            _dashboards = new();
        }
    }

    public string GetAllJson()
    {
        lock (_lock) return _dashboards.ToJsonString();
    }

    public string? Save(JsonObject dashboard)
    {
        var id = dashboard["id"]?.GetValue<string>();
        var name = dashboard["name"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return "Falta el id";
        if (string.IsNullOrWhiteSpace(name) || name.Length > 40) return "El nombre debe tener entre 1 y 40 caracteres";
        if (dashboard["widgets"] is not JsonArray widgets || widgets.Count > MaxWidgets) return $"Máximo {MaxWidgets} widgets";

        lock (_lock)
        {
            var existing = _dashboards.OfType<JsonObject>().FirstOrDefault(d => d["id"]?.GetValue<string>() == id);
            if (existing is not null)
                _dashboards[_dashboards.IndexOf(existing)] = dashboard.DeepClone();
            else if (_dashboards.Count >= MaxDashboards)
                return $"Máximo {MaxDashboards} dashboards";
            else
                _dashboards.Add(dashboard.DeepClone());
            Persist();
        }
        return null;
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var existing = _dashboards.OfType<JsonObject>().FirstOrDefault(d => d["id"]?.GetValue<string>() == id);
            if (existing is null) return false;
            _dashboards.Remove(existing);
            Persist();
            return true;
        }
    }

    private void Persist() => File.WriteAllText(FilePath, _dashboards.ToJsonString(JsonOpts));
}
