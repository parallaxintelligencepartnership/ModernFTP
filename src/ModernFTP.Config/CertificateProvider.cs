using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ModernFTP.Config;

public static class CertificateProvider
{
    public const string SelfSignedFileName = "modernftp-selfsigned.pfx";

    /// <summary>Loads the configured PFX, or loads or creates the self signed certificate in <paramref name="configDirectory"/>.</summary>
    public static X509Certificate2 LoadOrCreate(TlsConfig tls, string configDirectory)
    {
        ArgumentNullException.ThrowIfNull(tls);
        if (!string.IsNullOrWhiteSpace(tls.CertificatePath))
        {
            var path = Path.GetFullPath(tls.CertificatePath, configDirectory);
            return X509CertificateLoader.LoadPkcs12FromFile(path, tls.CertificatePassword);
        }

        var selfSigned = Path.Combine(configDirectory, SelfSignedFileName);
        if (File.Exists(selfSigned))
        {
            var existing = X509CertificateLoader.LoadPkcs12FromFile(selfSigned, password: null);
            if (existing.HasPrivateKey && existing.NotAfter > DateTime.Now.AddDays(7))
            {
                return existing;
            }

            existing.Dispose();
        }

        Directory.CreateDirectory(configDirectory);
        var pfx = CreateSelfSignedPfx();
        File.WriteAllBytes(selfSigned, pfx);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(selfSigned, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return X509CertificateLoader.LoadPkcs12(pfx, password: null);
    }

    public static byte[] CreateSelfSignedPfx()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=ModernFTP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddDnsName(Environment.MachineName);
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        return certificate.Export(X509ContentType.Pkcs12);
    }
}
