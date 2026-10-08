using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using SpineViewer.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SpineViewer.ViewModels
{
    public partial class AboutDialogViewModel : ObservableObject
    {
        public string ProgramTagName { get; } = App.VersionTag;

        public string ProjectUrl { get; } = $"https://github.com/{App.GithubOwner}/{App.GithubRepo}";

        /// <summary>
        /// 打开指定网址
        /// </summary>
        public RelayCommand<string?> Cmd_OpenUrl => _cmd_OpenUrl ??= new(url =>
        {
            if (string.IsNullOrEmpty(url))
                return;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        });
        private RelayCommand<string?>? _cmd_OpenUrl;
    }
}
