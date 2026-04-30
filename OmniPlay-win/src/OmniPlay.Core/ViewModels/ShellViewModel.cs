using CommunityToolkit.Mvvm.ComponentModel;
using OmniPlay.Core.ViewModels.Library;

namespace OmniPlay.Core.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    public ShellViewModel(PosterWallViewModel posterWall)
    {
        PosterWall = posterWall;
    }

    [ObservableProperty]
    private string title = "OmniPlay";

    public PosterWallViewModel PosterWall { get; }
}
