using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IptvPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer.Dialogs;


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


    // Show dialog, return target group
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

    // Close without moving
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _result = null;
        _hostDialog?.Hide();
    }

    // Confirm group name
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
