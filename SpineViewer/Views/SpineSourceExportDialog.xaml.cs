using SpineViewer.Extensions;
using SpineViewer.Resources;
using SpineViewer.Services;
using SpineViewer.ViewModels;
using System.Windows;

namespace SpineViewer.Views
{
    /// <summary>
    /// [AzureSail 新增] 批量导出 Spine 源文件对话框
    /// </summary>
    public partial class SpineSourceExportDialog : Window
    {
        public SpineSourceExportDialog()
        {
            InitializeComponent();
            SourceInitialized += SpineSourceExportDialog_SourceInitialized;
        }

        private void SpineSourceExportDialog_SourceInitialized(object? sender, EventArgs e)
        {
            this.SetWindowTextColor(AppResource.Color_PrimaryText);
            this.SetWindowCaptionColor(AppResource.Color_Region);
        }

        private void ButtonOK_Click(object sender, RoutedEventArgs e)
        {
            // 参数有问题就不关窗口，提示后让用户改
            var error = (DataContext as SpineSourceExportViewModel)?.Validate();
            if (error is not null)
            {
                MessagePopupService.Error(error);
                return;
            }
            DialogResult = true;
        }

        private void ButtonCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
