using System.Security.Cryptography;
using System.Text.Json;

namespace InfoPanel.Services;

public class AppConfig
{
    public int Port { get; set; } = 5055;
    // HTTPS (necesario para instalar la web como app) y anuncio UDP para que las PCs se encuentren
    public int HttpsPort { get; set; } = 5056;
    public int DiscoveryPort { get; set; } = 5057;
    // Identificador estable de esta PC (para distinguirla en el selector de PCs)
    public string InstanceId { get; set; } = "";
    // Rango permitido de TDP en watts (ajustar según el equipo)
    public int TdpMin { get; set; } = 5;
    public int TdpMax { get; set; } = 30;
    // Límite de carga de batería ASUS (%); null = sin límite aplicado por InfoPanel
    public int? BatteryLimit { get; set; }
    public string Pin { get; set; } = "";
    public List<string> Tokens { get; set; } = new();
}

/// <summary>
/// Emparejamiento por PIN: el dispositivo envía el PIN una vez y recibe un token que guarda.
/// La configuración se persiste en config.json junto al ejecutable.
/// </summary>
public sealed class AuthService
{
    private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _lock = new();

    public AppConfig Config { get; }

    public AuthService()
    {
        Config = File.Exists(ConfigPath)
            ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig()
            : new AppConfig();

        var changed = false;
        if (string.IsNullOrWhiteSpace(Config.Pin))
        {
            Config.Pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(Config.InstanceId))
        {
            Config.InstanceId = Guid.NewGuid().ToString("N");
            changed = true;
        }
        if (changed) Save();
    }

    public string? Pair(string pin)
    {
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(pin ?? ""),
                System.Text.Encoding.UTF8.GetBytes(Config.Pin)))
            return null;

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        lock (_lock)
        {
            Config.Tokens.Add(token);
            Save();
        }
        return token;
    }

    public bool IsValid(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        lock (_lock) return Config.Tokens.Contains(token);
    }

    /// <summary>Revoca todos los dispositivos emparejados.</summary>
    public void RevokeAll()
    {
        lock (_lock)
        {
            Config.Tokens.Clear();
            Save();
        }
    }

    public void SaveConfig()
    {
        lock (_lock) Save();
    }

    private void Save() => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config, JsonOpts));
}
