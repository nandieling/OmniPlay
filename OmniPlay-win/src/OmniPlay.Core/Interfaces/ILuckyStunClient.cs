using OmniPlay.Core.Settings;

namespace OmniPlay.Core.Interfaces;

public sealed record LuckyStunRule(
    string Id,
    string Name,
    string Address);

public sealed record LuckyStunSnapshot(
    IReadOnlyList<LuckyStunRule> Rules,
    LuckyStunRule? SelectedRule,
    string Message);

public interface ILuckyStunClient
{
    Task<LuckyStunSnapshot> FetchAsync(
        LuckyStunSettings settings,
        CancellationToken cancellationToken = default);
}
