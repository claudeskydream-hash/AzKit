using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using SFMLRenderer;
using SpineViewer.Extensions;
using SpineViewer.Models;
using SpineViewer.Services;
using SpineViewer.Utils;
using SpineViewer.ViewModels.Assets;
using SpineViewer.ViewModels.Assets.Local;
using SpineViewer.ViewModels.Main;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;

namespace SpineViewer.ViewModels
{
    /// <summary>
    /// MainWindow 上下文对象
    /// </summary>
    public class MainWindowViewModel : ObservableObject
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public MainWindowViewModel(ISFMLRenderer sfmlRenderer, ISFMLRenderer wallpaperRenderer)
        {
            _sfmlRenderer = sfmlRenderer;
            _wallpaperRenderer = wallpaperRenderer;
            _spineObjectListViewModel = new(this);
            _assetsPreviewViewModel = new(this);
            _localAssetsViewModel = new(this);
            _sfmlRendererViewModel = new(this);
            _preferenceViewModel = new(this);
        }

        public string Title => $"{App.AppName} - {App.VersionTag}";

        public Visibility Visibility
        {
            get => _visibility;
            set
            {
                if (SetProperty(ref _visibility, value))
                {
                    OnPropertyChanged(nameof(IsVisible));
                }
            }
        }
        private Visibility _visibility = Visibility.Visible;

        public bool IsVisible => _visibility == Visibility.Visible;

        /// <summary>
        /// 指示是否通过托盘图标进行退出
        /// </summary>
        public bool IsShuttingDownFromTray
        {
            get => _isShuttingDownFromTray;
            private set => SetProperty(ref _isShuttingDownFromTray, value);
        }
        private bool _isShuttingDownFromTray;

        public bool CloseToTray
        {
            get => _closeToTray;
            set => SetProperty(ref _closeToTray, value);
        }
        private bool _closeToTray;

        public string? AutoRunWorkspaceConfigPath
        {
            get => _autoRunWorkspaceConfigPath;
            set => SetProperty(ref _autoRunWorkspaceConfigPath, value);
        }
        private string? _autoRunWorkspaceConfigPath;

        /// <summary>
        /// SFML 渲染对象
        /// </summary>
        public ISFMLRenderer SFMLRenderer => _sfmlRenderer;
        private readonly ISFMLRenderer _sfmlRenderer;

        public ISFMLRenderer WallpaperRenderer => _wallpaperRenderer;
        private readonly ISFMLRenderer _wallpaperRenderer;

        public TaskbarItemProgressState ProgressState { get => _progressState; set => SetProperty(ref _progressState, value); }
        private TaskbarItemProgressState _progressState = TaskbarItemProgressState.None;

        public float ProgressValue { get => _progressValue; set => SetProperty(ref _progressValue, value); }
        private float _progressValue = 0;

        /// <summary>
        /// 已加载的 Spine 对象
        /// </summary>
        public ObservableCollectionWithLock<SpineObjectModel> SpineObjects => _spineObjectModels;
        private readonly ObservableCollectionWithLock<SpineObjectModel> _spineObjectModels = [];

        /// <summary>
        /// 首选项 ViewModel
        /// </summary>
        public PreferenceViewModel PreferenceViewModel => _preferenceViewModel;
        private readonly PreferenceViewModel _preferenceViewModel;

        /// <summary>
        /// 模型列表 ViewModel
        /// </summary>
        public SpineObjectListViewModel SpineObjectListViewModel => _spineObjectListViewModel;
        private readonly SpineObjectListViewModel _spineObjectListViewModel;

        /// <summary>
        /// 模型属性页 ViewModel
        /// </summary>
        public SpineObjectTabViewModel SpineObjectTabViewModel => _spineObjectTabViewModel;
        private readonly SpineObjectTabViewModel _spineObjectTabViewModel = new();

        /// <summary>
        /// 预览图管理 ViewModel
        /// </summary>
        public AssetsPreviewViewModel AssetsPreviewViewModel => _assetsPreviewViewModel;
        private readonly AssetsPreviewViewModel _assetsPreviewViewModel;

        /// <summary>
        /// 本地资源 ViewModel
        /// </summary>
        public LocalAssetsViewModel LocalAssetsViewModel => _localAssetsViewModel;
        private readonly LocalAssetsViewModel _localAssetsViewModel;

        /// <summary>
        /// GitHub 在线资源 ViewModel
        /// </summary>
        /// <summary>
        /// SFML 渲染 ViewModel
        /// </summary>
        public SFMLRendererViewModel SFMLRendererViewModel => _sfmlRendererViewModel;
        private readonly SFMLRendererViewModel _sfmlRendererViewModel;

        /// <summary>
        /// [AzureSail 新增] 右侧画面标签页: 0 = 模型动画, 1 = 模型预览图
        /// </summary>
        public int ViewTabIndex { get => _viewTabIndex; set => SetProperty(ref _viewTabIndex, value); }
        private int _viewTabIndex;

        public RelayCommand Cmd_SwitchWallpaperView => _cmd_SwitchWallpaperView ??= new(() =>
        {
            _preferenceViewModel.WallpaperView = !_preferenceViewModel.WallpaperView;
            _preferenceViewModel.SavePreference();
        });
        private RelayCommand? _cmd_SwitchWallpaperView;

        public RelayCommand Cmd_ExitFromTray => _cmd_ExitFromTray ??= new(() =>
        {
            IsShuttingDownFromTray = true;
            Application.Current.Shutdown();
        });
        private RelayCommand? _cmd_ExitFromTray;

