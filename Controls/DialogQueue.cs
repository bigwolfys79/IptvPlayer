using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer;

// ContentDialog.ShowAsync throws 0x80070489 when another dialog is still open.
// Route every modal through here: callers queue up instead of crashing or
// silently losing the user's action.
public static class DialogQueue
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await Gate.WaitAsync();
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "DialogQueue: диалог {Type} не показан (коллизия модалок).",
                dialog.GetType().Name);
            return ContentDialogResult.None;
        }
        finally
        {
            Gate.Release();
        }
    }
}
