namespace OmniPlay.Core.Interfaces;

public interface IExternalLinkOpener
{
    bool TryOpen(string target, out string? errorMessage);
}
