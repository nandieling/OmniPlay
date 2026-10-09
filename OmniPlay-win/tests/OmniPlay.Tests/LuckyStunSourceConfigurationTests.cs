using System.Text.Json;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Models.Network;

namespace OmniPlay.Tests;

public sealed class LuckyStunAddressHelperTests
{
    [Theory]
    [InlineData("1.2.3.4:45722", "http://1.2.3.4:45722")]
    [InlineData("http://1.2.3.4:45722", "http://1.2.3.4:45722")]
    [InlineData("https://host.example:45722/", "https://host.example:45722")]
    [InlineData("//1.2.3.4:45722", "http://1.2.3.4:45722")]
    [InlineData("some-name", "some-name")]
    public void NormalizeAddress_NormalizesSchemes(string input, string expected)
    {
        Assert.Equal(expected, LuckyStunAddressHelper.NormalizeAddress(input));
    }

    [Theory]
    [InlineData("", "http://1.2.3.4:45722", "http://1.2.3.4:45722")]
    [InlineData("45723", "http://1.2.3.4:45722", "http://1.2.3.4:45722/45723")]
    [InlineData("/45723/", "http://1.2.3.4:45722", "http://1.2.3.4:45722/45723")]
    [InlineData("45723", "http://1.2.3.4:45722/api", "http://1.2.3.4:45722/api/45723")]
    public void AppendPathSuffix_AppendsTrimmedSuffix(string suffix, string address, string expected)
    {
        Assert.Equal(expected, LuckyStunAddressHelper.AppendPathSuffix(suffix, address));
    }
}

public sealed class MediaSourceLuckyStunConfigTests
{
    [Fact]
    public void AddressConfiguration_RoundTripsLuckyStunThroughJson()
    {
        var configuration = new MediaSourceAddressConfiguration
        {
            LocalAddress = "http://1.2.3.4:45722",
            ActiveAddress = "http://1.2.3.4:45722",
            LuckyStun = new LuckyStunSourceConfiguration
            {
                ManagementUrl = "https://lucky.example",
                Username = "admin",
                Password = "secret",
                RuleId = "rule-1",
                RuleName = "主规则",
                AutoUpdate = true,
                UpdateIntervalMinutes = 45,
                LastUpdatedAt = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero),
                PathSuffix = "45722"
            }
        };

        var json = JsonSerializer.Serialize(configuration);
        var restored = JsonSerializer.Deserialize<MediaSourceAddressConfiguration>(json);

        Assert.NotNull(restored?.LuckyStun);
        Assert.Equal("https://lucky.example", restored!.LuckyStun!.ManagementUrl);
        Assert.Equal("admin", restored.LuckyStun.Username);
        Assert.Equal("rule-1", restored.LuckyStun.RuleId);
        Assert.Equal(45, restored.LuckyStun.UpdateIntervalMinutes);
        Assert.Equal("45722", restored.LuckyStun.PathSuffix);
    }

    [Fact]
    public void Clone_KeepsLuckyStunConfiguration()
    {
        var configuration = new MediaSourceAddressConfiguration
        {
            LuckyStun = new LuckyStunSourceConfiguration
            {
                ManagementUrl = "https://lucky.example",
                RuleId = "rule-1",
                PathSuffix = "45722"
            }
        };

        var clone = configuration.Clone();

        Assert.NotNull(clone.LuckyStun);
        Assert.NotSame(configuration.LuckyStun, clone.LuckyStun);
        Assert.Equal("https://lucky.example", clone.LuckyStun!.ManagementUrl);
    }

    [Fact]
    public void MediaSource_ReportsHasLuckyStunSourceConfig()
    {
        var without = new MediaSource { BaseUrl = "http://1.2.3.4:45722" };
        var with = new MediaSource
        {
            BaseUrl = "http://1.2.3.4:45722",
            AddressConfig = JsonSerializer.Serialize(new MediaSourceAddressConfiguration
            {
                LuckyStun = new LuckyStunSourceConfiguration { ManagementUrl = "https://lucky.example" }
            })
        };

        Assert.False(without.HasLuckyStunSourceConfig);
        Assert.True(with.HasLuckyStunSourceConfig);
    }
}
