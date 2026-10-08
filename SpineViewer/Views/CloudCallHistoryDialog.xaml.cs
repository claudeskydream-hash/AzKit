using SpineViewer.Extensions;
using SpineViewer.Resources;
using System.Windows;

namespace SpineViewer.Views
{
    /// <summary>
    /// [AzureSail 新增] 云存档 · 历史记录弹窗（非模态，由 DialogService.ShowCloudCallHistory 打开，同时只开一个）
    /// </summary>
    public partial class CloudCallHistoryDialog : Window
    {
        public CloudCallHistoryDialog()
        {
            InitializeComponent();
            SourceInitialized += CloudCallHistoryDialog_SourceInitialized;
        }

        private void CloudCallHistoryDialog_SourceInitialized(object? sender, EventArgs e)
        {
            this.SetWindowTextColor(AppResource.Color_PrimaryText);
            this.SetWindowCaptionColor(AppResource.Color_Region);
        }

        private void ButtonClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
