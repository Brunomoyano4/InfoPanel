using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using InfoPanel.Services;

namespace InfoPanel.Tray;

/// <summary>Ícono en la bandeja del sistema: muestra dirección y PIN, abre el panel e inicia con Windows.</summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly AutoStartService _autoStart;
    private readonly Func<List<string>> _getUrls;
    private readonly string _pin;
    private readonly ContextMenuStrip _menu;
    private readonly List<ToolStripItem> _urlItems = new();
    private readonly UpdateService _update;
    private readonly ToolStripMenuItem _updateItem;

    /// <summary>
    /// getUrls se consulta cada vez que hace falta (menú, QR): la IP puede cambiar o el Wi-Fi
    /// puede no estar conectado todavía cuando InfoPanel arranca con Windows.
    /// </summary>
    public TrayApp(Func<List<string>> getUrls, string pin, AutoStartService autoStart, UpdateService update, bool silent, bool justUpdated)
    {
        _autoStart = autoStart;
        _update = update;
        _getUrls = getUrls;
        _pin = pin;

        _menu = new ContextMenuStrip();
        _menu.Items.Add(new ToolStripMenuItem($"PIN: {pin}") { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Conectar un celular (QR)…", null, (_, _) => ShowConnect());
        _menu.Items.Add("Abrir panel", null, (_, _) => Open(LocalUrl));
        _menu.Items.Add("Copiar dirección", null, (_, _) => Clipboard.SetText(Url));
        _menu.Items.Add(new ToolStripSeparator());
        _autoStartItem = new ToolStripMenuItem("Iniciar con Windows", null, (_, _) => ToggleAutoStart()) { Checked = autoStart.IsEnabled };
        _menu.Items.Add(_autoStartItem);
        // Solo aparece cuando hay una versión nueva
        _updateItem = new ToolStripMenuItem("", null, (_, _) => InstallUpdate()) { Visible = false };
        _menu.Items.Add(_updateItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add($"Versión {UpdateService.CurrentText}", null, (_, _) => _ = _update.CheckAsync()).ToolTipText = "Buscar actualizaciones";
        _menu.Items.Add("Salir", null, (_, _) => ExitThread());
        _menu.Opening += (_, _) =>
        {
            _autoStartItem.Checked = _autoStart.IsEnabled;
            RefreshUrlItems();
            var st = _update.State;
            _updateItem.Visible = st.Available;
            _updateItem.Enabled = st.Status is not ("downloading" or "installing");
            _updateItem.Text = st.Status switch
            {
                "downloading" => $"Descargando {st.Latest}… {st.Progress} %",
                "installing" => $"Instalando {st.Latest}…",
                _ => $"Actualizar a la versión {st.Latest}",
            };
        };
        RefreshUrlItems();

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = Truncate($"InfoPanel · {Url.Replace("http://", "")}", 63),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => Open(LocalUrl);

        // Los eventos del actualizador llegan desde otro hilo: se pasan al de la interfaz
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _update.UpdateFound += v => ui.Post(_ => _icon.ShowBalloonTip(6000, "Actualización disponible",
            $"InfoPanel {v} está disponible. Instalala desde el menú del ícono o desde la app.", ToolTipIcon.Info), null);
        _update.RestartRequested += () => ui.Post(_ => ExitThread(), null);

        if (justUpdated)
            _icon.ShowBalloonTip(5000, "InfoPanel actualizado", $"Versión {UpdateService.CurrentText}", ToolTipIcon.Info);
        else if (!silent)
            _icon.ShowBalloonTip(5000, "InfoPanel en ejecución", $"{Url}\nPIN: {pin}", ToolTipIcon.Info);
    }

    private void InstallUpdate()
    {
        var st = _update.State;
        if (MessageBox.Show($"¿Actualizar InfoPanel a la versión {st.Latest}?\n\nSe descarga, se verifica y se reinicia solo. La configuración se mantiene.",
                "InfoPanel", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        if (_update.StartInstall() is { } error)
            _icon.ShowBalloonTip(5000, "InfoPanel", error, ToolTipIcon.Warning);
        else
            _icon.ShowBalloonTip(4000, "InfoPanel", $"Descargando la versión {st.Latest}…", ToolTipIcon.Info);
    }

    // Dirección en la red local (para el celu) y dirección para abrir el panel en esta misma PC
    private string Url => _getUrls().FirstOrDefault() ?? "Sin conexión a una red local";
    private string LocalUrl => _getUrls().FirstOrDefault() ?? "http://localhost:5055";

    private void RefreshUrlItems()
    {
        foreach (var item in _urlItems) _menu.Items.Remove(item);
        _urlItems.Clear();
        var urls = _getUrls();
        if (urls.Count == 0) urls.Add("Sin conexión a una red local");
        for (int i = 0; i < urls.Count; i++)
        {
            var item = new ToolStripMenuItem(urls[i]) { Enabled = false };
            _urlItems.Add(item);
            _menu.Items.Insert(i, item);
        }
        if (_icon is not null) _icon.Text = Truncate($"InfoPanel · {Url.Replace("http://", "")}", 63);
    }

    private void ToggleAutoStart()
    {
        var error = _autoStart.SetEnabled(!_autoStart.IsEnabled);
        _autoStartItem.Checked = _autoStart.IsEnabled;
        if (error is not null)
            _icon.ShowBalloonTip(4000, "InfoPanel", error, ToolTipIcon.Warning);
    }

    private ConnectForm? _connect;

    private void ShowConnect()
    {
        // Se vuelve a crear cada vez para que el QR use la dirección actual
        if (_connect is { IsDisposed: false }) _connect.Close();
        _connect = new ConnectForm(_getUrls(), _pin);
        _connect.Show();
        _connect.Activate();
    }

    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    // Dibuja el ícono (línea de pulso verde sobre fondo oscuro) para no depender de un .ico
    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Color.FromArgb(14, 17, 22));
            g.FillRectangle(bg, 1, 1, 30, 30);
            using var pen = new Pen(Color.FromArgb(61, 220, 151), 3) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, new[] { new Point(4, 20), new Point(10, 20), new Point(13, 10), new Point(18, 25), new Point(21, 16), new Point(28, 16) });
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
