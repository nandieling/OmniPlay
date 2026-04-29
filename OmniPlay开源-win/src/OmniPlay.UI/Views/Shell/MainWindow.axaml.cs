using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OmniPlay.Core.ViewModels;
using OmniPlay.Core.ViewModels.Library;

namespace OmniPlay.UI.Views.Shell;

public partial class MainWindow : Window
{
    private ShellViewModel? currentViewModel;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += MainWindow_OnDataContextChanged;
    }

    private void MainWindow_OnDataContextChanged(object? sender, EventArgs e)
    {
        if (currentViewModel is not null)
        {
            currentViewModel.PosterWall.PropertyChanged -= PosterWall_OnPropertyChanged;
        }

        currentViewModel = DataContext as ShellViewModel;

        if (currentViewModel is not null)
        {
            currentViewModel.PosterWall.PropertyChanged += PosterWall_OnPropertyChanged;
        }

        HideShellWindowControls();
    }

    private void PosterWall_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PosterWallViewModel.IsPlayerOverlayOpen))
        {
            HideShellWindowControls();
        }
    }

    private void ShellWindowControlsHotZone_OnPointerActivity(object? sender, PointerEventArgs e)
    {
        ShowShellWindowControls();
    }

    private void ShellWindowControlsBar_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        ShowShellWindowControls();
    }

    private void ShellWindowControlsBar_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        ShowShellWindowControls();
    }

    private void ShellWindowControlsBar_OnPointerExited(object? sender, PointerEventArgs e)
    {
        HideShellWindowControls();
    }

    private void ShellWindowControlsBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsWithinShellWindowButton(e.Source as Control) ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginMoveDrag(e);
        e.Handled = true;
    }

    private static bool IsWithinShellWindowButton(Control? control)
    {
        for (var current = control; current is not null; current = current.Parent as Control)
        {
            if (current is Button)
            {
                return true;
            }
        }

        return false;
    }

    private void ShellMinimizeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void ShellMaximizeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void ShellCloseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ShowShellWindowControls()
    {
        if (currentViewModel?.PosterWall.IsPlayerOverlayOpen == true)
        {
            HideShellWindowControls();
            return;
        }

        ShellWindowControlsBar.IsVisible = true;
    }

    private void HideShellWindowControls()
    {
        ShellWindowControlsBar.IsVisible = false;
    }
}
