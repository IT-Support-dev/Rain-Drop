using System.Security.Cryptography;
using System.Text;
using RainDrop.Shared;
using Xunit;

namespace RainDrop.Tests;

public class EnrollmentChannelTests
{
    [Fact]
    public void Hkdf_DeterministicSameInputsSameOutput()
    {
        byte[] ikm = Encoding.UTF8.GetBytes("ABCD1234EFGH5678");
        byte[] salt = SHA256.HashData(Encoding.UTF8.GetBytes("channel-binding"));
        byte[] k1 = EnrollmentChannel.HkdfSha256(ikm, salt, EnrollmentChannel.Info, 32);
        byte[] k2 = EnrollmentChannel.HkdfSha256(ikm, salt, EnrollmentChannel.Info, 32);
        Assert.Equal(k1, k2);
    }

    [Fact]
    public void Hkdf_DifferentSalt_DifferentKey()
    {
        byte[] ikm = Encoding.UTF8.GetBytes("ABCD1234EFGH5678");
        byte[] k1 = EnrollmentChannel.HkdfSha256(ikm, Encoding.UTF8.GetBytes("salt-a"), EnrollmentChannel.Info, 32);
        byte[] k2 = EnrollmentChannel.HkdfSha256(ikm, Encoding.UTF8.GetBytes("salt-b"), EnrollmentChannel.Info, 32);
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void AgentProof_VerifyPasses()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] nonceA = EnrollmentChannel.NewNonce();
        byte[] nonceM = EnrollmentChannel.NewNonce();
        byte[] transcript = EnrollmentChannel.TranscriptHash(Encoding.UTF8.GetBytes("msg1"), Encoding.UTF8.GetBytes("msg2"));
        byte[] proof = EnrollmentChannel.ComputeAgentProof(key, nonceA, nonceM, transcript);
        Assert.True(EnrollmentChannel.VerifyProof(proof, proof));
    }

    [Fact]
    public void AgentProof_WrongKey_Fails()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] wrongKey = RandomNumberGenerator.GetBytes(32);
        byte[] nonceA = EnrollmentChannel.NewNonce();
        byte[] nonceM = EnrollmentChannel.NewNonce();
        byte[] transcript = EnrollmentChannel.TranscriptHash(Array.Empty<byte>());
        byte[] proof = EnrollmentChannel.ComputeAgentProof(key, nonceA, nonceM, transcript);
        byte[] wrongProof = EnrollmentChannel.ComputeAgentProof(wrongKey, nonceA, nonceM, transcript);
        Assert.False(EnrollmentChannel.VerifyProof(proof, wrongProof));
    }

    [Fact]
    public void AgentVsMaster_DifferentLabels_AntiReflection()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] nonceA = EnrollmentChannel.NewNonce();
        byte[] nonceM = EnrollmentChannel.NewNonce();
        byte[] transcript = EnrollmentChannel.TranscriptHash(Array.Empty<byte>());
        byte[] agentProof = EnrollmentChannel.ComputeAgentProof(key, nonceA, nonceM, transcript);
        byte[] masterProof = EnrollmentChannel.ComputeMasterProof(key, nonceM, nonceA, transcript);
        // Agent proof กับ Master proof ต้องไม่เท่ากัน (label ต่างกันกัน reflection)
        Assert.NotEqual(agentProof, masterProof);
    }

    [Fact]
    public void MasterProof_VerifyPasses()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] nonceA = EnrollmentChannel.NewNonce();
        byte[] nonceM = EnrollmentChannel.NewNonce();
        byte[] transcript = EnrollmentChannel.TranscriptHash(Array.Empty<byte>());
        byte[] proof = EnrollmentChannel.ComputeMasterProof(key, nonceM, nonceA, transcript);
        Assert.True(EnrollmentChannel.VerifyProof(proof, proof));
    }

    [Fact]
    public void NewNonce_Is32Bytes()
    {
        byte[] n1 = EnrollmentChannel.NewNonce();
        byte[] n2 = EnrollmentChannel.NewNonce();
        Assert.Equal(32, n1.Length);
        Assert.NotEqual(n1, n2);
    }

    [Fact]
    public void TranscriptHash_CoversAllMessages()
    {
        byte[] h1 = EnrollmentChannel.TranscriptHash(Encoding.UTF8.GetBytes("a"), Encoding.UTF8.GetBytes("b"));
        byte[] h2 = EnrollmentChannel.TranscriptHash(Encoding.UTF8.GetBytes("b"), Encoding.UTF8.GetBytes("a"));
        Assert.NotEqual(h1, h2); // ลำดับข้อความสำคัญ (anti-downgrade)
    }

    [Fact]
    public void DeriveChannelBindingFromCert_Deterministic()
    {
        using var ecdsa = ECDsa.Create(ECCurve.CreateNamedCurve(ECCurve.NamedCurves.nistP256));
        byte[] cb1 = EnrollmentChannel.DeriveChannelBindingFromCert(ecdsa);
        byte[] cb2 = EnrollmentChannel.DeriveChannelBindingFromCert(ecdsa);
        Assert.Equal(cb1, cb2);
        Assert.Equal(32, cb1.Length); // SHA-256
    }
}
