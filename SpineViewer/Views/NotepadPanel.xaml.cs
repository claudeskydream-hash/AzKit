using SpineViewer.Utils;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SpineViewer.Views
{
    /// <summary>
    /// [AzureSail 新增] 左侧栏「记事本」页：文件夹树（逻辑见 NotepadViewModel / NotepadTree，编辑区见 NotepadEditorPanel）。
    /// 切走本页时自动保存、右侧跟着切页签，都在 MainWindow 的左侧栏切换处理里做。
    /// </summary>
    public partial class NotepadPanel : UserControl
    {
        public NotepadPanel()
        {
            InitializeComponent();
        }

        /// <summary>树默认右击不换选中项：先选中右击的那一项，菜单里的操作才作用于它</summary>
        private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 文字片段（Run 之类）不是 Visual，沿视觉树找不上去，直接不管
            if (e.OriginalSource is Visual v && v.GetParent<TreeViewItem>(true) is TreeViewItem item)
            {
                item.IsSelected = true;
                item.Focus();
            }
        }
    }
}
