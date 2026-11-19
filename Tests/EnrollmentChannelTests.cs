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
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] cb1 = EnrollmentChannel.DeriveChannelBindingFromCert(ecdsa);
        byte[] cb2 = EnrollmentChannel.DeriveChannelBindingFromCert(ecdsa);
        Assert.Equal(cb1, cb2);
        Assert.Equal(32, cb1.Length); // SHA-256
    }

    /// <summary>RFC 5869 Test Case 1: HKDF-SHA256 test vector</summary>
    [Fact]
    public void Hkdf_Rfc5869_TestCase1()
    {
        // IKM = 0x0b repeated 22 times
        byte[] ikm = new byte[22];
        Array.Fill(ikm, (byte)0x0b);
        // salt = 0x000102030405060708090a0b0c
        byte[] salt = { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c };
        // info = 0xf0f1f2f3f4f5f6f7f8f9
        byte[] info = { 0xf0, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9 };
        int length = 42;

        byte[] okm = EnrollmentChannel.HkdfSha256(ikm, salt, info, length);

        // Expected from RFC 5869 Test Case 1
        byte[] expected = {
            0x3c, 0xb2, 0x5f, 0x25, 0xfa, 0xac, 0xd5, 0x7a,
            0x90, 0x43, 0x4f, 0x64, 0xd0, 0x36, 0x2f, 0x2a,
            0x2d, 0x2d, 0x0a, 0x90, 0xcf, 0x1a, 0x5a, 0x4c,
            0x5d, 0xb0, 0x2d, 0x56, 0xec, 0xc4, 0xc5, 0xbf,
            0x34, 0x00, 0x72, 0x08, 0xd5, 0xb8, 0x87, 0x18,
            0x58, 0x65
        };
        Assert.Equal(expected, okm);
    }

}