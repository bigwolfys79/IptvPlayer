using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer;

/// <summary>
/// ContentDialog, явно наследующий фактическую тему корня окна.
/// WinUI 3: всплывающий слой диалога не наследует RequestedTheme
/// корневого элемента окна — тема диалога разрешается в системную,
/// поэтому в светлой теме приложения диалог оставался тёмным
/// (и наоборот). Все ContentDialog'и приложения создаются этим классом.
/// </summary>
public sealed partial class ThemedContentDialog : ContentDialog
{
    public ThemedContentDialog()
    {
        if (MainWindow.Instance?.Content is FrameworkElement root)
        {
            // ActualTheme — уже разрешённые Light/Dark даже когда у корня
            // стоит Default (системная тема).
            RequestedTheme = root.ActualTheme;
        }
    }
}
