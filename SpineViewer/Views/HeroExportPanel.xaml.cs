using SpineViewer.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace SpineViewer.Views
{
    /// <summary>
    /// [AzureSail 新增] 左侧栏「母骨骼导出」页（原「文件 → 母骨骼导出英雄...」对话框）
    /// </summary>
    public partial class HeroExportPanel : UserControl
    {
        public HeroExportPanel()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 每次切到这一页都重读 heroes.json 并扫描英雄目录：窗口外（命令行、手改）改过的配置不会被旧数据覆盖，
        /// 美术新放进去的英雄文件夹也会出现在列表里。正在跑脚本时不重读。
        /// </summary>
        private void HeroExportPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is true && DataContext is HeroExportViewModel vm && vm.IsIdle)
                vm.Reload();
        }

        /// <summary>日志始终滚到最后一行</summary>
        private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            (sender as TextBox)?.ScrollToEnd();
        }
    }
}
