using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using SpineViewer.Models;
using SpineViewer.ViewModels.Assets;
using SpineViewer.ViewModels.Exporters;
using SpineViewer.Views;
using SpineViewer.Views.AssetsDialogs;
using SpineViewer.Views.ExporterDialogs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace SpineViewer.Services
{
    /// <summary>
    /// 用于弹出各种对话框的服务
    /// </summary>
    public static class DialogService
    {
        private static bool ShowDialog<TDialog>(object? dc = null) 
            where TDialog : Window, new() 
        {
            var dialog = new TDialog() { Owner = App.Current.MainWindow };
            if (dc is not null) dialog.DataContext = dc;
            return dialog.ShowDialog() ?? false;
        }

        public static bool ShowAboutDialog() => ShowDialog<AboutDialog>();

        // [AzureSail 新增] 批量导出 Spine 源文件
        public static bool ShowSpineSourceExportDialog(SpineViewer.ViewModels.SpineSourceExportViewModel vm) => ShowDialog<SpineSourceExportDialog>(vm);

        // [AzureSail 新增] 配置到特效表
        public static bool ShowEffectTableDialog(SpineViewer.ViewModels.EffectTableViewModel vm) => ShowDialog<EffectTableDialog>(vm);

        // [AzureSail 新增] 配置到怪物
        public static bool ShowMonsterTableDialog(SpineViewer.ViewModels.MonsterTableViewModel vm) => ShowDialog<MonsterTableDialog>(vm);

        // [AzureSail 新增] 云存档 · 历史记录：非模态（开着也能继续操作），同时只开一个，再点就把它提到前面
        private static CloudCallHistoryDialog? _cloudCallHistory;

        public static void ShowCloudCallHistory(SpineViewer.ViewModels.CloudSaveViewModel vm)
        {
            if (_cloudCallHistory is not null)
            {
                if (_cloudCallHistory.WindowState == WindowState.Minimized)
                    _cloudCallHistory.WindowState = WindowState.Normal;
                _cloudCallHistory.Activate();
                return;
            }

            _cloudCallHistory = new CloudCallHistoryDialog { Owner = App.Current.MainWindow, DataContext = vm };
            _cloudCallHistory.Closed += (_, _) => _cloudCallHistory = null;
            _cloudCallHistory.Show();
        }

        // [AzureSail 新增] 一键获取后台凭证：内置浏览器登录星火创作者中心，返回 token；取消返回 null
        public static string? ShowSparkLoginDialog()
        {
            var dialog = new SparkLoginDialog { Owner = App.Current.MainWindow };
            return dialog.ShowDialog() is true ? dialog.Token : null;
        }

        // [AzureSail 新增] 线上日志：非模态、同时只开一个（同上）
        private static OnlineLogDialog? _onlineLog;

        public static void ShowOnlineLog(SpineViewer.ViewModels.OnlineLogViewModel vm)
        {
            if (_onlineLog is not null)
            {
                if (_onlineLog.WindowState == WindowState.Minimized)
                    _onlineLog.WindowState = WindowState.Normal;
                _onlineLog.Activate();
                return;
            }

            _onlineLog = new OnlineLogDialog { Owner = App.Current.MainWindow, DataContext = vm };
            _onlineLog.Closed += (_, _) => _onlineLog = null;
            _onlineLog.Show();
        }

        public static bool ShowGeneratePreviewsDialog(AssetsPreviewViewModel vm) => ShowDialog<GeneratePreviewsDialog>(vm);

        public static bool ShowEditLocalAssetsRepoDialog(LocalAssetsRepoModel vm) => ShowDialog<EditLocalAssetsRepoDialog>(vm);

        public static bool ShowFrameExporterDialog(FrameExporterViewModel vm) => ShowDialog<FrameExporterDialog>(vm);

        public static bool ShowPsdExporterDialog(PsdExporterViewModel vm) => ShowDialog<PsdExporterDialog>(vm);

        public static bool ShowFrameSequenceExporterDialog(FrameSequenceExporterViewModel vm) => ShowDialog<FrameSequenceExporterDialog>(vm);

        public static bool ShowFFmpegVideoExporterDialog(FFmpegVideoExporterViewModel vm) => ShowDialog<FFmpegVideoExporterDialog>(vm);

        public static bool ShowCustomFFmpegExporterDialog(CustomFFmpegExporterViewModel vm) => ShowDialog<CustomFFmpegExporterDialog>(vm);

        public static bool ShowPreferenceDialog(PreferenceModel m) => ShowDialog<PreferenceDialog>(m);

        /// <summary>
        /// 获取用户选择的文件
        /// </summary>
        /// <returns>是否确认了选择</returns>
        public static bool ShowOpenFileDialog(out string? fileName, string title = null, string filter = "")
        {
            var dialog = new OpenFileDialog() { Title = title, Filter = filter };
            if (dialog.ShowDialog() is true)
            {
                fileName = dialog.FileName;
                return true;
            }
            fileName = null;
            return false;
        }

        /// <summary>
        /// 获取用户选择的文件夹
        /// </summary>
        /// <returns>是否确认了选择</returns>
        public static bool ShowOpenFolderDialog(out string? folderName, string? title = null, string? initialDirectory = null)
        {
            // [AzureSail 修改] 可带标题与初始目录（设置「全部导出」的导出文件夹时用）
            var dialog = new OpenFolderDialog() { Multiselect = false };
            if (!string.IsNullOrWhiteSpace(title)) dialog.Title = title;
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
            if (dialog.ShowDialog() is true)
            {
                folderName = dialog.FolderName;
                return true;
            }
            folderName = null;
            return false;
        }

        public static bool ShowOpenSFMLImageDialog(out string? fileName, string initialDirectory = "")
        {
            var dialog = new OpenFileDialog()
            {
                InitialDirectory = initialDirectory,
                Filter = "SFML Image|*.png;*.jpg;*.jpeg;*.bmp;*.tga|All|*.*"
            };
            if (dialog.ShowDialog() is true)
            {
                fileName = dialog.FileName;
                return true;
            }
            fileName = null;
            return false;
        }

        public static bool ShowOpenJsonDialog(out string? fileName, string initialDirectory = "")
        {
            var dialog = new OpenFileDialog()
            {
                InitialDirectory = initialDirectory,
                Filter = "Json|*.jcfg;*.json|All|*.*"
            };
            if (dialog.ShowDialog() is true)
            {
                fileName = dialog.FileName;
                return true;
            }
            fileName = null;
            return false;
        }

        public static bool ShowSaveJsonDialog(ref string? fileName, string initialDirectory = "")
        {
            var dialog = new SaveFileDialog()
            {
                FileName = fileName,
                InitialDirectory = initialDirectory,
                DefaultExt = ".jcfg",
                Filter = "Json|*.jcfg;*.json|All|*.*",
            };
            if (dialog.ShowDialog() is true)
            {
                fileName = dialog.FileName;
                return true;
            }
            fileName = null;
            return false;
        }

        // [AzureSail 新增] 线上日志另存为 .log
        public static bool ShowSaveLogDialog(ref string? fileName)
        {
            var dialog = new SaveFileDialog()
            {
                FileName = fileName,
                DefaultExt = ".log",
                Filter = "日志|*.log;*.txt|All|*.*",
            };
            if (dialog.ShowDialog() is true)
            {
                fileName = dialog.FileName;
                return true;
            }
            fileName = null;
            return false;
        }
    }
}
