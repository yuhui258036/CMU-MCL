// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using CMUmcl.Content;

namespace CMUmcl;

// Version and loader dropdowns that load their own lists; no separate "fetch list" buttons.
internal sealed partial class InstanceSettingsView
{
    private string _lastGameVersion = "", _loaderListKey = "";
    private bool _gameListLoaded;
    private int _loaderRequest;

    private void SetUpVersionPickers(InstanceMetadata? metadata)
    {
        foreach (var combo in new[] { _gameVersion, _loaderVersion })
        {
            // Hundreds of entries: virtualize so the dropdown opens immediately.
            combo.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
            VirtualizingPanel.SetIsVirtualizing(combo, true);
        }
        // Like a launcher download list: every version, newest first, with its type and date.
        System.Windows.Controls.TextSearch.SetTextPath(_gameVersion, nameof(VersionChoice.Name));
        _gameVersion.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<DockPanel><TextBlock DockPanel.Dock='Right' Text='{Binding Detail}' Margin='16,0,0,0' FontSize='12' Foreground='{DynamicResource MutedTextBrush}' VerticalAlignment='Center'/>" +
            "<TextBlock Text='{Binding Name}' VerticalAlignment='Center'/></DockPanel></DataTemplate>");
        var itemStyle = new Style(typeof(ComboBoxItem), TryFindResource(typeof(ComboBoxItem)) as Style);
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _gameVersion.ItemContainerStyle = itemStyle;
        _gameVersion.Text = metadata?.Game ?? "";
        _lastGameVersion = _gameVersion.Text.Trim();
        _loaderKind.SelectedItem = string.IsNullOrEmpty(metadata?.Loader) ? "vanilla" : metadata!.Loader;
        _loaderVersion.Text = metadata?.LoaderVersion ?? "";
        UpdateLoaderVersionState();

        _gameVersion.DropDownOpened += async (_, _) => await FillGameVersionsAsync();
        _loaderVersion.DropDownOpened += async (_, _) => await FillLoaderVersionsAsync(false);
        _loaderKind.SelectionChanged += async (_, _) =>
        {
            UpdateLoaderVersionState();
            if (!IsVanilla) { _loaderVersion.Text = ""; await FillLoaderVersionsAsync(true); }
        };
        _gameVersion.SelectionChanged += async (_, _) => await GameVersionChangedAsync();
        _gameVersion.LostKeyboardFocus += async (_, _) => await GameVersionChangedAsync();
    }

    // An editable ComboBox raises SelectionChanged before Text follows the new item.
    private string CurrentGameVersion => ((_gameVersion.SelectedItem as VersionChoice)?.Name ?? _gameVersion.Text).Trim();

    private bool IsVanilla => (_loaderKind.SelectedItem as string ?? "vanilla") == "vanilla";

    /// <summary>Vanilla has no loader build: the field is cleared and disabled.</summary>
    private void UpdateLoaderVersionState()
    {
        _loaderVersion.IsEnabled = !IsVanilla;
        if (IsVanilla) { _loaderVersion.ItemsSource = null; _loaderVersion.Text = ""; _loaderListKey = ""; }
    }

    /// <summary>Starts loading both lists when the components tab is shown.</summary>
    private void PrefetchVersionLists()
    {
        _ = FillGameVersionsAsync(quiet: true);
        if (!IsVanilla) _ = FillLoaderVersionsAsync(false, quiet: true);
        _ = RefreshComponentStateAsync();
    }

    private async Task FillGameVersionsAsync(bool quiet = false)
    {
        if (_gameListLoaded || IsClosed) return;
        if (!quiet) _status.Text = "正在加载版本列表…";
        VersionChoice[] values;
        try { values = await VersionChoices.GameVersionsAsync(); } catch { values = Array.Empty<VersionChoice>(); }
        if (IsClosed || _gameListLoaded) return;
        if (values.Length == 0) { if (!quiet) _status.Text = "无法获取版本列表，可直接输入版本号。"; return; }
        string text = _gameVersion.Text.Trim();
        // Keep a custom version the instance already uses (for example a modified client).
        _gameVersion.ItemsSource = text.Length > 0 && !values.Any(v => v.Name == text) ? new[] { new VersionChoice(text, "当前版本", "") }.Concat(values).ToArray() : values;
        _gameVersion.Text = text;
        _gameListLoaded = true;
        if (!quiet) _status.Text = "";
    }

    private async Task GameVersionChangedAsync()
    {
        string game = CurrentGameVersion;
        if (game == _lastGameVersion) return;
        _lastGameVersion = game;
        if (!IsVanilla) await FillLoaderVersionsAsync(true);
    }

    /// <param name="pickLatest">Select the recommended build unless the current one is still compatible.</param>
    private async Task FillLoaderVersionsAsync(bool pickLatest, bool quiet = false)
    {
        string loader = _loaderKind.SelectedItem as string ?? "vanilla", game = CurrentGameVersion;
        if (loader == "vanilla" || game.Length == 0 || IsClosed) return;
        string key = loader + "|" + game;
        if (key == _loaderListKey && _loaderVersion.ItemsSource != null) return;
        int request = ++_loaderRequest;
        if (!quiet) _status.Text = "正在加载加载器版本…";
        string[] values;
        try { values = await VersionChoices.LoaderVersionsAsync(loader, game); } catch { values = Array.Empty<string>(); }
        if (IsClosed || request != _loaderRequest) return;
        if (values.Length == 0)
        {
            _loaderVersion.ItemsSource = null; _loaderListKey = "";
            if (!quiet || pickLatest) _status.Text = $"没有找到适用于 Minecraft {game} 的 {loader} 版本，或网络不可用。";
            return;
        }
        string current = _loaderVersion.Text.Trim();
        _loaderVersion.ItemsSource = values; _loaderListKey = key;
        _loaderVersion.Text = (pickLatest && !values.Contains(current)) || current.Length == 0 ? values[0] : current;
        if (!quiet) _status.Text = "";
    }
}
