using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SpineViewer.Views
{
    /// <summary>
    /// [AzureSail 新增] 最简单的单行输入框（记事本重命名用）。纯代码搭，不配 XAML。
    /// </summary>
    public static class TextInputDialog
    {
        /// <summary>弹框让用户输入一行字；点取消 / 关窗返回 null</summary>
        public static string? Show(string title, string prompt, string initial)
        {
            Window window = new()
            {
                Title = title,
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.Height,
                Width = 360,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
            };

            TextBox input = new() { Text = initial, Margin = new Thickness(0, 6, 0, 10) };
            Button ok = new() { Content = "确定", Width = 72, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            Button cancel = new() { Content = "取消", Width = 72, IsCancel = true };
            ok.Click += (_, _) => window.DialogResult = true;

            StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            StackPanel root = new() { Margin = new Thickness(12) };
            root.Children.Add(new TextBlock { Text = prompt });
            root.Children.Add(input);
            root.Children.Add(buttons);
            window.Content = root;

            window.Loaded += (_, _) =>
            {
                input.Focus();
                input.SelectAll();
                Keyboard.Focus(input);
            };

            return window.ShowDialog() == true ? input.Text : null;
        }
    }
}
