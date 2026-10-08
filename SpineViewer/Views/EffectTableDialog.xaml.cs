using SpineViewer.Extensions;
using SpineViewer.Resources;
using SpineViewer.Services;
using SpineViewer.ViewModels;
using System.Windows;

namespace SpineViewer.Views
{
    /// <summary>
    /// [AzureSail 新增] 配置到特效表对话框
    /// </summary>
    public partial class EffectTableDialog : Window
    {
        public EffectTableDialog()
        {
            InitializeComponent();
            SourceInitialized += EffectTableDialog_SourceInitialized;
        }

        private void EffectTableDialog_SourceInitialized(object? sender, EventArgs e)
        {
            this.SetWindowTextColor(AppResource.Color_PrimaryText);
            this.SetWindowCaptionColor(AppResource.Color_Region);
        }

        private void ButtonOK_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not EffectTableViewModel vm) return;

            // 写失败（没选行、表被 Excel 占用等）不关窗口，改完可以再点
            var (ok, message) = vm.Apply(this);
            if (!ok)
            {
                MessagePopupService.Error(message);
                return;
            }
            MessagePopupService.Info(message);
            DialogResult = true;
        }

        private void ButtonCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
