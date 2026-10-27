using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace InfoPanel.Services;

/// <summary>Mantiene los clientes WebSocket y les envía la telemetría.</summary>
public sealed class TelemetryHub
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private readonly AuthService _auth;

    public TelemetryHub(AuthService auth) => _auth = auth;

    public int ClientCount => _clients.Count;

    /// <summary>El primer mensaje del cliente debe ser su token; después solo recibe datos.</summary>
    public async Task HandleAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[1024];
        var first = await socket.ReceiveAsync(buffer, ct);
        var token = Encoding.UTF8.GetString(buffer, 0, first.Count);
        if (!_auth.IsValid(token))
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized", ct);
            return;
        }

        var id = Guid.NewGuid();
        _clients[id] = socket;
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var r = await socket.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception) when (ct.IsCancellationRequested || socket.State != WebSocketState.Open) { }
        finally
        {
            _clients.TryRemove(id, out _);
        }
    }

    public async Task BroadcastAsync(object payload, JsonSerializerOptions opts)
    {
        if (_clients.IsEmpty) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, opts);
        foreach (var (id, ws) in _clients)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                else
                    _clients.TryRemove(id, out _);
            }
            catch
            {
                _clients.TryRemove(id, out _);
            }
        }
    }
}
