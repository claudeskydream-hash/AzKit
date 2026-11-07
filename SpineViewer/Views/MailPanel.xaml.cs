using SpineViewer.ViewModels;
using System.Windows.Controls;
using System.Windows.Input;

namespace SpineViewer.Views
{
    /// <summary>
    /// [AzureSail 新增] 左侧栏「邮件」页：写一封邮件（逻辑见 MailViewModel；待发箱在右侧「邮件」页 MailOutboxPanel）
    /// </summary>
    public partial class MailPanel : UserControl
    {
        public MailPanel()
        {
            InitializeComponent();
        }

        /// <summary>双击搜索结果 = 加进附件</summary>
        private void ItemList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is MailViewModel vm && vm.SelectedItem is not null)
                vm.Cmd_AddItem.Execute(null);
        }
    }
}
