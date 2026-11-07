using SpineViewer.Extensions;
using SpineViewer.Resources;
using System.Windows;

namespace SpineViewer.Views
{
    /// <summary>
    /// [AzureSail 新增] 开服设置窗口（非模态，由 DialogService.ShowServerOpen 打开，同时只开一个）
    /// </summary>
    public partial class ServerOpenDialog : Window
    {
        public ServerOpenDialog()
        {
            InitializeComponent();
            SourceInitialized += ServerOpenDialog_SourceInitialized;
        }

        private void ServerOpenDialog_SourceInitialized(object? sender, EventArgs e)
        {
            this.SetWindowTextColor(AppResource.Color_PrimaryText);
            this.SetWindowCaptionColor(AppResource.Color_Region);
        }
    }
}
