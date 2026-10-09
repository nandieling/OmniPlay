namespace OmniPlay.Core.ViewModels.Library;

public enum ToastKind
{
    Info = 0,
    Success = 1,
    Error = 2
}

public sealed class ToastItem
{
    public ToastItem(string text, ToastKind kind)
    {
        Text = text;
        Kind = kind;
    }

    public string Text { get; }

    public ToastKind Kind { get; }

    public bool IsInfo => Kind == ToastKind.Info;

    public bool IsSuccess => Kind == ToastKind.Success;

    public bool IsError => Kind == ToastKind.Error;
}
