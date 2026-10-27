using System.Drawing;
using System.Windows.Forms;
using QRCoder;

namespace InfoPanel.Tray;

/// <summary>Ventana con un QR que abre la página de instalación en el celu, más la dirección y el PIN.</summary>
public sealed class ConnectForm : Form
{
    private readonly PictureBox _qr = new() { SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(50, 16, 280, 280) };

    public ConnectForm(IReadOnlyList<string> urls, string pin)
    {
        // Sin dirección de red local (Wi-Fi/Ethernet desconectado) el celu no tiene cómo llegar
        var url = urls.Count > 0 ? urls[0] + "/setup.html" : null;

        Text = "Conectar un celular · InfoPanel";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(380, 540);
        Font = new Font("Segoe UI", 10);

        var close = new Button { Text = "Cerrar", Bounds = new Rectangle(270, 496, 90, 30) };
        // La ventana no es modal (Show, no ShowDialog): DialogResult no la cerraría
        close.Click += (_, _) => Close();
        CancelButton = close;

        if (url is null)
        {
            var none = new Label
            {
                Text = "La PC no está conectada a una red local (Wi-Fi o cable).\n\nConectala a la misma red que el celu y volvé a abrir esta ventana.",
                Bounds = new Rectangle(20, 20, 340, 120),
            };
            Controls.AddRange(new Control[] { none, close });
            return;
        }

        // QR con la dirección http:// común. Un enlace intent:// (para forzar Chrome en Android) no sirve:
        // lectores como el de la cámara de Xiaomi lo muestran como texto. Si la página se abre en un visor
        // interno, setup.html ofrece "Abrir en Chrome".
        ShowQr(url);

        var steps = new Label
        {
            Text = "1. Escaneá el código con la cámara del celu (misma red Wi-Fi).\n" +
                   "2. Seguí los pasos de la página para instalar el certificado y la app.\n" +
                   $"3. Cuando la app pida el PIN, ingresá:  {pin}",
            Bounds = new Rectangle(20, 310, 340, 96),
        };
        var link = new LinkLabel { Text = url, Bounds = new Rectangle(20, 414, 340, 24) };
        link.LinkClicked += (_, _) => Clipboard.SetText(url);
        var hint = new Label
        {
            Text = "Si no abre, escribí esa dirección en Chrome\n(clic en la dirección para copiarla).",
            ForeColor = Color.Gray,
            Bounds = new Rectangle(20, 440, 340, 44),
        };

        Controls.AddRange(new Control[] { _qr, steps, link, hint, close });
    }

    private void ShowQr(string content)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);
        var old = _qr.Image;
        _qr.Image = Image.FromStream(new MemoryStream(png));
        old?.Dispose();
    }
}
