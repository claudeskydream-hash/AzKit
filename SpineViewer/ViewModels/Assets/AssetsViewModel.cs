using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using Spine;
using Spine.Exporters;
using SpineViewer.Extensions;
using SpineViewer.Resources;
using SpineViewer.Services;
using SpineViewer.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;

namespace SpineViewer.ViewModels.Assets
{
    public abstract class AssetsViewModel : ObservableObject
    {
        /// <summary>
        /// 资源相关信息的缓存目录
        /// </summary>
        public static readonly string AssetsCacheDirectory = Path.Combine(App.CacheDirectory, "assets");

        private static readonly string DefaultAssetsDownloadDirectory = Path.Combine(App.ProcessDirectory, "assets");

        /// <summary>
        /// 资源下载目录
        /// </summary>
        public static string AssetsDownloadDirectory 
        { 
            get => string.IsNullOrWhiteSpace(_assetsDownloadDirectory) ? DefaultAssetsDownloadDirectory : _assetsDownloadDirectory; 
            set => _assetsDownloadDirectory = value; 
        }
        private static string? _assetsDownloadDirectory;

        protected static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        protected readonly MainWindowViewModel _vmMain;

        public AssetsViewModel(MainWindowViewModel vmMain)
        {
            _vmMain = vmMain;
        }

        #region 资源库列表管理

        /// <summary>
        /// 资源库列表
        /// </summary>
        public abstract IReadOnlyList<AssetsRepoViewModel> AssetsRepos { get; }

        /// <summary>
        /// 资源文件夹选中项发生变化命令
        /// </summary>
        public abstract RelayCommand<IList?> Cmd_AssetsRepoSelectionChanged { get; }

        /// <summary>
        /// 添加资源库
        /// </summary>
        public abstract RelayCommand Cmd_AddAssetsRepos { get; }

        /// <summary>
        /// 移除资源库
        /// </summary>
        public abstract RelayCommand<IList?> Cmd_RemoveAssetsRepos { get; }

        /// <summary>
        /// 资源库上移一位
        /// </summary>
        public abstract RelayCommand<IList?> Cmd_MoveUpAssetsRepo { get; }

        /// <summary>
        /// 资源库下移一位
        /// </summary>
        public abstract RelayCommand<IList?> Cmd_MoveDownAssetsRepo { get; }

        /// <summary>
        /// 在资源管理器中打开资源
        /// </summary>
        public abstract RelayCommand<IList?> Cmd_OpenAssetsInExplorer { get; }

        /// <summary>
        /// 编辑资源库信息
        /// </summary>
        public abstract RelayCommand<IList?> Cmd_EditAssetsRepo { get; }

        /// <summary>
        /// 保存资源库列表
        /// </summary>
        public abstract void SaveAssetsRepos();

        /// <summary>
        /// 加载资源库列表
        /// </summary>
        public abstract void LoadAssetsRepos();

        #endregion

        #region 资源库模型列表管理

        /// <summary>
        /// 当前被显示的模型文件列表
        /// </summary>
        public abstract IReadOnlyList<AssetsItemViewModel> ShownItems { get; }

        /// <summary>
        /// 模型列表筛选字符串
        /// </summary>
        public abstract string? FilterString { get; set; }

        /// <summary>
        /// 资源文件选中项发生变化命令
        /// </summary>
        public abstract RelayCommand<IList?> Cmd_AssetsItemSelectionChanged { get; }

        /// <summary>
        /// 强制刷新列表项命令
        /// </summary>
        public abstract RelayCommand<IList?> Cmd_RefreshRepoItems { get; }

        /// <summary>
        /// 导入选中的模型文件或者资源库
        /// </summary>
        public abstract RelayCommand<IList?> Cmd_ImportSelectedAssets { get; }

        #endregion

        #region 预览图管理

        /// <summary>
        /// 为选中的资源库/文件项生成预览图
        /// </summary>
        public abstract RelayCommand<IList?> Cmd_GeneratePreviews { get; }

