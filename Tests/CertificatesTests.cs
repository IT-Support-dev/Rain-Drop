using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RainDrop.Shared;
using Xunit;

namespace RainDrop.Tests;

public class CertificatesTests
{
    [Fact]
    public void Ca_IsP256TenYearsAndCritical()
    {
        using var ca = RepoCertificates.CreateCa();
        using var key = ca.GetECDsaPrivateKey()!;
        Assert.Equal(256, key.KeySize);
        Assert.True(ca.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddYears(9));
        var bc = ca.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.True(bc.CertificateAuthority);
        Assert.True(bc.Critical);
    }

    [Fact]
    public void ClientAndAgent_HaveDistinctRolesAndOneYearValidity()
    {
        using var ca = RepoCertificates.CreateCa();
        using var caKey = ca.GetECDsaPrivateKey()!;
        using var client = RepoCertificates.IssueClient(ca, caKey);
        using var agent = RepoCertificates.IssueAgent(ca, caKey, Guid.NewGuid().ToString("N"), "PC-1", new[] { "127.0.0.1" });
        Assert.Contains(RepoCertificates.ClientAuthOid, client.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value));
        Assert.Contains(RepoCertificates.ServerAuthOid, agent.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value));
        Assert.True(agent.NotAfter.ToUniversalTime() < DateTime.UtcNow.AddYears(1).AddDays(1));
        var policy = new ControlTlsPolicy(new X509Certificate2(ca.RawData), new RevocationState(), ControlRole.MasterClient);
        Assert.False(policy.Validate(agent, null, System.Net.Security.SslPolicyErrors.None, out string reason));
        Assert.Equal("wrong-role", reason);
    }

    [Fact]
    public void EnrollmentBundle_RoundTripsWithPasswordAndRejectsWrongPassword()
    {
        using var ca = RepoCertificates.CreateCa();
        using var caKey = ca.GetECDsaPrivateKey()!;
        using var agent = RepoCertificates.IssueAgent(ca, caKey, Guid.NewGuid().ToString("N"), "PC-1", Array.Empty<string>());
        byte[] pfx = RepoCertificates.ExportEnrollment(agent, ca, "correct horse battery");
        var (restored, restoredCa) = RepoCertificates.ReadEnrollment(pfx, "correct horse battery");
        using (restored) using (restoredCa)
        {
            Assert.Equal(agent.RawData, restored.RawData);
            Assert.Equal(ca.RawData, restoredCa.RawData);
            Assert.True(restored.HasPrivateKey);
        }
        Assert.ThrowsAny<CryptographicException>(() => RepoCertificates.ReadEnrollment(pfx, "wrong password!!"));
    }

    [Fact]
    public void EnrollmentBundle_TamperedMacRejected()
    {
        using var ca = RepoCertificates.CreateCa();
        using var caKey = ca.GetECDsaPrivateKey()!;
        using var agent = RepoCertificates.IssueAgent(ca, caKey, Guid.NewGuid().ToString("N"), "PC-1", Array.Empty<string>());
        byte[] pfx = RepoCertificates.ExportEnrollment(agent, ca, "correct horse battery");
        pfx[^1] ^= 0xff;
        Assert.ThrowsAny<CryptographicException>(() => RepoCertificates.ReadEnrollment(pfx, "correct horse battery"));
    }

    [Fact]
    public void RevocationState_RejectsRollbackAndNormalizesSerials()
    {
        var state = new RevocationState();
        var first = state.Revoke("00abCD");
        Assert.True(state.IsRevoked("ABCD"));
        Assert.Throws<InvalidDataException>(() => state.Apply(new RevocationList(first.Version - 1, new List<string>())));
        state.Apply(new RevocationList(first.Version + 1, new List<string>()));
        Assert.False(state.IsRevoked("ABCD"));
    }
}
