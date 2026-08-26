namespace OmniPlay.Core.Settings;

public sealed record LuckyStunSettings
{
    public string ManagementUrl { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public string SelectedRuleId { get; init; } = string.Empty;

    public string SelectedRuleName { get; init; } = string.Empty;

    public bool AutoUpdate { get; init; }

    public int UpdateIntervalMinutes { get; init; } = 30;

    public DateTimeOffset? LastUpdatedAt { get; init; }
}
