using SpineViewer.Extensions;
using SpineViewer.Resources;
using SpineViewer.Services;
using SpineViewer.ViewModels;
using System.Windows;

namespace SpineViewer.Views
{
    /// <summary>
    /// [AzureSail 新增] 配置到怪物对话框
    /// </summary>
    public partial class MonsterTableDialog : Window
    {
        public MonsterTableDialog()
        {
            InitializeComponent();
            SourceInitialized += MonsterTableDialog_SourceInitialized;
        }

        private void MonsterTableDialog_SourceInitialized(object? sender, EventArgs e)
        {
            this.SetWindowTextColor(AppResource.Color_PrimaryText);
            this.SetWindowCaptionColor(AppResource.Color_Region);
        }

        private void ButtonOK_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MonsterTableViewModel vm) return;

            // 缺标准动画不硬拦（比如没技能的怪不需要 spell），但要明确确认
            if (vm.MissingAnims.Count > 0
                && !MessagePopupService.OKCancel("这套 Spine 缺少动画：" + string.Join("、", vm.MissingAnims)
                    + "\n游戏里播到缺的动画时角色会不动（缺 attack 还打不出伤害）。\n\n仍要写入表格吗？"))
                return;

            // 写失败（没填名字、表被 Excel 占用等）不关窗口，改完可以再点
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
