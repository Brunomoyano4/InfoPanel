using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace InfoPanel.Services;

public record PeerDto(string Id, string Name, string Ip, int Port, int HttpsPort, DateTime LastSeen);

/// <summary>
/// Descubrimiento de otras PCs con InfoPanel en la red local: cada una anuncia por UDP broadcast
/// su id, nombre y puertos cada 5 s, y guarda los anuncios que recibe.
/// El navegador no puede hacer esto por sí mismo; la web le pide la lista a la PC a la que está conectada.
/// </summary>
public sealed class DiscoveryService : BackgroundService
{
    private const string App = "InfoPanel";
    private static readonly TimeSpan Expiry = TimeSpan.FromSeconds(20);

    private readonly AppConfig _config;
    private readonly ILogger<DiscoveryService> _log;
    private readonly ConcurrentDictionary<string, PeerDto> _peers = new();

    public DiscoveryService(AuthService auth, ILogger<DiscoveryService> log)
    {
        _config = auth.Config;
        _log = log;
    }

    public List<PeerDto> GetPeers() =>
        _peers.Values.Where(p => DateTime.UtcNow - p.LastSeen < Expiry).OrderBy(p => p.Name).ToList();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        UdpClient udp;
        try
        {
            udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, _config.DiscoveryPort));
            udp.EnableBroadcast = true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "No se pudo abrir el puerto de descubrimiento {Port}", _config.DiscoveryPort);
            return;
        }

        using (udp)
        {
            var receive = ReceiveLoop(udp, ct);
            var announce = new { app = App, v = 1, id = _config.InstanceId, name = Environment.MachineName, port = _config.Port, httpsPort = _config.HttpsPort };
            var packet = JsonSerializer.SerializeToUtf8Bytes(announce);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            do
            {
                foreach (var target in BroadcastAddresses())
                {
                    try { await udp.SendAsync(packet, new IPEndPoint(target, _config.DiscoveryPort), ct); }
                    catch (Exception) when (!ct.IsCancellationRequested) { }
                }
            } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));

            await receive;
        }
    }

    private async Task ReceiveLoop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await udp.ReceiveAsync(ct);
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(r.Buffer));
                var root = doc.RootElement;
                if (root.GetProperty("app").GetString() != App) continue;
                var id = root.GetProperty("id").GetString();
                if (string.IsNullOrEmpty(id) || id == _config.InstanceId) continue;

                _peers[id] = new PeerDto(id,
                    root.GetProperty("name").GetString() ?? "PC",
                    r.RemoteEndPoint.Address.MapToIPv4().ToString(),
                    root.GetProperty("port").GetInt32(),
                    root.GetProperty("httpsPort").GetInt32(),
                    DateTime.UtcNow);
            }
            catch (OperationCanceledException) { break; }
            catch { /* paquetes ajenos o mal formados */ }
        }
    }

    // Broadcast dirigido de cada red (más confiable que 255.255.255.255 con varios adaptadores)
    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var list = new List<IPAddress>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var a in n.GetIPProperties().UnicastAddresses)
            {
                if (a.Address.AddressFamily != AddressFamily.InterNetwork || a.IPv4Mask is null) continue;
                var ip = a.Address.GetAddressBytes();
                var mask = a.IPv4Mask.GetAddressBytes();
                list.Add(new IPAddress(ip.Select((b, i) => (byte)(b | ~mask[i])).ToArray()));
            }
        }
        return list.Count > 0 ? list.Distinct() : new[] { IPAddress.Broadcast };
    }
}