        /// <summary>
        /// 为选中的目录/文件项删除预览图
        /// </summary>
        public abstract RelayCommand<IList?> Cmd_DeletePreviews { get; }

        #endregion
    }

    public abstract class AssetsViewModel<TRepo, TItem> : AssetsViewModel
        where TRepo : AssetsRepoViewModel<TItem>
        where TItem : AssetsItemViewModel
    {
        /// <summary>
        /// 辅助函数, 获取 <see cref="TItem"/> 对象列表
        /// </summary>
        protected static List<TItem> GetItems(IList args)
        {
            List<TItem> items = [];
            foreach (var it in args!)
            {
                switch (it)
                {
                    case TRepo repo:
                        items.AddRange(repo.Items);
                        break;
                    case TItem item:
                        items.Add(item);
                        break;
                    default:
                        _logger.Warn("Invalid type {0}, skip it", it.GetType().Name);
                        break;
                }
            }
            return items;
        }

        public AssetsViewModel(MainWindowViewModel vmMain) : base(vmMain) { }

        #region 资源库列表管理

        /// <summary>
        /// 当前选中的资源库
        /// </summary>
        private TRepo? _selectedAssetsRepo;

        public override IReadOnlyList<TRepo> AssetsRepos => _assetsRepos;
        protected readonly ObservableCollection<TRepo> _assetsRepos = [];

        public override RelayCommand<IList?> Cmd_AssetsRepoSelectionChanged => _cmd_AssetsRepoSelectionChanged ??= new(args =>
        {
            // 选中单个目录时显示该目录下所有文件项
            if (CommandCanExecute.OnlyOne(args))
            {
                _selectedAssetsRepo = (TRepo)args[0]!;
            }
            else
            {
                _selectedAssetsRepo = null;
            }
            _ = UpdateShownItemsAsync();
        });
        private RelayCommand<IList?>? _cmd_AssetsRepoSelectionChanged;

        public override RelayCommand Cmd_AddAssetsRepos => _cmd_AddAssetsRepos ??= new(AddAssetsRepos_Execute);
        private RelayCommand? _cmd_AddAssetsRepos;

        private void AddAssetsRepos_Execute()
        {
            var repos = AddAssetsRepos();
            var duplicated = 0;
            foreach (var r in repos)
            {
                if (_assetsRepos.Contains(r))
                {
                    _logger.Info("Ignore existed repo: {0}", r);
                    duplicated++;
                    continue;
                }
                _assetsRepos.Add(r);
            }
            SaveAssetsRepos();

            if (duplicated > 0)
            {
                _logger.Info("{0} new repos added, {1} existed repos ignored", repos.Count - duplicated, duplicated);
            }
        }

        public override RelayCommand<IList?> Cmd_RemoveAssetsRepos => _cmd_RemoveAssetsRepos ??= new(RemoveAssetsRepos_Execute, CommandCanExecute.AtLeastOne);
        private RelayCommand<IList?>? _cmd_RemoveAssetsRepos;

        private void RemoveAssetsRepos_Execute(IList? args)
        {
            if (!CommandCanExecute.AtLeastOne(args)) return;

            if (args.Count > 1)
            {
                if (!MessagePopupService.OKCancel(string.Format(AppResource.Str_RemoveItemsQuest, args.Count)))
                    return;
            }

            // NOTE: 这里必须要浅拷贝一次, 不能直接对会被修改的绑定数据 args 进行 foreach 遍历
            foreach (var repo in args.Cast<TRepo>().ToArray())
            {
                _assetsRepos.Remove(repo);
            }

            SaveAssetsRepos();
        }

        public override RelayCommand<IList?> Cmd_MoveUpAssetsRepo => _cmd_MoveUpAssetsRepo ??= new(MoveUpAssetsRepo_Execute, CommandCanExecute.OnlyOne);
        private RelayCommand<IList?>? _cmd_MoveUpAssetsRepo;

        private void MoveUpAssetsRepo_Execute(IList? args)
        {
            if (!CommandCanExecute.OnlyOne(args)) return;

            var repo = (TRepo)args[0]!;
            var idx = _assetsRepos.IndexOf(repo);
            if (idx <= 0) return;
            _assetsRepos.Move(idx, idx - 1);

            SaveAssetsRepos();
        }

        public override RelayCommand<IList?> Cmd_MoveDownAssetsRepo => _cmd_MoveDownAssetsRepo ??= new(MoveDownAssetsRepo_Execute, CommandCanExecute.OnlyOne);
        private RelayCommand<IList?>? _cmd_MoveDownAssetsRepo;

        private void MoveDownAssetsRepo_Execute(IList? args)
        {
            if (!CommandCanExecute.OnlyOne(args)) return;

            var repo = (TRepo)args[0]!;
            var idx = _assetsRepos.IndexOf(repo);
            if (idx < 0 || idx >= _assetsRepos.Count - 1) return;
            _assetsRepos.Move(idx, idx + 1);

            SaveAssetsRepos();
        }

        public override RelayCommand<IList?> Cmd_OpenAssetsInExplorer => _cmd_OpenAssetsInExplorer ??= new(OpenAssetsInExplorer_Execute, CommandCanExecute.OnlyOne);
        private RelayCommand<IList?>? _cmd_OpenAssetsInExplorer;

        private void OpenAssetsInExplorer_Execute(IList? args)
        {
            if (!CommandCanExecute.OnlyOne(args)) return;

            var obj = (IExplorerOpenable)args[0]!;
            obj.OpenDirectoryInExplorer();
        }

        public override RelayCommand<IList?> Cmd_EditAssetsRepo => _cmd_EditAssetsRepo ??= new(EditAssetsRepo_Execute, CommandCanExecute.OnlyOne);
        private RelayCommand<IList?>? _cmd_EditAssetsRepo;

        private void EditAssetsRepo_Execute(IList? args)
        {
            if (!CommandCanExecute.OnlyOne(args)) return;

            var repo = (TRepo)args[0]!;

            if (!EditAssetsRepo(repo)) return;

            SaveAssetsRepos();
        }

        /// <summary>
        /// 添加资源库
        /// </summary>
        protected abstract IReadOnlyList<TRepo> AddAssetsRepos();

        /// <summary>
        /// 编辑资源库信息
        /// </summary>
        /// <returns>取消或失败返回 false</returns>
        protected abstract bool EditAssetsRepo(TRepo repo);

        #endregion

        #region 资源库模型列表管理

        public override IReadOnlyList<TItem> ShownItems => _shownItems;
        private List<TItem> _shownItems = [];

        public override string? FilterString
        {
            get => string.IsNullOrWhiteSpace(_filterString) ? null : _filterString;
            set
            {
                if (!SetProperty(ref _filterString, value)) return;
                _ = UpdateShownItemsAsync();
            }
        }
        private string? _filterString;

        public override RelayCommand<IList?> Cmd_AssetsItemSelectionChanged => _cmd_AssetsItemSelectionChanged ??= new(args =>
        {
            // 选中单个目录时显示该目录下所有文件项
            if (!CommandCanExecute.OnlyOne(args))
            {
                _vmMain.AssetsPreviewViewModel.PreviewImage = null;
                return;
            }

            var item = (TItem)args[0]!;
            _vmMain.AssetsPreviewViewModel.PreviewImage = item.PreviewImage;

            // [AzureSail 新增] 单击即在右侧「模型动画」里实时播放, 不必先导入或生成预览图
            if (File.Exists(item.LocalFullPath) && _vmMain.SpineObjectListViewModel.ShowPreviewObject(item.LocalFullPath))
                _vmMain.ViewTabIndex = 0;
        });
        private RelayCommand<IList?>? _cmd_AssetsItemSelectionChanged;

        public override RelayCommand<IList?> Cmd_RefreshRepoItems => _cmd_RefreshRepoItems ??= new(
            args =>
            {
                if (!CommandCanExecute.OnlyOne(args)) return;
                _ = UpdateShownItemsAsync(true);
            },
            CommandCanExecute.OnlyOne
        );
        private RelayCommand<IList?>? _cmd_RefreshRepoItems;

        /// <summary>
        /// [AzureSail 新增] 重新扫描某个资源库（全部导出生成了新的预览 skel 之后用）：正在显示的就连列表一起刷新
        /// </summary>
        protected void RefreshRepo(TRepo repo)
        {
            if (ReferenceEquals(repo, _selectedAssetsRepo))
                _ = UpdateShownItemsAsync(true);
            else
                _ = repo.RefreshItemsAsync();
        }

        public override RelayCommand<IList?> Cmd_ImportSelectedAssets => _cmd_ImportSelectedAssets ??= new(ImportSelectedAssets_Execute, CommandCanExecute.AtLeastOne);
        private RelayCommand<IList?>? _cmd_ImportSelectedAssets;

        private void ImportSelectedAssets_Execute(IList? args)
        {
            if (!CommandCanExecute.AtLeastOne(args))
                return;

            var items = GetItems(args);

            _vmMain.SpineObjectListViewModel.AddSpineObjectFromFileList(items.Select(m => m.LocalFullPath));
        }

        /// <summary>
        /// [AzureSail 新增] 用 Spine 编辑器把选中项所在目录里的 .spine 工程重新导出
        /// （版本、Spine 程序、输出目录沿用「文件 → 批量导出 Spine 源文件」里的设置）
        /// </summary>
        public RelayCommand<IList?> Cmd_ExportSpineSource => _cmd_ExportSpineSource ??= new(ExportSpineSource_Execute, CommandCanExecute.AtLeastOne);
        private RelayCommand<IList?>? _cmd_ExportSpineSource;

        private void ExportSpineSource_Execute(IList? args)
        {
            if (!CommandCanExecute.AtLeastOne(args))
                return;

            var items = GetItems(args);
            var spineFiles = items
                .Select(m => m.LocalDirectory)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists)
                .SelectMany(dir => Directory.GetFiles(dir, "*.spine"))
                .ToList();
            if (spineFiles.Count <= 0)
            {
                MessagePopupService.Warn("选中项所在的目录里没有 .spine 源文件，无法重新导出");
                return;
            }

            var vm = _vmMain.SpineSourceExportViewModel;
            if (string.IsNullOrWhiteSpace(vm.OutputDirectory))
            {
                MessagePopupService.Info("第一次使用，请选择导出目录（放在游戏 res 目录以外）");
                if (!DialogService.ShowOpenFolderDialog(out var folder))
                    return;
                vm.OutputDirectory = folder!;
            }
            var error = vm.ValidateForSelected();
            if (error is not null)
            {
                MessagePopupService.Error(error + "\n可在「文件 → 批量导出 Spine 源文件...」里修改");
                return;
            }

            vm.ExportProjects(spineFiles, items[0].RepoDirectory);
        }

