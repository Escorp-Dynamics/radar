using System.Text.Json;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>Новые каналы по умолчанию добавляются в уже сохранённые настройки.</summary>
public class SettingsMergeTests
{
    private static readonly string[] Old13 =
    [
        "LPRalarm", "kupolrussia", "lpr1_Crimea_Alarm", "lpr1_Krasnodar_alarm", "lpr1_Rostov_alarm", "lpr1_Belgorod_alarm",
        "lpr1_Kursk_alarm", "lpr1_Bryansk_alarm", "lpr1_Orlov_alarm", "lpr1_Voronezh_alarm", "lpr1_Kherson_alarm",
        "lpr1_LDPR_alarm", "lpr1_Kharkov_alarm",
    ];

    private static AppSettings Read(string json) => JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!;

    private static string Channels(IEnumerable<string> names) =>
        "[" + string.Join(",", names.Select(n => $"{{\"name\":\"{n}\",\"enabled\":true}}")) + "]";

    [Fact]
    public void OldFileWithoutKnownListGetsNewChannels()
    {
        var s = Read($"{{\"channels\":{Channels(Old13)}}}");
        Assert.True(SettingsStore.MergeNewChannels(s));
        Assert.Equal(AppSettings.DefaultChannels().Count, s.Channels.Count);
        Assert.Contains(s.Channels, c => c.Name == "locatorru");
    }

    [Fact]
    public void BrokenKnownListFromPreviousVersionIsRepaired()
    {
        // так выглядел файл пользователя: «виденными» помечены все 21, а каналов — 13
        var known = string.Join(",", AppSettings.DefaultChannels().Select(c => $"\"{c.Name}\""));
        var s = Read($"{{\"channels\":{Channels(Old13)},\"knownChannels\":[{known}]}}");
        SettingsStore.MergeNewChannels(s);
        Assert.Equal(AppSettings.DefaultChannels().Count, s.Channels.Count);
        Assert.Equal(AppSettings.CurrentChannelListVersion, s.ChannelListVersion);
    }

    [Fact]
    public void ChannelRemovedByUserStaysRemoved()
    {
        var s = Read($"{{\"channels\":{Channels(Old13)}}}");
        SettingsStore.MergeNewChannels(s);
        s.Channels.RemoveAll(c => c.Name == "locatorru");
        var again = Read(JsonSerializer.Serialize(s, SettingsJsonContext.Default.AppSettings));
        Assert.False(SettingsStore.MergeNewChannels(again));
        Assert.DoesNotContain(again.Channels, c => c.Name == "locatorru");
    }
}
