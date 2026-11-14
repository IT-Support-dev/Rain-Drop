using System.Buffers.Binary;
using RainDrop.Shared;
using Xunit;

namespace RainDrop.Tests;

/// <summary>
/// เทสต์โปรโตคอลห้องชุมชน (HubProtocol.cs): เฟรมเสียง, ชังก์ไฟล์ และการเขียน/อ่านเฟรม
/// </summary>
public class HubProtocolTests
{
    private static async Task<(HubMsg Type, byte[] Payload)> RoundTripAsync(HubMsg type, byte[] payload)
    {
        using var ms = new MemoryStream();
        await HubProtocol.WriteAsync(ms, type, payload);
        ms.Position = 0;
        return await HubProtocol.ReadAsync(ms);
    }

    // ---------- เฟรมเสียง ----------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void VoiceFrame_RoundTrip_PreservesSenderAndPcm(int senderId)
    {
        var pcm = new byte[640]; // 20 ms ที่ 16 kHz mono 16-bit
        Random.Shared.NextBytes(pcm);
        var frame = HubProtocol.MakeVoiceFrame(senderId, pcm);
        var (sender, back) = HubProtocol.ReadVoiceFrame(frame);
        Assert.Equal(senderId, sender);
        Assert.Equal(pcm, back);
    }

    [Fact]
    public void VoiceFrame_EmptyPcm_RoundTrips()
    {
        var frame = HubProtocol.MakeVoiceFrame(7, ReadOnlySpan<byte>.Empty);
        var (sender, pcm) = HubProtocol.ReadVoiceFrame(frame);
        Assert.Equal(7, sender);
        Assert.Empty(pcm);
    }

    // ---------- ชังก์ไฟล์ ----------

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(12345, true)]
    public void FileChunk_RoundTrip_PreservesAllFields(int seq, bool last)
    {
        var fileId = Guid.NewGuid();
        var data = new byte[HubProtocol.FileChunkSize];
        Random.Shared.NextBytes(data);
        var chunk = HubProtocol.MakeFileChunk(fileId, seq, last, data);
        var (id, backSeq, backLast, backData) = HubProtocol.ReadFileChunk(chunk);
        Assert.Equal(fileId, id);
        Assert.Equal(seq, backSeq);
        Assert.Equal(last, backLast);
        Assert.Equal(data, backData);
    }

    [Fact]
    public void FileChunk_EmptyData_LastFlag_RoundTrips()
    {
        var fileId = Guid.NewGuid();
        var chunk = HubProtocol.MakeFileChunk(fileId, 9, true, ReadOnlySpan<byte>.Empty);
        var (id, seq, last, data) = HubProtocol.ReadFileChunk(chunk);
        Assert.Equal(fileId, id);
        Assert.Equal(9, seq);
        Assert.True(last);
        Assert.Empty(data);
    }

    // ---------- Write/Read เฟรมเต็ม ----------

    [Fact]
    public async Task WriteRead_BinaryVoiceFrame_RoundTrips()
    {
        var pcm = new byte[320];
        Random.Shared.NextBytes(pcm);
        var payload = HubProtocol.MakeVoiceFrame(5, pcm);
        var (type, back) = await RoundTripAsync(HubMsg.VoiceFrame, payload);
        Assert.Equal(HubMsg.VoiceFrame, type);
        Assert.Equal(payload, back);

        var (sender, backPcm) = HubProtocol.ReadVoiceFrame(back);
        Assert.Equal(5, sender);
        Assert.Equal(pcm, backPcm);
    }

    [Fact]
    public async Task WriteRead_FileChunk_RoundTrips()
    {
        var fileId = Guid.NewGuid();
        var data = new byte[] { 10, 20, 30, 40, 50 };
        var payload = HubProtocol.MakeFileChunk(fileId, 3, true, data);
        var (type, back) = await RoundTripAsync(HubMsg.FileChunk, payload);
        Assert.Equal(HubMsg.FileChunk, type);

        var (id, seq, last, backData) = HubProtocol.ReadFileChunk(back);
        Assert.Equal(fileId, id);
        Assert.Equal(3, seq);
        Assert.True(last);
        Assert.Equal(data, backData);
    }

    [Fact]
    public async Task WriteJson_ChatPost_RoundTrips()
    {
        var post = new ChatPost("general", 7, "alice", "hello",
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), null);
        using var ms = new MemoryStream();
        await HubProtocol.WriteJsonAsync(ms, HubMsg.Chat, post);
        ms.Position = 0;
        var (type, payload) = await HubProtocol.ReadAsync(ms);
        Assert.Equal(HubMsg.Chat, type);
        Assert.Equal(post, HubProtocol.FromJson<ChatPost>(payload));
    }

    [Fact]
    public async Task ReadAsync_OversizeLength_Throws()
    {
        using var ms = new MemoryStream();
        var header = new byte[5];
        header[0] = (byte)HubMsg.Chat;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1, 4), int.MaxValue);
        ms.Write(header, 0, header.Length);
        ms.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => HubProtocol.ReadAsync(ms));
    }
}