        /// <summary>
        /// [AzureSail 新增] 把选中的 Spine 特效配置进 AzureSail 的特效表（覆盖一行或新增一行）
        /// </summary>
        public RelayCommand<IList?> Cmd_ConfigEffectTable => _cmd_ConfigEffectTable ??= new(ConfigEffectTable_Execute, CommandCanExecute.OnlyOne);
        private RelayCommand<IList?>? _cmd_ConfigEffectTable;

        private void ConfigEffectTable_Execute(IList? args)
        {
            if (!CommandCanExecute.OnlyOne(args))
                return;

            var item = GetItems(args)[0];
            var vm = SpineViewer.ViewModels.EffectTableViewModel.Create(item.LocalFullPath,
                _vmMain.HeroExportViewModel.ProjectRoot, _vmMain.SpineSourceExportViewModel, out var error);
            if (vm is null)
            {
                MessagePopupService.Warn(error ?? "无法配置到特效表");
                return;
            }
            DialogService.ShowEffectTableDialog(vm);
        }

        /// <summary>
        /// [AzureSail 新增] 设置导出：资源库「全部导出」时，标了的才往导出文件夹生成一份（标记记在 .spine 工程上）
        /// </summary>
        public RelayCommand<IList?> Cmd_MarkExport => _cmd_MarkExport ??= new(args => SetExportMark(args, true), CommandCanExecute.AtLeastOne);
        private RelayCommand<IList?>? _cmd_MarkExport;

