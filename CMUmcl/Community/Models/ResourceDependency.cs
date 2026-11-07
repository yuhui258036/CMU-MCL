// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Windows.Media.Imaging;

namespace CMUmcl.Community
{
    internal enum DependencyKind { Required, Optional, Incompatible, Embedded, EmbeddedLibrary, Tool, Unknown }

    internal sealed class ResourceDependency
    {
        public string? ProjectId;
        public string? VersionId;
        public string? FileName;
        public DependencyKind Kind;
        public string Identity => Kind + ":" + ProjectId + ":" + VersionId + ":" + FileName;
    }

    internal sealed class ResolvedDependency
    {
        public ResourceProject Project { get; }
        public ResourceVersion? PinnedVersion { get; }
        public ResolvedDependency(ResourceProject project, ResourceVersion? pinnedVersion) { Project = project; PinnedVersion = pinnedVersion; }
    }

    internal sealed class DependencyItemViewModel : ObservableObject
    {
        public ResourceDependency Reference { get; }
        public ResolvedDependency? Resolution { get; private set; }
        public DependencyItemViewModel(ResourceDependency reference) { Reference = reference; }
        public string GroupLabel => Reference.Kind switch {
            DependencyKind.Required => "必需前置（缺少可能无法启动）",
            DependencyKind.Optional => "可选前置（按需安装）",
            DependencyKind.Incompatible => "不兼容模组（不建议同时安装）",
            DependencyKind.Embedded => "已包含的组件（通常无需单独安装）",
            DependencyKind.EmbeddedLibrary => "嵌入库 / 运行库（请核对作者说明）",
            DependencyKind.Tool => "工具 / 附属内容", _ => "其他关联（请核对作者说明）"
        };
        public string Title => Resolution?.Project.Title ?? Reference.FileName ?? Reference.ProjectId ?? Reference.VersionId ?? "未知前置";
        public string Description => HasError ? "无法读取前置信息，请重试或到来源网站核对。" : Resolution?.Project.Description ?? "正在读取前置信息…";
        public string VersionNote => !string.IsNullOrEmpty(Reference.VersionId) ? "指定版本：" + (Resolution?.PinnedVersion?.VersionNumber ?? Reference.VersionId) : "请选择与目标实例兼容的版本";
        public bool CanOpen => Resolution != null;
        private bool _hasError;
        public bool HasError { get => _hasError; private set => Set(ref _hasError, value); }
        private BitmapSource? _icon;
        public BitmapSource? Icon { get => _icon; set => Set(ref _icon, value); }
        public void Complete(ResolvedDependency? value)
        {
            Resolution = value; HasError = value == null;
            OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Description)); OnPropertyChanged(nameof(VersionNote)); OnPropertyChanged(nameof(CanOpen));
        }
    }
}
