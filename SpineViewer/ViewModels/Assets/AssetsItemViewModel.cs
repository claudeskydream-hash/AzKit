using CommunityToolkit.Mvvm.ComponentModel;
using NLog;
using SpineViewer.Extensions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;

namespace SpineViewer.ViewModels.Assets
{
    /// <summary>
    /// 资源库模型 ViewModel
    /// </summary>
    public abstract class AssetsItemViewModel : ObservableObject, IExplorerOpenable
    {
        /// <summary>
        /// 缩略图文件名格式字符串, 需要一个参数
        /// </summary>
        private const string PreviewFileNameFormat = ".{0}.preview.webp";

        protected static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private readonly AssetsRepoViewModel _vmRepo;
        protected readonly string _relativePath;
        protected readonly string _fileName;

        public AssetsItemViewModel(AssetsRepoViewModel vmRepo, string relativePath)
        {
            _vmRepo = vmRepo;
            _relativePath = relativePath.Replace("/", "\\");
            _fileName = Path.GetFileName(_relativePath);
        }

        /// <summary>
        /// 相对资源库的相对路径
        /// </summary>
        public string RelativePath => _relativePath;

        /// <summary>
        /// 文件名
        /// </summary>
        public string FileName => _fileName;

        /// <summary>
        /// [AzureSail 新增] 所属资源库的本地根目录
        /// </summary>
        public string RepoDirectory => _vmRepo.LocalDirectory;

        /// <summary>
        /// [AzureSail 新增] 所在目录（相对资源库），文件在资源库根目录时为空串
        /// </summary>
        public string DirectoryPath => Path.GetDirectoryName(_relativePath) ?? "";

        /// <summary>
        /// [AzureSail 新增] 列表分组名: 相对路径的第一级目录, 根目录下的文件归入「(根目录)」
        /// </summary>
        public string GroupName
        {
            get
            {
                var idx = _relativePath.IndexOf('\\');
                return idx > 0 ? _relativePath[..idx] : "(根目录)";
            }
        }

        /// <summary>
        /// 本地存储完整路径
        /// </summary
        public string LocalFullPath => Path.Combine(_vmRepo.LocalDirectory, _relativePath);

        /// <summary>
        /// [AzureSail 新增] 是否已设置导出（资源库「全部导出」时才往导出文件夹生成一份），见 <see cref="Utils.ExportMarks"/>
        /// </summary>
        public bool IsExportMarked => Utils.ExportMarks.IsItemMarked(LocalFullPath);

        /// <summary>[AzureSail 新增] 导出标记改了之后通知界面刷新</summary>
        public void NotifyExportMarkChanged() => OnPropertyChanged(nameof(IsExportMarked));

        /// <summary>
        /// 文件所处本地目录
        /// </summary>
        public string LocalDirectory => Path.GetDirectoryName(LocalFullPath) ?? "";

        /// <summary>
        /// 预览图统一存放目录（程序缓存目录下），不写进资源目录
        /// </summary>
        public static readonly string PreviewCacheDirectory = Path.Combine(App.CacheDirectory, "previews");

        /// <summary>
        /// 预览图路径。
        /// [AzureSail 修改] 原版写在模型文件旁边（.xxx.skel.preview.webp），会污染游戏资源目录，
        /// 改为按完整路径哈希存进程序缓存目录。
        /// </summary>
        public string PreviewFilePath
        {
            get
            {
                var key = LocalFullPath.ToLowerInvariant();
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
                return Path.Combine(PreviewCacheDirectory, string.Format(PreviewFileNameFormat, hash).TrimStart('.'));
            }
        }

        /// <summary>
        /// 预览图
        /// </summary>
        public ImageSource? PreviewImage
        {
            get
            {
                try
                {
                    return WpfExtension.LoadWebpWithAlpha(PreviewFilePath);
                }
                catch (FileNotFoundException)
                {
                    return null;
                }
                catch (DirectoryNotFoundException) 
                {
                    return null;
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex.ToString());
                    _logger.Warn("Failed to load preview image for {0}, {1}", LocalFullPath, ex.Message);
                    return null;
                }
            }
        }

        #region IExplorerOpenable

        string IExplorerOpenable.OpenInExplorerDirectory => LocalDirectory;

        #endregion
    }
}