        /// <summary>[AzureSail 新增] 取消导出</summary>
        public RelayCommand<IList?> Cmd_UnmarkExport => _cmd_UnmarkExport ??= new(args => SetExportMark(args, false), CommandCanExecute.AtLeastOne);
        private RelayCommand<IList?>? _cmd_UnmarkExport;

        private void SetExportMark(IList? args, bool marked)
        {
            if (!CommandCanExecute.AtLeastOne(args)) return;

            var items = GetItems(args);
            var (changed, noProject) = Utils.ExportMarks.SetItems(items.Select(it => it.LocalFullPath), marked);
            // 同一个工程可能对应列表里的多项（比如同目录的几个 skel），整列刷一遍最省事
            foreach (var it in ShownItems) it.NotifyExportMarkChanged();
            OnExportMarksChanged();

            if (noProject > 0)
                MessagePopupService.Warn($"{noProject} 项旁边没有 .spine 工程，没法{(marked ? "设置" : "取消")}导出（全部导出只认 .spine）");
            _logger.Info("{0}导出 {1} 项", marked ? "设置" : "取消", changed);
        }

        /// <summary>[AzureSail 新增] 导出标记变了（子类据此刷新「全部导出」的说明）</summary>
        protected virtual void OnExportMarksChanged() { }

