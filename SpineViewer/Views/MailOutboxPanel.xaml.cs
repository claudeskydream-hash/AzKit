using System.Windows.Controls;

namespace SpineViewer.Views
{
    /// <summary>
    /// [AzureSail 新增] 右侧「邮件」页：待发箱与详情（逻辑见 MailViewModel；写信在左侧栏「邮件」页 MailPanel）
    /// </summary>
    public partial class MailOutboxPanel : UserControl
    {
        public MailOutboxPanel()
        {
            InitializeComponent();
        }
    }
}
