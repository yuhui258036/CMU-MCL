// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CMUmcl.Auth;

namespace CMUmcl
{
    public partial class MainWindow
    {
        private SkinAvatarService? _avatarService;
        private SkinPreview? _skinPreview;
        private bool _refreshingSkin;
        private readonly CancellationTokenSource _avatarLifetime = new();
        internal void SetAccountAvatar(System.Windows.Media.Imaging.BitmapSource? image)
        {
            AccountAvatarImage.Source = image;
            AccountAvatarImage.Visibility = image == null ? Visibility.Collapsed : Visibility.Visible;
            AccountAvatarFallback.Visibility = image == null ? Visibility.Visible : Visibility.Collapsed;
        }
        private async void RefreshSkin_Click(object sender, RoutedEventArgs e) => await LoadAccountAvatarAsync(true);
        private async Task LoadAccountAvatarAsync(bool force = false)
        {
            if (_refreshingSkin || (!force && _avatarService != null)) return;
            if(_skinPreview==null){_skinPreview=new SkinPreview();SkinPreviewHost.Children.Add(_skinPreview);}
            if (SkinAvatarService.NormalizeId(_session.SelectedProfileId) == null) return;
            _refreshingSkin=true;RefreshSkinButton.IsEnabled=false;
            _avatarService ??= new SkinAvatarService();
            var cached = _avatarService.LoadCached(_session.SelectedProfileId);
            SetAccountAvatar(cached);
            try
            {
                var image = await _avatarService.GetAsync(_session.SelectedProfileId, _avatarLifetime.Token, force);
                if (!_windowClosed) { SetAccountAvatar(image);_skinPreview.SetSkin(_avatarService.Skin,_avatarService.IsSlim);RefreshSkinButton.ToolTip="已刷新"; }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if(!_windowClosed) RefreshSkinButton.ToolTip="皮肤预览暂时不可用，可重试刷新"; }
            finally { _refreshingSkin=false;if(!_windowClosed)RefreshSkinButton.IsEnabled=true; }
        }
    }
}
