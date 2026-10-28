using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using CMUmcl.Auth;

namespace CMUmcl
{
    public partial class ProfileSelectWindow : Window
    {
        public YggdrasilProfile? SelectedProfile { get; private set; }

        public ProfileSelectWindow(IEnumerable<YggdrasilProfile> profiles)
        {
            InitializeComponent();
            foreach (var p in profiles)
            {
                ProfileList.Items.Add(p);
            }
            if (ProfileList.Items.Count > 0)
            {
                ProfileList.SelectedIndex = 0;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmSelection();
        }

        private void ProfileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ProfileList.SelectedItem is YggdrasilProfile)
            {
                ConfirmSelection();
            }
        }

        private void ConfirmSelection()
        {
            if (ProfileList.SelectedItem is YggdrasilProfile profile)
            {
                SelectedProfile = profile;
                DialogResult = true;
            }
            else
            {
                MessageBox.Show(this, "请选择一个角色。", "CMUmcl", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
