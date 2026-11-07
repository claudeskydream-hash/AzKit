using ICSharpCode.AvalonEdit.Search;
using SpineViewer.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

            // Ctrl+F 查找：AvalonEdit 自带的查找条（Enter / F3 下一个，Shift+Enter / Shift+F3 上一个，Esc 关，所有匹配处高亮；
            // 选中一段文字再按 Ctrl+F 会直接拿它当关键词）。切文件时查找条留着，对新文件接着查
            SearchPanel search = SearchPanel.Install(_editor);
            search.Localization = new ChineseSearchLocalization();

            // 查找框里按 Enter 找下一个、Shift+Enter 找上一个（焦点留在查找框，可以一直按）。
            // 挂在查找条自己身上，正文里的回车照常换行
            search.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter)
                    return;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                    search.FindPrevious();
                else
                    search.FindNext();
                e.Handled = true;
            };
        }

        /// <summary>查找条上的文字换成中文</summary>
        private sealed class ChineseSearchLocalization : ICSharpCode.AvalonEdit.Search.Localization
        {
            public override string MatchCaseText => "区分大小写";
            public override string MatchWholeWordsText => "全字匹配";
            public override string UseRegexText => "正则表达式";
            public override string FindNextText => "下一个 (Enter / F3)";
            public override string FindPreviousText => "上一个 (Shift+Enter / Shift+F3)";
            public override string ErrorText => "错误：";
            public override string NoMatchesFoundText => "没找到";
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
