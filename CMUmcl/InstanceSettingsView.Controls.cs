// SPDX-License-Identifier: GPL-3.0-only
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using CMUmcl.Launch;

namespace CMUmcl;

internal sealed partial class InstanceSettingsView
{
    private readonly ComboBox _javaCandidates = new(), _resolution = new();
    private readonly Slider _memorySlider = new();
    private readonly TextBlock _javaInfo = new(), _memoryInfo = new(), _javaChoiceHint = new(), _javaPathHint = new();
    private Button _scanJava = null!, _javaAutomatic = null!;
    private readonly CancellationTokenSource _javaScanCts = new();
    private readonly MemorySnapshot _physicalMemory = MemorySnapshot.Read();
    private bool _scanningJava, _syncMemory, _syncJavaChoice, _syncResolution;

    private async Task ScanJavaChoices()
    {
        if (_scanningJava || IsClosed) return;
        _scanningJava = true; _scanJava.IsEnabled = false; _javaInfo.Text = "正在检测本机 Java…";
        try
        {
            var found = await Task.Run(() => JavaLocator.Scan(_javaScanCts.Token), _javaScanCts.Token);
            if (IsClosed) return;
            // Replacing a list must not silently change a manually selected path.
            string selectedPath = _java.Text;
            _javaCandidates.ItemsSource = found.OrderByDescending(x => x.Major).ThenByDescending(x => x.Bits).ToArray();
            _javaCandidates.SelectedItem = found.FirstOrDefault(x => string.Equals(x.JavaPath, selectedPath, StringComparison.OrdinalIgnoreCase));
            UpdateJavaDescription();
            if (found.Count == 0) _javaInfo.Text = "未检测到 Java，可手动选择。";
        }
        catch (OperationCanceledException) { }
        catch { if (!IsClosed) _javaInfo.Text = "检测未完成，请重试或浏览本机路径。"; }
        finally { _scanningJava = false; if (!IsClosed) _scanJava.IsEnabled = true; }
    }

    // Autosave validates on every change; probing the same unchanged executable once is enough.
    private (string Path, DateTime Stamp, JavaInfo? Info)? _javaProbe;
    private JavaInfo? DetectJavaCached(string path)
    {
        DateTime stamp; try { stamp = System.IO.File.GetLastWriteTimeUtc(path); } catch { stamp = default; }
        if (_javaProbe is { } probe && string.Equals(probe.Path, path, StringComparison.OrdinalIgnoreCase) && probe.Stamp == stamp) return probe.Info;
        var info = JavaLocator.DetectExecutable(path);
        _javaProbe = (path, stamp, info);
        return info;
    }

    private JavaInfo? SelectedJava() => _javaCandidates.Items.OfType<JavaInfo>().FirstOrDefault(x => string.Equals(x.JavaPath, _java.Text.Trim(), StringComparison.OrdinalIgnoreCase));

    private void UpdateJavaDescription()
    {
        _syncJavaChoice=true;
        try { _javaCandidates.SelectedItem=SelectedJava(); } finally { _syncJavaChoice=false; }
        _javaChoiceHint.Text=string.IsNullOrWhiteSpace(_java.Text)?"自动选择兼容版本":"检测或浏览本机 Java";
        _javaChoiceHint.Visibility=_javaCandidates.SelectedItem==null?System.Windows.Visibility.Visible:System.Windows.Visibility.Collapsed;
        _javaPathHint.Visibility=string.IsNullOrWhiteSpace(_java.Text)?System.Windows.Visibility.Visible:System.Windows.Visibility.Collapsed;
        _javaInfo.Text = string.IsNullOrWhiteSpace(_java.Text) ? "缺少 Java 时自动下载。"
            : SelectedJava()?.DisplayName ?? "使用指定的 Java";
        _java.ToolTip = string.IsNullOrWhiteSpace(_java.Text) ? "Java 路径；留空时自动选择" : _java.Text;
        UpdateMemoryControls();
    }

    private void UpdateResolutionChoice()
    {
        if(_syncResolution)return;
        _syncResolution=true;
        try
        {
            string size=_width.Text.Trim()+" × "+_height.Text.Trim();
            _resolution.SelectedIndex=_resolution.Items.Contains(size)?_resolution.Items.IndexOf(size):0;
        }
        finally { _syncResolution=false; }
    }

    private void UpdateMemoryControls()
    {
        if (_syncMemory) return;
        var snapshot = MemorySnapshot.Read(); int bits = SelectedJava()?.Bits ?? 64;
        int allocation = _auto.IsChecked == true ? MemoryAllocation.Recommend(snapshot, bits)
            : int.TryParse(_memory.Text, out int manual) ? manual : 0;
        _syncMemory = true;
        try { _memorySlider.Value = Math.Max(_memorySlider.Minimum, Math.Min(_memorySlider.Maximum, allocation)); }
        finally { _syncMemory = false; }
        string system = snapshot.TotalMb > 0 ? $"物理内存 {snapshot.TotalMb / 1024d:0.#} GiB · 当前可用 {snapshot.AvailableMb / 1024d:0.#} GiB" : "无法读取物理内存";
        string? warning = _auto.IsChecked == true ? null : MemoryAllocation.ValidateManual(allocation, snapshot, bits);
        _memoryInfo.Text = system + (_auto.IsChecked == true ? $" · 预计分配 {allocation} MiB" : "")
            + (warning != null ? "\n" + warning : allocation > snapshot.AvailableMb && snapshot.TotalMb > 0 ? "\n分配值高于当前可用内存。" : "");
    }
}
