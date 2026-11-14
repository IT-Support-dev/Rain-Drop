using System.Text.Json;
using RainDrop.Shared;
using Xunit;

namespace RainDrop.Tests;

/// <summary>
/// เทสต์การอ่าน/เขียน BlockRules ซึ่งเป็นสัญญาตาระหว่าง Agent (คนเขียน blocklist.json)
/// กับ AgentService (คนอ่านเพื่อแก้ไฟล์ hosts) ทั้งสองฝั่งใช้ JsonSerializer ค่าเริ่มต้น
/// </summary>
public class BlockRulesTests
{
    private static BlockRules Parse(string json) =>
        JsonSerializer.Deserialize<BlockRules>(json) ?? throw new InvalidDataException("JSON ว่าง");

    [Fact]
    public void Deserialize_ReadsProgramsAndUrls()
    {
        var rules = Parse("""{"Programs":["chrome","game.exe"],"Urls":["facebook.com","youtube.com"]}""");

        Assert.Equal(new[] { "chrome", "game.exe" }, rules.Programs);
        Assert.Equal(new[] { "facebook.com", "youtube.com" }, rules.Urls);
    }

    [Fact]
    public void Deserialize_EmptyArrays_GiveEmptyLists()
    {
        var rules = Parse("""{"Programs":[],"Urls":[]}""");

        Assert.Empty(rules.Programs);
        Assert.Empty(rules.Urls);
    }

    [Fact]
    public void SerializeThenDeserialize_RoundTripsBothLists()
    {
        var rules = new BlockRules(
            new List<string> { "chrome", "LINE" },
            new List<string> { "facebook.com", "https://youtube.com/watch" });

        string json = JsonSerializer.Serialize(rules, new JsonSerializerOptions { WriteIndented = true });
        var back = Parse(json);

        Assert.Equal(rules.Programs, back.Programs);
        Assert.Equal(rules.Urls, back.Urls);
    }

    [Fact]
    public void Serialize_UsesThePropertyNamesThatAgentServiceReads()
    {
        // AgentService อ่านผ่านคลาสที่มี property Programs/Urls ด้วยตัวเลือกเริ่มต้น (case-sensitive)
        // ถ้าชื่อเพี้ยนจากนี้ การบล็อกเว็บจะพังแบบเงียบ ๆ จึงล็อกไว้ด้วยเทสต์
        var rules = new BlockRules(new List<string> { "chrome" }, new List<string> { "example.com" });
        string json = JsonSerializer.Serialize(rules);

        Assert.Contains("\"Programs\"", json);
        Assert.Contains("\"Urls\"", json);
    }

    [Fact]
    public void Deserialize_IgnoresUnknownExtraProperties()
    {
        // ไฟล์จากรุ่นอนาคตอาจมีฟิลด์เพิ่ม ต้องไม่ทำให้การอ่านล้ม
        var rules = Parse("""{"Programs":["chrome"],"Urls":["example.com"],"FutureField":123}""");

        Assert.Single(rules.Programs);
        Assert.Single(rules.Urls);
    }
}
