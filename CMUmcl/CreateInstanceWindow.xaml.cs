using System;
using System.Windows;
using System.Windows.Controls;
using CMUmcl.Content;

namespace CMUmcl
{
    /// <summary>Small dialog for creating a local (non-manifest) instance.</summary>
    public partial class CreateInstanceWindow : Window
    {
        private readonly LocalInstanceService _service;

        /// <summary>The created instance on success, or null.</summary>
        public LocalInstanceResult? Result { get; private set; }

        public CreateInstanceWindow(LocalInstanceService service)
        {
            InitializeComponent();
            _service = service;

            LoaderCombo.Items.Add(new ComboBoxItem { Content = "原版", Tag = "vanilla" });
            LoaderCombo.Items.Add(new ComboBoxItem { Content = "Fabric", Tag = "fabric" });
            LoaderCombo.Items.Add(new ComboBoxItem { Content = "Forge", Tag = "forge" });
            LoaderCombo.Items.Add(new ComboBoxItem { Content = "NeoForge", Tag = "neoforge" });
            LoaderCombo.Items.Add(new ComboBoxItem { Content = "Quilt", Tag = "quilt" });
            LoaderCombo.SelectedIndex = 0;

            UpdateLoaderVersionEnabled();
        }

        private string SelectedLoader => (LoaderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "vanilla";

        private void LoaderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LoaderVersionBox == null) return;

            UpdateLoaderVersionEnabled();
            ShowError(null);
        }

        private void UpdateLoaderVersionEnabled()
        {
            string loader = SelectedLoader;
            LoaderVersionBox.IsEnabled = loader is "fabric" or "forge" or "neoforge" or "quilt";
        }

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            string name = NameBox.Text?.Trim() ?? string.Empty;
            string gameVersion = GameVersionBox.Text?.Trim() ?? string.Empty;
            string loader = SelectedLoader;
            string loaderVersion = loader is "fabric" or "forge" or "neoforge" or "quilt"
                ? (LoaderVersionBox.Text?.Trim() ?? string.Empty)
                : string.Empty;

            if (name.Length == 0)
            {
                ShowError("请输入实例名称。");
                NameBox.Focus();
                return;
            }

            if (gameVersion.Length == 0)
            {
                ShowError("请输入 Minecraft 游戏版本。");
                GameVersionBox.Focus();
                return;
            }

            if (loader is "forge" or "neoforge" && loaderVersion.Length == 0)
            {
                ShowError("Forge / NeoForge 需要填写加载器版本。");
                LoaderVersionBox.Focus();
                return;
            }

            try
            {
                Result = _service.Create(new CreateLocalInstanceRequest
                {
                    DisplayName = name,
                    GameVersion = gameVersion,
                    Loader = loader,
                    LoaderVersion = loaderVersion,
                });

                DialogResult = true;
            }
            catch (LocalInstanceException ex)
            {
                ShowError(ex.Message);
            }
            catch (Exception)
            {
                ShowError("创建本地实例失败，发生了未知错误。");
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void ShowError(string? message)
        {
            ErrorText.Text = message ?? string.Empty;
        }
    }
}
