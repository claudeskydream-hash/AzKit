using SpineViewer.Extensions;
using SpineViewer.Resources;
using System.Windows;

namespace SpineViewer.Views
{
    /// <summary>
    /// [AzureSail 新增] 线上日志窗口（非模态，由 DialogService.ShowOnlineLog 打开，同时只开一个）
    /// </summary>
    public partial class OnlineLogDialog : Window
    {
        public OnlineLogDialog()
        {
            InitializeComponent();
            SourceInitialized += OnlineLogDialog_SourceInitialized;
        }

        private void OnlineLogDialog_SourceInitialized(object? sender, EventArgs e)
        {
            this.SetWindowTextColor(AppResource.Color_PrimaryText);
            this.SetWindowCaptionColor(AppResource.Color_Region);
        }
    }
}