        /// <summary>
        /// 打开工作区
        /// </summary>
        public RelayCommand Cmd_OpenWorkspace => _cmd_OpenWorkspace ??= new(OpenWorkspace_Execute);
        private RelayCommand? _cmd_OpenWorkspace;

        private void OpenWorkspace_Execute()
        {
            if (!DialogService.ShowOpenJsonDialog(out var fileName)) return;
            if (JsonHelper.Deserialize<WorkspaceModel>(fileName, out var obj))
            {
                Workspace = obj;
            }
        }

        /// <summary>
        /// 保存工作区
        /// </summary>
        public RelayCommand Cmd_SaveWorkspace => _cmd_SaveWorkspace ??= new(SaveWorkspace_Execute);
        private RelayCommand? _cmd_SaveWorkspace;

        private void SaveWorkspace_Execute()
        {
            string fileName = "workspace.jcfg";
            if (!DialogService.ShowSaveJsonDialog(ref fileName)) return;
            JsonHelper.Serialize(Workspace, fileName);
        }

        /// <summary>
        /// 打开使用文档（本仓库 README）
        /// </summary>
        public RelayCommand Cmd_GotoWiki => _cmd_GotoWiki ??= new(() => Process.Start(new ProcessStartInfo($"https://github.com/{App.GithubOwner}/{App.GithubRepo}#readme") { UseShellExecute = true }));
        private RelayCommand? _cmd_GotoWiki;

        /// <summary>
        /// 显示关于对话框
        /// </summary>
        public RelayCommand Cmd_ShowAboutDialog => _cmd_ShowAboutDialog ??= new(() => { DialogService.ShowAboutDialog(); });
        private RelayCommand? _cmd_ShowAboutDialog;

        /// <summary>
        /// [AzureSail 新增] AZ → 导表：打开桌面上的「AzureSail导表」快捷方式（指向 DataTables\gen.bat）
        /// </summary>
        public RelayCommand Cmd_RunAzureSailGenTable => _cmd_RunAzureSailGenTable ??= new(() =>
        {
            var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AzureSail导表.lnk");
            if (!File.Exists(lnk))
            {
                MessagePopupService.Error($"找不到导表快捷方式：{lnk}");
                return;
            }
            Process.Start(new ProcessStartInfo(lnk) { UseShellExecute = true });
        });
        private RelayCommand? _cmd_RunAzureSailGenTable;

        /// <summary>
        /// [AzureSail 新增] 自己结尾带 pause 的 bat，直接运行；其余用 cmd /k 跑，跑完窗口留着看结果
        /// </summary>
        private static readonly HashSet<string> _batSelfPause = new(StringComparer.OrdinalIgnoreCase)
        {
            "生成地形MIX.bat", "清理编辑器缓存.bat",
        };

        /// <summary>
        /// [AzureSail 新增] AZ → 运行工程根目录下的 bat（参数 = 相对工程根的文件名），工程根取母骨骼导出记下的那个
        /// </summary>
        public RelayCommand<string> Cmd_RunAzureSailBat => _cmd_RunAzureSailBat ??= new(bat =>
        {
            if (string.IsNullOrWhiteSpace(bat)) return;
            var root = HeroExportViewModel.ProjectRoot;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                MessagePopupService.Error("还没有记下 AzureSail 工程根，请先在「母骨骼导出」页签里设置工程目录");
                return;
            }
            var path = Path.Combine(root, bat);
            if (!File.Exists(path))
            {
                MessagePopupService.Error($"找不到脚本：{path}");
                return;
            }
            var psi = _batSelfPause.Contains(bat)
                ? new ProcessStartInfo(path) { UseShellExecute = true }
                : new ProcessStartInfo("cmd.exe", $"/k \"\"{path}\"\"") { UseShellExecute = true };
            psi.WorkingDirectory = root;
            Process.Start(psi);
        });
        private RelayCommand<string>? _cmd_RunAzureSailBat;

        /// <summary>
        /// [AzureSail 新增] 批量导出 Spine 源文件（设置在对话框关闭后保留，下次打开沿用）
        /// </summary>
        public SpineSourceExportViewModel SpineSourceExportViewModel => _spineSourceExportViewModel ??= new(this);
        private SpineSourceExportViewModel? _spineSourceExportViewModel;

        public RelayCommand Cmd_ShowSpineSourceExportDialog => _cmd_ShowSpineSourceExportDialog ??= new(() =>
        {
            if (DialogService.ShowSpineSourceExportDialog(SpineSourceExportViewModel))
                SpineSourceExportViewModel.Run();
        });
        private RelayCommand? _cmd_ShowSpineSourceExportDialog;

        /// <summary>
        /// [AzureSail 新增] 母骨骼导出英雄（编辑 heroes.json 并调用 export_hero.py）
        /// </summary>
        public HeroExportViewModel HeroExportViewModel => _heroExportViewModel ??= new(this);
        private HeroExportViewModel? _heroExportViewModel;

        /// <summary>
        /// [AzureSail 新增] 左侧栏「云存档」页：查询、删除玩家的星火云存档
        /// </summary>
        public CloudSaveViewModel CloudSaveViewModel => _cloudSaveViewModel ??= new(this);
        private CloudSaveViewModel? _cloudSaveViewModel;

        public WorkspaceModel Workspace
        {
            get
            {
                return new()
                {
                    RendererConfig = _sfmlRendererViewModel.WorkspaceConfig,
                    LoadedSpineObjects = _spineObjectListViewModel.LoadedSpineObjects
                };
            }
            set
            {
                _sfmlRendererViewModel.WorkspaceConfig = value.RendererConfig;
                _spineObjectListViewModel.LoadedSpineObjects = value.LoadedSpineObjects;
            }
        }
    }
}