using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DownKyi.TestInfrastructure;

public sealed class TestCertificateAuthority : IDisposable
{
    private readonly List<X509Certificate2> _issuedCertificates = [];

    public TestCertificateAuthority(string commonName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonName);
        RootCertificate = CreateSelfSignedCertificate(
            commonName,
            isCertificateAuthority: true,
            dnsSubjectAlternativeName: null,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(2));
    }

    public X509Certificate2 RootCertificate { get; }

    public X509Certificate2 IssueServerCertificate(
        string commonName = "localhost",
        string? dnsSubjectAlternativeName = "localhost",
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        X509Certificate2? issuer = null)
    {
        var certificate = CreateSignedCertificate(
            commonName,
            issuer ?? RootCertificate,
            isCertificateAuthority: false,
            dnsSubjectAlternativeName,
            notBefore ?? DateTimeOffset.UtcNow.AddHours(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddHours(12));
        _issuedCertificates.Add(certificate);
        return certificate;
    }

    public X509Certificate2 IssueIntermediateCertificate(string commonName)
    {
        var certificate = CreateSignedCertificate(
            commonName,
            RootCertificate,
            isCertificateAuthority: true,
            dnsSubjectAlternativeName: null,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        _issuedCertificates.Add(certificate);
        return certificate;
    }

    public static X509Certificate2 CreateSelfSignedServerCertificate(
        string commonName = "localhost",
        string? dnsSubjectAlternativeName = "localhost")
    {
        return CreateSelfSignedCertificate(
            commonName,
            isCertificateAuthority: false,
            dnsSubjectAlternativeName,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }

    public string WriteRootCertificatePem(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllText(path, RootCertificate.ExportCertificatePem());
        return path;
    }

    private static X509Certificate2 CreateSelfSignedCertificate(
        string commonName,
        bool isCertificateAuthority,
        string? dnsSubjectAlternativeName,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = CreateRequest(
            commonName,
            rsa,
            isCertificateAuthority,
            dnsSubjectAlternativeName);
        using var generated = request.CreateSelfSigned(notBefore, notAfter);
        return LoadExportedCertificate(generated);
    }

    private static X509Certificate2 CreateSignedCertificate(
        string commonName,
        X509Certificate2 issuer,
        bool isCertificateAuthority,
        string? dnsSubjectAlternativeName,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = CreateRequest(
            commonName,
            rsa,
            isCertificateAuthority,
            dnsSubjectAlternativeName);
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7f;
        using var generated = request.Create(issuer, notBefore, notAfter, serial);
        using var certificateWithKey = generated.CopyWithPrivateKey(rsa);
        return LoadExportedCertificate(certificateWithKey);
    }

    private static CertificateRequest CreateRequest(
        string commonName,
        RSA rsa,
        bool isCertificateAuthority,
        string? dnsSubjectAlternativeName)
    {
        var request = new CertificateRequest(
            $"CN={commonName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            isCertificateAuthority,
            isCertificateAuthority,
            0,
            critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(
            request.PublicKey,
            critical: false));

        if (isCertificateAuthority)
        {
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                critical: true));
        }
        else
        {
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection
                {
                    new("1.3.6.1.5.5.7.3.1")
                },
                critical: true));
            if (dnsSubjectAlternativeName != null)
            {
                var subjectAlternativeName = new SubjectAlternativeNameBuilder();
                subjectAlternativeName.AddDnsName(dnsSubjectAlternativeName);
                request.CertificateExtensions.Add(subjectAlternativeName.Build());
            }
        }

        return request;
    }

    private static X509Certificate2 LoadExportedCertificate(X509Certificate2 certificate)
    {
        var keyStorageFlags = X509KeyStorageFlags.Exportable;
        if (OperatingSystem.IsWindows())
        {
            keyStorageFlags |= X509KeyStorageFlags.UserKeySet;
        }
        else if (!OperatingSystem.IsMacOS())
        {
            keyStorageFlags |= X509KeyStorageFlags.EphemeralKeySet;
        }

        return X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pkcs12),
            password: null,
            keyStorageFlags);
    }

    public void Dispose()
    {
        foreach (var certificate in _issuedCertificates)
        {
            certificate.Dispose();
        }

        RootCertificate.Dispose();
    }
}
