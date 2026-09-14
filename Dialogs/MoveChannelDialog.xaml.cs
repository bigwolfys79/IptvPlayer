using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IptvPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer.Dialogs;

/// <summary>
/// Диалог переноса канала в другую группу: редактируемый комбобокс — выбор
/// существующей группы или ввод имени новой. Возвращает итоговое имя группы
/// (null — отменено).
/// </summary>
public sealed partial class MoveChannelDialog : UserControl
{
    private string? _result;
    private ContentDialog? _hostDialog;

    private MoveChannelDialog(string channelName, IReadOnlyList<string> groups, string? currentGroup)
    {
        InitializeComponent();
        ChannelNameText.Text = channelName;
        HintText.Text = L.T("Vyberite_Gruppu");
        CancelButton.Content = L.T("Otmena_Lbl");
        MoveButton.Content = L.T("Perenesti");

        foreach (var group in groups)
        {
            GroupCombo.Items.Add(group);
        }

        GroupCombo.Text = currentGroup ?? string.Empty;
    }

    /// <summary>
    /// Показывает диалог и возвращает выбранную/введённую группу
    /// (null — отменено или имя пустое).
    /// </summary>
    public static async Task<string?> PickAsync(
        XamlRoot xamlRoot, string channelName, IReadOnlyList<string> groups, string? currentGroup)
    {
        var control = new MoveChannelDialog(channelName, groups, currentGroup);
        var dialog = new ThemedContentDialog
        {
            XamlRoot = xamlRoot,
            Title = L.T("Perenesti_V_Gruppu"),
            Content = control
        };
        control._hostDialog = dialog;
        await dialog.ShowAsync();
        return control._result;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _result = null;
        _hostDialog?.Hide();
    }

    private void MoveButton_Click(object sender, RoutedEventArgs e)
    {
        var group = GroupCombo.Text?.Trim();
        if (string.IsNullOrEmpty(group))
        {
            return;
        }

        _result = group;
        _hostDialog?.Hide();
    }
}
