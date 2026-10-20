using SpineViewer.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SpineViewer.Views
{
    /// <summary>
    /// [AzureSail 新增] 右侧「笔记」页签：编辑区（逻辑见 NotepadViewModel）
    /// </summary>
    public partial class NotepadEditorPanel : UserControl
    {
        public NotepadEditorPanel()
        {
            InitializeComponent();
            DataContextChanged += NotepadEditorPanel_DataContextChanged;
        }

        /// <summary>新建笔记后光标直接进编辑区（只跟新建走；在列表里点选别的不抢焦点，方向键还能接着翻）</summary>
        private void NotepadEditorPanel_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is NotepadViewModel oldVm)
                oldVm.NoteCreated -= FocusEditor;
            if (e.NewValue is NotepadViewModel newVm)
                newVm.NoteCreated += FocusEditor;
        }

        /// <summary>把光标放进编辑区（左栏切到记事本、新建笔记后调，打开就能直接打字）</summary>
        public void FocusEditor()
        {
            // 页签刚切过来时编辑器还没排版完，排到下一帧再给焦点
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (_editor.IsVisible)
                    _editor.TextArea.Focus();
            });
        }
    }
}
