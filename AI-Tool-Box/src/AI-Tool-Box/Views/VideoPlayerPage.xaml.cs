using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Serilog;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Pickers;

namespace AI_Tool_Box.Views;

/// <summary>
/// 视频播放器页：MediaPlayerElement 播放本地视频或在线视频直链（mp4/m3u8/webm 等）。
/// 内置传输控件（播放/暂停/进度/音量/全屏）。播放器为静态实例，切换页面不中断。
/// </summary>
public sealed partial class VideoPlayerPage : Page
{
    private static MediaPlayer? _player;
    private readonly ILogger _logger;

    public VideoPlayerPage()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger
                  ?? Serilog.Log.Logger;

        Loaded += (_, _) =>
        {
            try
            {
                EnsurePlayer();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "初始化视频播放器失败");
                StatusText.Text = $"播放器初始化失败：{ex.Message}";
            }
        };
    }

    /// <summary>惰性创建 MediaPlayer 并绑定到 MediaPlayerElement（跨页面复用）。</summary>
    private void EnsurePlayer()
    {
        _player ??= CreatePlayer();
        if (Player.MediaPlayer != _player)
        {
            Player.SetMediaPlayer(_player);
        }
    }

    private MediaPlayer CreatePlayer()
    {
        var mp = new MediaPlayer
        {
            AutoPlay = false,
            Volume = 0.9
        };
        mp.MediaOpened += (_, _) =>
        {
            var dur = mp.NaturalDuration;
            StatusText.Text = dur > TimeSpan.Zero
                ? $"正在播放（时长 {dur.Minutes:00}:{dur.Seconds:00}）"
                : "正在播放";
        };
        mp.MediaEnded += (_, _) => StatusText.Text = "播放结束";
        mp.MediaFailed += (_, args) =>
        {
            StatusText.Text = $"播放失败：{args.ErrorMessage}";
            _logger.Warning("视频播放失败：{Error}", args.ErrorMessage);
        };
        return mp;
    }

    private void PlaySource(IMediaPlaybackSource source, string display)
    {
        try
        {
            EnsurePlayer();
            _player!.Source = source;
            _player.Play();
            StatusText.Text = $"正在加载：{display}";
            _logger.Information("视频播放：{Source}", display);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "播放失败：{Source}", display);
            StatusText.Text = $"播放失败：{ex.Message}";
        }
    }

    // ==================== 本地文件 ====================

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
            foreach (var ext in new[] { ".mp4", ".mkv", ".webm", ".avi", ".mov", ".flv", ".wmv", ".m4v", ".ts", ".mpg", ".mpeg", ".3gp" })
            {
                picker.FileTypeFilter.Add(ext);
            }
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            PlaySource(MediaSource.CreateFromStorageFile(file), file.Name);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "打开视频失败");
            StatusText.Text = $"打开失败：{ex.Message}";
        }
    }

    // ==================== 在线 URL ====================

    private void OnPlayUrlClick(object sender, RoutedEventArgs e) => PlayFromUrlBox();

    private void OnUrlKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            PlayFromUrlBox();
        }
    }

    private void PlayFromUrlBox()
    {
        var url = UrlBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(url))
        {
            StatusText.Text = "请输入在线视频地址。";
            return;
        }
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }
        try
        {
            PlaySource(MediaSource.CreateFromUri(new Uri(url)), url);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "在线视频地址无效：{Url}", url);
            StatusText.Text = $"地址无效：{ex.Message}";
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _player?.Pause();
            if (_player is not null) _player.Source = null;
            StatusText.Text = "已停止";
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "停止播放失败");
        }
    }
}
