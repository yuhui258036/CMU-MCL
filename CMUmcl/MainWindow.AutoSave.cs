using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CMUmcl.Appearance;
using CMUmcl.Content;
using CMUmcl.Launch;

namespace CMUmcl;

public partial class MainWindow
{
    private bool _preferencesReady, _closingAfterFlush, _confirmingClose;
    private AppSettings? _unsavedSettings;
    private static AppSettings CopySettings(AppSettings value) => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(value))!;

    // Each valid field commits independently: an incomplete Java path cannot block theme/memory edits.
    private bool SavePreference(Action<AppSettings> change)
    {
        if (!_preferencesReady || _loadingPreferences || _windowClosed) return false;
        var next = CopySettings(_unsavedSettings ?? _settings);
        change(next);
        SettingsStore.Sanitize(next);
        if (_unsavedSettings == null && JsonSerializer.Serialize(next) == JsonSerializer.Serialize(_settings)) return true;
        if (!_settingsStore.Save(next))
        {
            _unsavedSettings = next;
            PreferenceNotice.Visibility = Visibility.Visible;
            RetryPreferenceButton.Visibility = Visibility.Visible;
            SettingsStatusText.Text = "设置保存失败，请检查配置目录的写入权限。";
            return false;
        }
        _settings = next; _unsavedSettings = null;
        PreferenceNotice.Visibility = Visibility.Collapsed;
        RetryPreferenceButton.Visibility = Visibility.Collapsed;
        _ = AppearanceManager.ApplyAsync(next);
        return true;
    }
    private void RetryPreference_Click(object sender, RoutedEventArgs e) => SavePreference(_ => { });
    private void AutoUpdatePacks_Changed(object sender, RoutedEventArgs e)
        => SavePreference(s => s.AutoUpdatePacks = AutoUpdatePacksCheck.IsChecked == true);

    private async void Preferences_Closing(object? sender, CancelEventArgs e)
    {
        if (_migrating) { e.Cancel = true; return; }
        if (_closingAfterFlush || _updatingLauncher || !_preferencesReady) return;
        _globalSettingsInline?.FlushPendingSave();
        if (_globalSettingsInline?.HasUnsavedChanges != true) return;
        e.Cancel = true;
        if (await ResolveUnsavedGameDefaultsAsync("退出")) { _closingAfterFlush = true; Close(); }
    }

    // Game defaults save on change; only edits that failed validation can still be pending here.
    // Returns true when the caller may continue (nothing pending, saved, or discarded).
    private async Task<bool> ResolveUnsavedGameDefaultsAsync(string action)
    {
        var editor = _globalSettingsInline;
        editor?.FlushPendingSave();
        if (editor == null || !editor.HasUnsavedChanges) return true;
        if (_confirmingClose) return false;
        _confirmingClose = true;
        try
        {
            ShowSettingsView();
            ShowSettingsTab(RuntimeTab);
            var choice = await ShowInlinePromptAsync("设置未保存", $"全局游戏设置中有修改未通过校验，尚未保存。仍要{action}吗？这些修改将丢失。", "仍要" + action);
            return !_windowClosed && choice == InlineChoice.Continue;
        }
        finally { _confirmingClose = false; }
    }

    private async Task ChangeInstancesDirectoryAsync(string? directory)
    {
        if (!_preferencesReady || _migrating || _updatingLauncher) return;
        if (directory != null)
        {
            directory = LauncherPaths.NormalizeInstancesDirectory(directory);
            if (directory == null) { ShowInstancesError("游戏目录无效，请选择其他目录。"); return; }
        }
        string oldDirectory = LauncherPaths.InstancesDirectory;
        string destination = directory ?? LauncherPaths.DefaultInstancesDirectory;
        if (string.Equals(oldDirectory, destination, StringComparison.OrdinalIgnoreCase)) return;
        var records = _installedState.ListRecords();
        if (records.Count == 0)
        {
            if (SavePreference(s => s.InstancesDirectory = directory)) InstancesDirectoryTextBox.Text = directory ?? "";
            return;
        }
        if (LauncherPrompt.Show(this, $"将 {records.Count} 个实例从\n{oldDirectory}\n移动到\n{destination}\n\n是否继续？", "更改游戏目录", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            _migrating = true; SettingsContentScroll.IsEnabled = false;
            IReadOnlyDictionary<string, MigrationNameOverride>? overrides = null;
            var conflicts = InstanceMigrationService.FindConflicts(oldDirectory, destination, records);
            if (conflicts.Count > 0)
            {
                var dialog = new MigrationConflictWindow(conflicts, destination) { Owner = this };
                if (dialog.ShowDialog() != true || dialog.Result == null) return;
                overrides = dialog.Result;
            }
            PreferenceNotice.Visibility = Visibility.Visible;
            var progress = new Progress<MigrationProgress>(p => SettingsStatusText.Text = p.Message);
            var result = await InstanceMigrationService.MigrateAsync(oldDirectory, destination, records, progress, overrides: overrides);
            string? previous = _settings.InstancesDirectory;
            bool saved = SavePreference(s => s.InstancesDirectory = directory);
            if (saved && _installedState.ApplyMigration(result.UpdatedRecords))
            {
                DeleteDirectories(result.SourceDirectories);
                InstancesDirectoryTextBox.Text = directory ?? "";
                RefreshCardDisplayNames();
            }
            else
            {
                // Do not retry a directory preference after removing its migration copies.
                if (_unsavedSettings != null) _unsavedSettings.InstancesDirectory = previous;
                bool rolledBack = !saved || SavePreference(s => s.InstancesDirectory = previous);
                if (rolledBack) DeleteDirectories(result.TargetDirectories);
                PreferenceNotice.Visibility = Visibility.Visible;
                SettingsStatusText.Text = "更改游戏目录失败，原实例仍然保留。";
            }
        }
        catch (Exception ex)
        {
            PreferenceNotice.Visibility = Visibility.Visible;
            SettingsStatusText.Text = "更改游戏目录失败：" + ex.Message;
        }
        finally { _migrating = false; SettingsContentScroll.IsEnabled = true; }
    }
}
