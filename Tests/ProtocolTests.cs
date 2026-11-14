using System.Buffers.Binary;
using System.Security.Cryptography;
using RainDrop.Shared;
using Xunit;

namespace RainDrop.Tests;

/// <summary>
/// เทสต์โปรโตคอลควบคุม (Protocol.cs): การเขียน/อ่านแพ็กเก็ต, JSON round-trip และการเซ็น/ตรวจ ECDSA
/// ทั้งหมดไม่ผูก Windows จึงรันได้บน CI ทุกแพลตฟอร์ม
/// </summary>
public class ProtocolTests
{
    private static async Task<(MsgType Type, byte[] Payload)> RoundTripAsync(MsgType type, byte[] payload)
    {
        using var ms = new MemoryStream();
        await Protocol.WriteAsync(ms, type, payload);
        ms.Position = 0;
        return await Protocol.ReadAsync(ms);
    }

    // ---------- Write/Read round-trip ----------

    [Theory]
    [InlineData(MsgType.AuthChallenge)]
    [InlineData(MsgType.ChatMessage)]
    [InlineData(MsgType.ScreenFrame)]
    [InlineData(MsgType.ActivitySnapshot)]
    [InlineData(MsgType.SetBlocklist)]
    public async Task WriteRead_RoundTrip_PreservesTypeAndPayload(MsgType type)
    {
        var payload = new byte[] { 0, 1, 2, 250, 255, 128, 64 };
        var (readType, readPayload) = await RoundTripAsync(type, payload);
        Assert.Equal(type, readType);
        Assert.Equal(payload, readPayload);
    }

    [Fact]
    public async Task WriteRead_EmptyPayload_RoundTrips()
    {
        var (type, payload) = await RoundTripAsync(MsgType.ActivityRequest, Array.Empty<byte>());
        Assert.Equal(MsgType.ActivityRequest, type);
        Assert.Empty(payload);
    }

    [Fact]
    public async Task WriteRead_LargePayload_RoundTrips()
    {
        var payload = new byte[1_000_000];
        Random.Shared.NextBytes(payload);
        var (type, readPayload) = await RoundTripAsync(MsgType.ScreenFrame, payload);
        Assert.Equal(MsgType.ScreenFrame, type);
        Assert.Equal(payload, readPayload);
    }

    [Fact]
    public async Task WriteRead_MultipleMessages_PreserveOrder()
    {
        using var ms = new MemoryStream();
        var items = new (MsgType Type, byte[] Payload)[]
        {
            (MsgType.AuthChallenge, new byte[] { 1, 2, 3 }),
            (MsgType.ChatMessage, new byte[] { 4, 5 }),
            (MsgType.Alert, new byte[] { 6 }),
        };
        foreach (var (t, p) in items) await Protocol.WriteAsync(ms, t, p);
        ms.Position = 0;
        foreach (var (t, p) in items)
        {
            var (rt, rp) = await Protocol.ReadAsync(ms);
            Assert.Equal(t, rt);
            Assert.Equal(p, rp);
        }
    }

    [Fact]
    public async Task ReadAsync_NegativeLength_Throws()
    {
        using var ms = new MemoryStream();
        var header = new byte[5];
        header[0] = (byte)MsgType.ChatMessage;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1, 4), -1);
        ms.Write(header, 0, header.Length);
        ms.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => Protocol.ReadAsync(ms));
    }

    [Fact]
    public async Task ReadAsync_OversizeLength_Throws()
    {
        using var ms = new MemoryStream();
        var header = new byte[5];
        header[0] = (byte)MsgType.ScreenFrame;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1, 4), int.MaxValue);
        ms.Write(header, 0, header.Length);
        ms.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => Protocol.ReadAsync(ms));
    }

    // ---------- JSON round-trip ของ record ----------

    [Fact]
    public void Json_ChatMsg_RoundTrips()
    {
        var msg = new ChatMsg("master", "สวัสดี", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var back = Protocol.FromJson<ChatMsg>(Protocol.ToJson(msg));
        Assert.Equal(msg, back);
    }

    [Fact]
    public void Json_MouseCmd_RoundTrips()
    {
        var cmd = new MouseCmd(0.5, 0.25, MouseAction.Wheel, 2, -120);
        var back = Protocol.FromJson<MouseCmd>(Protocol.ToJson(cmd));
        Assert.Equal(cmd, back);
    }

    [Fact]
    public void Json_BlockRules_RoundTrips_ElementWise()
    {
        // record ที่มี List ใช้เทียบแบบ reference จึงเทียบสมาชิกทีละรายการแทน
        var rules = new BlockRules(
            new List<string> { "chrome", "game.exe" },
            new List<string> { "facebook.com", "youtube.com" });
        var back = Protocol.FromJson<BlockRules>(Protocol.ToJson(rules));
        Assert.Equal(rules.Programs, back.Programs);
        Assert.Equal(rules.Urls, back.Urls);
    }

    // ---------- Sign / Verify (ECDSA) ----------

    [Fact]
    public void SignVerify_CorrectKeyAndNonce_ReturnsTrue()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var nonce = RandomNumberGenerator.GetBytes(32);
        var signature = Protocol.Sign(key, nonce);
        Assert.True(Protocol.Verify(key, nonce, signature));
    }

    [Fact]
    public void SignVerify_WrongNonce_ReturnsFalse()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var nonce = RandomNumberGenerator.GetBytes(32);
        var otherNonce = RandomNumberGenerator.GetBytes(32);
        var signature = Protocol.Sign(key, nonce);
        Assert.False(Protocol.Verify(key, otherNonce, signature));
    }

    [Fact]
    public void SignVerify_WrongKey_ReturnsFalse()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var nonce = RandomNumberGenerator.GetBytes(32);
        var signature = Protocol.Sign(signer, nonce);
        Assert.False(Protocol.Verify(verifier, nonce, signature));
    }

    [Fact]
    public void SignVerify_SignatureForDifferentNonce_ReturnsFalse()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var nonce = RandomNumberGenerator.GetBytes(32);
        // ลายเซ็นที่ถูกรูปแบบ (DER) แต่เซ็นจาก nonce อื่น ต้องตรวจไม่ผ่าน (ไม่โยน exception)
        var signature = Protocol.Sign(key, RandomNumberGenerator.GetBytes(32));
        Assert.False(Protocol.Verify(key, nonce, signature));
    }

    [Fact]
    public void Verify_PublicOnlyKey_AcceptsSignatureFromPrivateKey()
    {
        // จำลองสถานการณ์จริง: Master เซ็นด้วย private key, Agent ตรวจด้วย public key อย่างเดียว
        using var full = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var publicOnly = ECDsa.Create();
        publicOnly.ImportSubjectPublicKeyInfo(full.ExportSubjectPublicKeyInfo(), out _);

        var nonce = RandomNumberGenerator.GetBytes(32);
        var signature = Protocol.Sign(full, nonce);
        Assert.True(Protocol.Verify(publicOnly, nonce, signature));
    }

    // ---------- Fingerprint ----------

    [Fact]
    public void Fingerprint_SameKeyMaterial_ProducesSameValue()
    {
        using var full = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var publicOnly = ECDsa.Create();
        publicOnly.ImportSubjectPublicKeyInfo(full.ExportSubjectPublicKeyInfo(), out _);
        Assert.Equal(Protocol.Fingerprint(full), Protocol.Fingerprint(publicOnly));
    }

    [Fact]
    public void Fingerprint_DifferentKeys_ProduceDifferentValues()
    {
        using var a = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var b = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.NotEqual(Protocol.Fingerprint(a), Protocol.Fingerprint(b));
    }
}
