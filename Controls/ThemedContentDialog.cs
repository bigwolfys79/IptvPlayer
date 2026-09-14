using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer;


public sealed partial class ThemedContentDialog : ContentDialog
{
    public ThemedContentDialog()
    {
        if (MainWindow.Instance?.Content is FrameworkElement root)
        {


            RequestedTheme = root.ActualTheme;
        }
    }
}