        /// <summary>
        /// [AzureSail 新增] 把选中的 Spine 配置成英雄表里的怪物（有指向它的怪物行就覆盖，没有就新增）
        /// </summary>
        public RelayCommand<IList?> Cmd_ConfigMonsterTable => _cmd_ConfigMonsterTable ??= new(ConfigMonsterTable_Execute, CommandCanExecute.OnlyOne);
        private RelayCommand<IList?>? _cmd_ConfigMonsterTable;

        private void ConfigMonsterTable_Execute(IList? args)
        {
            if (!CommandCanExecute.OnlyOne(args))
                return;

            var item = GetItems(args)[0];
            var vm = SpineViewer.ViewModels.MonsterTableViewModel.Create(item.LocalFullPath,
                _vmMain.HeroExportViewModel.ProjectRoot, _vmMain.SpineSourceExportViewModel, out var error);
            if (vm is null)
            {
                MessagePopupService.Warn(error ?? "无法配置到怪物");
                return;
            }
            DialogService.ShowMonsterTableDialog(vm);
        }

        /// <summary>
        /// <see cref="UpdateShownItemsAsync(bool)"/> 异步任务计数器, 用于区分执行先后顺序
        /// </summary>
        private long _updateShownItemsAsyncCounter = 0;

        /// <summary>
        /// 更新 <see cref="ShownItems"/>
        /// </summary>
        private async Task UpdateShownItemsAsync(bool refreshRepoItems = false)
        {
            // 先清空显示
            SetProperty(ref _shownItems, [], nameof(ShownItems));

            List<TItem> shownItems = [];
            var repo = _selectedAssetsRepo;
            var filter = FilterString;

            // 保存进入时的计数器
            var counter1 = Interlocked.Increment(ref _updateShownItemsAsyncCounter);

            if (repo is not null)
            {
                if (!repo.IsItemsLoaded || refreshRepoItems)
                {
                    await repo.RefreshItemsAsync();
                }

                if (string.IsNullOrWhiteSpace(filter))
                {
                    shownItems.AddRange(repo.Items);
                }
                else
                {
                    // [AzureSail 修改] 按相对路径筛选, 输入目录名(如 Effect)也能筛出整个目录
                    shownItems.AddRange(repo.Items.Where(it => it.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase)));
                }
            }

