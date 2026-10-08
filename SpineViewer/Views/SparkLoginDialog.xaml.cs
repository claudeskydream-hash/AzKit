using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NLog;
using SpineViewer.Extensions;
using SpineViewer.Resources;
using SpineViewer.Utils;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace SpineViewer.Views
{
    /// <summary>
    /// [AzureSail 新增] 「一键获取」后台凭证：用内置浏览器（WebView2）打开星火创作者中心，
    /// 每秒查一次发往后台接口的 Cookie，拿到<b>没过期</b>的 <c>token</c> 就把它交出来并关窗。
    ///
    /// <b>登录状态存在 AzKit 自己的浏览器数据目录</b>（<see cref="UserDataFolder"/>），不碰本机 Chrome / Edge：
    /// 第一次要在窗口里用 TapTap 登录；之后 token 过期（24 小时）再点一次，TapTap 登录还在就会自动登进去，
    /// 拿到新 token 即关窗。浏览器里残留的过期 token 不算，接着等新的。
    ///
    /// 不去解密 Chrome 的 Cookie 库：Chrome 127 起 Cookie 改成绑定浏览器自身的加密（App-Bound），外部程序解不开。
    /// </summary>
    public partial class SparkLoginDialog : Window
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        /// <summary>星火创作者中心（后台前端）</summary>
        public const string LoginUrl = "https://developer.spark.xd.com/";

        /// <summary>内置浏览器的数据目录（登录状态在这里）</summary>
        public static readonly string UserDataFolder = Path.Combine(App.DataDirectory, "webview2");

        /// <summary>拿到的 token；取消或没拿到为 null</summary>
        public string? Token { get; private set; }

        private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };

        /// <summary>正在查 / 已经拿到（计时器每秒一跳，上一次的异步查询可能还没回来）</summary>
        private bool _busy;

        public SparkLoginDialog()
        {
            InitializeComponent();
            SourceInitialized += SparkLoginDialog_SourceInitialized;
            Loaded += async (_, _) => await StartAsync();
            Closed += (_, _) => _poll.Stop();
            _poll.Tick += async (_, _) => await CheckTokenAsync();
        }

        private void SparkLoginDialog_SourceInitialized(object? sender, EventArgs e)
        {
            this.SetWindowTextColor(AppResource.Color_PrimaryText);
            this.SetWindowCaptionColor(AppResource.Color_Region);
        }

        private async Task StartAsync()
        {
            try
            {
                Directory.CreateDirectory(UserDataFolder);
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, UserDataFolder);
                await web.EnsureCoreWebView2Async(env);
            }
            catch (Exception e) when (e is WebView2RuntimeNotFoundException or COMException or InvalidOperationException)
            {
                _logger.Warn("[SparkLogin] 内置浏览器启动失败：{0}", e.Message);
                statusText.Text = "✗ 内置浏览器启动失败（本机缺 WebView2 运行时？）：" + e.Message;
                return;
            }

            web.CoreWebView2.Navigate(LoginUrl);
            statusText.Text = "在上面登录星火创作者中心（TapTap 登录），登录成功后会自动取到凭证并关闭本窗口。";
            _poll.Start();
        }

        /// <summary>每秒一跳：防重入后去查 token</summary>
        private async Task CheckTokenAsync()
        {
            if (web.CoreWebView2 is null || _busy) return;
            _busy = true;
            try
            {
                await FindTokenAsync();
            }
            finally
            {
                if (Token is null) _busy = false;
            }
        }

        /// <summary>查发往后台接口（和创作者中心本身）的 Cookie 里有没有没过期的 token</summary>
        private async Task FindTokenAsync()
        {
            foreach (string url in new[] { CloudAdminApi.ApiBase, LoginUrl })
            {
                List<CoreWebView2Cookie> cookies = await web.CoreWebView2.CookieManager.GetCookiesAsync(url);
                CoreWebView2Cookie? cookie = cookies.FirstOrDefault(c => c.Name.Equals("token", StringComparison.OrdinalIgnoreCase));
                if (cookie is null || CloudAdminToken.Parse(cookie.Value, out _) is not { } token)
                    continue;

                if (token.IsExpired)
                {
                    statusText.Text = "浏览器里的旧凭证已过期，请在上面重新登录……";
                    continue;
                }

                _poll.Stop();
                Token = token.Token;
                _logger.Info("[SparkLogin] 已取到凭证：后台账号 {0}，token {1}，到期 {2}", token.AccountName, token.Masked, token.ExpiresAt);
                DialogResult = true;
                return;
            }
        }

        private void ButtonCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
