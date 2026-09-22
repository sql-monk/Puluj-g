using System.Text.Json;
using Puluj.Domain;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;

namespace Puluj.Collectors.Tests;

/// <summary>One channel, one code: the convention from docs/naming.md ("Коди джерел") and the duplicate-channel lookup behind it.</summary>
public class SourceCodesTests
{
    [Theory]
    [InlineData("kpszsu", "kpszsu")]
    [InlineData("@KPSZSU", "KPSZSU")]
    [InlineData("https://t.me/UkraineAlarmSignal", "UkraineAlarmSignal")]
    [InlineData("t.me/kudy_letyt/12345", "kudy_letyt")]
    [InlineData("  @name?x=1 ", "name")]
    public void Username_is_normalized(string raw, string expected) =>
        Assert.Equal(expected, SourceCodes.NormalizeTelegramUsername(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@")]
    [InlineData("https://t.me/")]
    public void Empty_username_is_null(string? raw) => Assert.Null(SourceCodes.NormalizeTelegramUsername(raw));

    [Theory]
    [InlineData("kpszsu", "tg_kpszsu")]
    [InlineData("@UkraineAlarmSignal", "tg_ukrainealarmsignal")]
    [InlineData("https://t.me/Radar_Dnipra", "tg_radar_dnipra")]
    public void Telegram_code_is_tg_plus_lowercase_username(string channel, string code) =>
        Assert.Equal(code, SourceCodes.Telegram(channel));

    [Fact]
    public void Telegram_code_needs_a_username() => Assert.Throws<ArgumentException>(() => SourceCodes.Telegram("@"));

    [Fact]
    public void Channel_of_a_source_comes_from_config_for_telegram_only()
    {
        Assert.Equal("kpszsu", SourceCodes.TelegramChannel(Telegram("kpszsu", "@kpszsu")));
        Assert.Null(SourceCodes.TelegramChannel(Telegram("x", null)));
        Assert.Null(SourceCodes.TelegramChannel(new Source { Code = "alerts_in_ua", Name = "a", Type = SourceType.RestApi, Config = JsonDocument.Parse("""{"channel":"kpszsu"}""") }));
    }

    [Fact]
    public void Same_channel_is_found_whatever_the_code_or_case()
    {
        Source[] sources = [Telegram("kherson_monitoring", "kherson_monitoring"), Telegram("tg_radar_dnipra", "Radar_Dnipra"), Telegram("odd", null)];
        Assert.Equal("kherson_monitoring", SourceCodes.FindTelegramChannel(sources, "@KHERSON_monitoring")?.Code);
        Assert.Equal("tg_radar_dnipra", SourceCodes.FindTelegramChannel(sources, "https://t.me/radar_dnipra")?.Code);
        Assert.Null(SourceCodes.FindTelegramChannel(sources, "kpszsu"));
        Assert.Null(SourceCodes.FindTelegramChannel(sources, null));
        Assert.Null(SourceCodes.FindTelegramChannel(sources, ""));
    }

    private static Source Telegram(string code, string? channel) => new()
    {
        Code = code,
        Name = code,
        Type = SourceType.Telegram,
        Config = channel is null ? null : JsonDocument.Parse(JsonSerializer.Serialize(new { channel })),
    };
}
