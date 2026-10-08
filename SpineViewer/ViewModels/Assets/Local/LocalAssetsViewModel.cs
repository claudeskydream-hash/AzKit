using CommunityToolkit.Mvvm.Input;
using NLog;
using SpineViewer.Models;
using SpineViewer.Services;
using SpineViewer.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SpineViewer.ViewModels.Assets.Local
{
    public sealed class LocalAssetsViewModel : AssetsViewModel<LocalAssetsRepoViewModel, LocalAssetsItemViewModel>
    {
        /// <summary>
        /// 文件保存路径
        /// </summary>
        public static readonly string LocalAssetsFilePath = Path.Combine(App.DataDirectory, "localassets.json");

        public LocalAssetsViewModel(MainWindowViewModel vmMain) : base(vmMain)
        {
            // [AzureSail 新增] 导出设置在别处（文件 → 批量导出）改了也要跟着刷新按钮与标记
            Exporter.PropertyChanged += Exporter_PropertyChanged;
        }

        protected override IReadOnlyList<LocalAssetsRepoViewModel> AddAssetsRepos()
        {
            if (!DialogService.ShowOpenFolderDialog(out var selectedPath))
                return [];
            LocalAssetsRepoViewModel repo = new(selectedPath!);
            repo.IsBatchExportSource = IsBatchExportSource(repo);
            return [repo];
        }

        protected override bool EditAssetsRepo(LocalAssetsRepoViewModel repo)
        {
            var m = repo.Model;
            if (!DialogService.ShowEditLocalAssetsRepoDialog(m))
                return false;

            repo.Model = m;
            RefreshBatchExportState();
            return true;
        }

        public override void LoadAssetsRepos()
        {
            _assetsRepos.Clear();
            if (JsonHelper.Deserialize<LocalAssetsModel>(LocalAssetsFilePath, out var assets, true))
            {
                foreach (var m in assets.LocalAssetsRepos)
                {
                    _assetsRepos.Add(new(m));
                }
            }
            RefreshBatchExportState();
        }

        public override void SaveAssetsRepos()
        {
            var m = new LocalAssetsModel();

            foreach (var repo in _assetsRepos)
            {
                m.LocalAssetsRepos.Add(repo.Model);
            }

            JsonHelper.Serialize(m, LocalAssetsFilePath);
        }

        #region [AzureSail 新增] 全部导出

        // 源目录与导出文件夹存在 SpineSourceExportViewModel 里（全场唯一一份），这里只是资源库面板上的入口：
        // 右键某个资源库「设为全部导出文件夹」→ 选导出到的文件夹；设好后列表下方出现「全部导出」按钮。
        // 要换文件夹就重新设一次。

        private SpineSourceExportViewModel Exporter => _vmMain.SpineSourceExportViewModel;

        /// <summary>两个文件夹都设好了才显示「全部导出」</summary>
        public bool HasBatchExport => Exporter.HasBatchExport;

        /// <summary>按钮旁的说明：源 → 导出文件夹</summary>
        public string BatchExportText
        {
            get
            {
                var repo = _assetsRepos.FirstOrDefault(r => r.IsBatchExportSource);
                var src = repo?.Name ?? Exporter.SourceDirectory;
                var marked = Directory.Exists(Exporter.SourceDirectory) ? ExportMarks.CountUnder(Exporter.SourceDirectory) : 0;
                return $"{src}  →  {Exporter.OutputDirectory}（已设置导出 {marked} 个）";
            }
        }

        /// <summary>按钮提示：完整路径与导出规则</summary>
        public string BatchExportToolTip =>
            $"源目录：{Exporter.SourceDirectory}\n导出到：{Exporter.OutputDirectory}\n"
            + $"1. 源目录里每个 .spine 都在它旁边导出一份预览（Spine {Exporter.SpineVersion}，skel + atlas + png）"
            + (Exporter.SkipExported ? "，已有不比工程旧的预览则跳过" : "") + "；\n"
            + "2. 只有资源项右键「设置导出」了的，再拷一份到 导出文件夹/<骨骼名>/。\n"
            + "要换文件夹：在上面资源库上右键「设为全部导出文件夹...」重新设置。";

        /// <summary>右键资源库：把它设为全部导出的源目录，并选导出到的文件夹</summary>
        public RelayCommand<IList?> Cmd_SetBatchExportFolder => _cmd_SetBatchExportFolder ??= new(SetBatchExportFolder_Execute, CommandCanExecute.OnlyOne);
        private RelayCommand<IList?>? _cmd_SetBatchExportFolder;

        private void SetBatchExportFolder_Execute(IList? args)
        {
            if (!CommandCanExecute.OnlyOne(args)) return;
            if (args[0] is not LocalAssetsRepoViewModel repo) return;

            var initial = Directory.Exists(Exporter.OutputDirectory) ? Exporter.OutputDirectory : null;
            if (!DialogService.ShowOpenFolderDialog(out var folder, $"选择「{repo.Name}」全部导出到的文件夹", initial))
                return;

            var error = Exporter.SetBatchExport(repo.LocalDirectory, folder!);
            if (error is not null)
            {
                MessagePopupService.Error(error);
                return;
            }
            RefreshBatchExportState();
        }

        /// <summary>全部导出：所有 .spine 在旁边导出预览，设置了导出的再拷到导出文件夹（进度框里跑，可取消），见 <see cref="SpineSourceExportViewModel.RunExportAll"/></summary>
        public RelayCommand Cmd_BatchExportAll => _cmd_BatchExportAll ??= new(() =>
        {
            var error = Exporter.Validate();
            if (error is not null)
            {
                MessagePopupService.Error(error + "\n可在资源库上右键「设为全部导出文件夹...」重新设置");
                return;
            }
            Exporter.RunExportAll();

            // 新导出的预览 skel 要出现在列表里
            if (_assetsRepos.FirstOrDefault(r => r.IsBatchExportSource) is { } source)
                RefreshRepo(source);
            OnPropertyChanged(nameof(BatchExportText));
        });
        private RelayCommand? _cmd_BatchExportAll;

        private void Exporter_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(SpineSourceExportViewModel.SourceDirectory)
                or nameof(SpineSourceExportViewModel.OutputDirectory)
                or nameof(SpineSourceExportViewModel.HasBatchExport))
                RefreshBatchExportState();
        }

        /// <summary>按当前设置刷新各资源库的标记与按钮显示</summary>
        private void RefreshBatchExportState()
        {
            foreach (var repo in _assetsRepos)
                repo.IsBatchExportSource = IsBatchExportSource(repo);
            OnPropertyChanged(nameof(HasBatchExport));
            OnPropertyChanged(nameof(BatchExportText));
            OnPropertyChanged(nameof(BatchExportToolTip));
        }

        protected override void OnExportMarksChanged() => OnPropertyChanged(nameof(BatchExportText));

        private bool IsBatchExportSource(LocalAssetsRepoViewModel repo) =>
            !string.IsNullOrWhiteSpace(Exporter.SourceDirectory)
            && string.Equals(NormalizeDir(repo.LocalDirectory), NormalizeDir(Exporter.SourceDirectory), StringComparison.OrdinalIgnoreCase);

        private static string NormalizeDir(string dir)
        {
            try { return Path.GetFullPath(dir).TrimEnd('\\', '/'); }
            catch { return dir; }
        }

        #endregion
    }
}
