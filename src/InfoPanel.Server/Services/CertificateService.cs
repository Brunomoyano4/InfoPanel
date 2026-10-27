using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace InfoPanel.Services;

/// <summary>
/// Certificados para HTTPS en la red local, sin servicios externos:
/// - Una autoridad (CA) propia de esta PC, válida 10 años. Se instala una vez en el celu.
/// - Un certificado de servidor firmado por esa CA con las IPs y nombres actuales de la PC.
///   Si cambia la IP, se emite uno nuevo al arrancar (la CA no cambia, así que el celu no nota nada).
/// Se guardan en la carpeta certs/ junto al ejecutable.
/// </summary>
public sealed class CertificateService
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "certs");
    private static readonly string CaPath = Path.Combine(Dir, "ca.pfx");
    private static readonly string ServerPath = Path.Combine(Dir, "server.pfx");

    public X509Certificate2 Ca { get; }
    public X509Certificate2 Server { get; }

    public CertificateService()
    {
        Directory.CreateDirectory(Dir);
        Ca = LoadOrCreateCa();
        Server = LoadOrCreateServer();
    }

    /// <summary>Certificado de la CA (solo la parte pública) para instalar en el celu.</summary>
    public byte[] CaDer => Ca.Export(X509ContentType.Cert);

    private X509Certificate2 LoadOrCreateCa()
    {
        if (File.Exists(CaPath))
        {
            var existing = Load(CaPath);
            if (existing.NotAfter > DateTime.Now.AddDays(30)) return existing;
        }

        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN=InfoPanel CA ({Environment.MachineName}), O=InfoPanel", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        using var ca = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(10));
        File.WriteAllBytes(CaPath, ca.Export(X509ContentType.Pfx));
        // Al cambiar la CA, el certificado de servidor anterior deja de servir
        if (File.Exists(ServerPath)) File.Delete(ServerPath);
        return Load(CaPath);
    }

    private X509Certificate2 LoadOrCreateServer()
    {
        var ips = LocalIps();
        var names = new[] { "localhost", Environment.MachineName, Environment.MachineName + ".local" };

        if (File.Exists(ServerPath))
        {
            var existing = Load(ServerPath);
            var san = existing.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
            var covered = san is not null && ips.All(ip => san.EnumerateIPAddresses().Contains(ip));
            if (covered && existing.Issuer == Ca.Subject && existing.NotAfter > DateTime.Now.AddDays(30)) return existing;
        }

        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={Environment.MachineName}, O=InfoPanel", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sanBuilder = new SubjectAlternativeNameBuilder();
        foreach (var n in names) sanBuilder.AddDnsName(n);
        foreach (var ip in ips.Append(IPAddress.Loopback).Append(IPAddress.IPv6Loopback)) sanBuilder.AddIpAddress(ip);
        req.CertificateExtensions.Add(sanBuilder.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Ca, true, false));

        // 800 días: iOS rechaza certificados de servidor de más de 825 días
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var signed = req.Create(Ca, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(800), serial);
        using var withKey = signed.CopyWithPrivateKey(key);
        File.WriteAllBytes(ServerPath, withKey.Export(X509ContentType.Pfx));
        return Load(ServerPath);
    }

    private static X509Certificate2 Load(string path) =>
        // Sin EphemeralKeySet: SslStream en Windows no acepta claves efímeras
        X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.Exportable);

    /// <summary>
    /// IPv4 de esta PC alcanzables desde la red local, la principal primero.
    /// Se descartan las 169.254.x.x (dirección automática "sin red" de adaptadores virtuales como Wi-Fi Direct
    /// o Bluetooth) y los adaptadores virtuales conocidos; primero va el adaptador con puerta de enlace,
    /// que es el que está conectado al router.
    /// </summary>
    public static List<IPAddress> LocalIps() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && !IsVirtual(n))
        .Select(n => (Props: n.GetIPProperties(), Nic: n))
        .SelectMany(x => x.Props.UnicastAddresses.Select(a => (a.Address, HasGateway: x.Props.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)))))
        .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x.Address) && !IsLinkLocal(x.Address))
        .OrderByDescending(x => x.HasGateway)
        .Select(x => x.Address)
        .Distinct()
        .ToList();

    private static bool IsLinkLocal(IPAddress ip) => ip.GetAddressBytes() is [169, 254, ..];

    private static bool IsVirtual(NetworkInterface n) =>
        new[] { "Hyper-V", "vEthernet", "VirtualBox", "VMware", "WSL", "Wi-Fi Direct", "Bluetooth" }
            .Any(v => n.Description.Contains(v, StringComparison.OrdinalIgnoreCase) || n.Name.Contains(v, StringComparison.OrdinalIgnoreCase));
}
