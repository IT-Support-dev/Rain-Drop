using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RainDrop.Master;
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

    /// <summary>
    /// จำลองการเปิดโปรแกรม Master สองครั้งติดต่อกัน:
    /// ครั้งแรกสร้าง CA/กุญแจ/ใบรับรองใหม่ลงโฟลเดอร์ชั่วคราว ปิด แล้วเปิดครั้งที่สอง
    /// เพื่อโหลดจากที่เก็บ (load path) ซึ่งเป็นเส้นทางที่เคยล้มด้วย
    /// "issuer certificate does not have an associated private key"
    /// ต้องออก client cert และ agent cert ได้ทั้งสองครั้ง
    /// </summary>
    [Fact]
    public void MasterPki_LoadOrCreate_Twice_CanIssueClientAndAgentCerts()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            DataFolder.Path = tempDir;

            // ---- ครั้งแรก: สร้างใหม่ทั้งหมด ----
            using (var pki1 = MasterPki.LoadOrCreate())
            {
                Assert.NotNull(pki1.CaCertificate);
                Assert.NotNull(pki1.ClientCertificate);
                Assert.True(pki1.ClientCertificate.HasPrivateKey);

                using var clientCert1 = pki1.IssueClientCertificate();
                Assert.True(clientCert1.HasPrivateKey);
                Assert.Contains(RepoCertificates.ClientAuthOid,
                    clientCert1.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                        .SelectMany(e => e.EnhancedKeyUsages.Cast<Oid>()).Select(o => o.Value));

                using var challengeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                string challengePem = challengeKey.ExportSubjectPublicKeyInfoPem();
                byte[] pfx1 = pki1.ExportEnrollment("TEST-MACHINE-1", new[] { "127.0.0.1" },
                    "CorrectHorseBatteryStaple123!", challengePem);
                Assert.NotEmpty(pfx1);
            }

            // ---- ครั้งที่สอง: โหลดจากที่เก็บ (จำลองรีสตาร์ทโปรแกรม) ----
            using (var pki2 = MasterPki.LoadOrCreate())
            {
                Assert.NotNull(pki2.CaCertificate);
                Assert.NotNull(pki2.ClientCertificate);
                Assert.True(pki2.ClientCertificate.HasPrivateKey);

                // เส้นทางนี้เคย crash ที่ Certificates.Sign เนื่องจาก ca โหลดจาก PEM แล้วไม่มี private key
                using var clientCert2 = pki2.IssueClientCertificate();
                Assert.True(clientCert2.HasPrivateKey);
                Assert.Contains(RepoCertificates.ClientAuthOid,
                    clientCert2.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                        .SelectMany(e => e.EnhancedKeyUsages.Cast<Oid>()).Select(o => o.Value));

                using var challengeKey2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                string challengePem2 = challengeKey2.ExportSubjectPublicKeyInfoPem();
                byte[] pfx2 = pki2.ExportEnrollment("TEST-MACHINE-2", new[] { "127.0.0.1" },
                    "CorrectHorseBatteryStaple123!", challengePem2);
                Assert.NotEmpty(pfx2);
            }
        }
        finally
        {
            DataFolder.ResetOverride();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