            var counter2 = Interlocked.Read(ref _updateShownItemsAsyncCounter);

            // 如果此次运行是最新的, 则按这个结果更新
            if (counter1 >= counter2)
            {
                SetProperty(ref _shownItems, shownItems, nameof(ShownItems));
            }
        }

        #endregion

        #region 预览图管理

        public override RelayCommand<IList?> Cmd_GeneratePreviews => _cmd_GeneratePreviews ??= new(GeneratePreviews_Execute, CommandCanExecute.AtLeastOne);
        private RelayCommand<IList?>? _cmd_GeneratePreviews;

        private void GeneratePreviews_Execute(IList? args)
        {
            if (!CommandCanExecute.AtLeastOne(args))
                return;

            if (!DialogService.ShowGeneratePreviewsDialog(_vmMain.AssetsPreviewViewModel))
                return;

            var items = GetItems(args);
            _vmMain.AssetsPreviewViewModel.GeneratePreviews(items);
        }

        public override RelayCommand<IList?> Cmd_DeletePreviews => _cmd_DeletePreviews ??= new(DeletePreviews_Execute, CommandCanExecute.AtLeastOne);
        private RelayCommand<IList?>? _cmd_DeletePreviews;

        private void DeletePreviews_Execute(IList? args)
        {
            if (!CommandCanExecute.AtLeastOne(args))
                return;

            var items = GetItems(args);

            if (items.Count <= 0)
                return;

            if (!MessagePopupService.OKCancel(string.Format(AppResource.Str_DeleteItemsQuest, items.Count)))
                return;

            if (args.Count <= 10)
            {
                foreach (var it in items)
                {
                    try
                    {
                        File.Delete(it.PreviewFilePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex.ToString());
                        _logger.Error("Failed to delete preview: {0}, {1}", it.PreviewFilePath, ex.Message);
                    }
                }
            }
            else
            {
                ProgressService.RunAsync(
                    (pr, ct) => DeletePreviewsTask(items, pr, ct),
                    AppResource.Str_DeletePreviewsTitle
                );
            }
        }

        private void DeletePreviewsTask(List<TItem> items, IProgressReporter reporter, CancellationToken ct)
        {
            int totalCount = items.Count;
            int success = 0;
            int error = 0;

            _vmMain.ProgressState = TaskbarItemProgressState.Normal;
            _vmMain.ProgressValue = 0;

            reporter.Total = totalCount;
            reporter.Done = 0;
            reporter.ProgressText = $"[0/{totalCount}]";
            for (int i = 0; i < totalCount; i++)
            {
                if (ct.IsCancellationRequested) break;

                var it = items[i];
                reporter.ProgressText = $"[{i}/{totalCount}] {it.LocalFullPath}";

                try
                {
                    File.Delete(it.PreviewFilePath);
                    success++;
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex.ToString());
                    _logger.Error("Failed to delete preview: {0}, {1}", it.PreviewFilePath, ex.Message);
                    error++;
                }

                reporter.Done = i + 1;
                reporter.ProgressText = $"[{i + 1}/{totalCount}] {it}";
                _vmMain.ProgressValue = (i + 1f) / totalCount;
            }
            _vmMain.ProgressState = TaskbarItemProgressState.None;

            if (error > 0)
                _logger.Warn("Preview deletion {0} successfully, {1} failed", success, error);
            else
                _logger.Info("{0} previews deleted successfully", success);
        }

        #endregion
    }
}
