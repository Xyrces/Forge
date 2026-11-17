using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Forge.Orchestrator.Workers;
using Xunit;

namespace Forge.Tests;

public sealed class KubernetesTlsTests
{
    [Fact]
    public void PublicEcCaPem_TrustsItsServerButRejectsWrongNameAndUntrustedCertificate()
    {
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=Kubernetes test CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var serverRequest = new CertificateRequest("CN=kubernetes.default.svc", serverKey, HashAlgorithmName.SHA256);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("kubernetes.default.svc");
        serverRequest.CertificateExtensions.Add(names.Build());
        using var server = serverRequest.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
        using var untrusted = serverRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(TempRoot.Instance.NewDirectory("kube-ca"), "ca.crt");
        File.WriteAllText(path, ca.ExportCertificatePem());
        Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(path));
        using var handler = KubernetesWorkerJobs.CreateClusterTlsHandler(path);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://kubernetes.default.svc/");
        var validate = handler.ServerCertificateCustomValidationCallback!;

        Assert.True(validate(request, server, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(validate(request, server, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(validate(request, untrusted, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(validate(request, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
    }
}
