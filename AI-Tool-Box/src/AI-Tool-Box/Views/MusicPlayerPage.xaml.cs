using AI_Tool_Box.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using System.Collections.ObjectModel;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Pickers;

namespace AI_Tool_Box.Views;

/// <summary>
/// 音乐播放器页：借鉴 MusicPlayer2 的功能与布局，全部原生 WinUI 实现。
/// 播放使用 Windows.Media.Playback.MediaPlayer；歌词/封面/收藏由 MusicPlayerService 提供。
/// 播放模式：列表顺序 / 乱序 / 单曲循环（借鉴 MusicPlayer2 RepeatMode，简化三种）。
/// </summary>
public sealed partial class MusicPlayerPage : Page
{
    private static readonly ILogger Log = Serilog.Log.ForContext<MusicPlayerPage>();

    private enum PlayMode { ListOrder, Shuffle, SingleLoop }

    // 播放器与状态（静态跨页面保留：切换页面不中断播放）
    private static MediaPlayer? _player;
    private static DispatcherTimer? _ticker;
    private static PlayMode _mode = PlayMode.ListOrder;

    // 歌曲列表（全部 + 当前显示视图）
    private static readonly ObservableCollection<SongItem> _allSongs = new();
    private static List<SongItem> _currentView = new();
    private static int _currentIndex = -1;
    private static string _lastSortKey = "path";
    private static bool _favFilterOnly;
    private static string _searchText = string.Empty;

    // 歌词
    private static List<LyricLine> _lyrics = new();
    private static int _lyricIndex = -1;

    private bool _pageLoaded;
    private bool _restoring;
    private bool _suppressSliderEvent;

    public MusicPlayerPage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    // ==================== 生命周期 ====================

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_pageLoaded) return;
        _pageLoaded = true;

        // 保险：无论 WebShell 此前挂载在哪个页面槽位，加载本页时一律强制摘除归还 HiddenWebHost，
        // 防止网址导航的网页/地址栏 UI 残留在右侧覆盖本页（用户反馈的覆盖问题根因）。
        try
        {
            if (App.MainWindow is MainWindow mw)
            {
                mw.HideWebShellForNavigation();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "音乐播放器加载时隐藏 WebShell 失败");
        }

        // 响应式：监听主窗口尺寸，动态调整右栏宽度，避免窄窗口下 UI 挤压/堆叠
        try
        {
            if (App.MainWindow is Microsoft.UI.Xaml.Window w)
            {
                w.SizeChanged += OnMainWindowSizeChanged;
                OnMainWindowSizeChanged(w, null);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "注册窗口尺寸监听失败");
        }

        // 惰性初始化播放器（首次进入页面时，UI 线程）
        _player ??= CreatePlayer();
        _ticker ??= StartTicker();

        // 恢复上次会话的歌曲列表（跨页面/应用保留；只恢复显示，不自动播放）
        if (_allSongs.Count > 0)
        {
            _restoring = true;
            ApplyFilterAndSort();
            if (_currentIndex >= 0 && _currentIndex < _currentView.Count)
            {
                SongList.SelectedIndex = _currentIndex;
                ShowNowPlaying(_currentView[_currentIndex]);
            }
            _restoring = false;
        }
        UpdateModeButton();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        // 离开页面：播放器后台继续播放，仅停止 UI 刷新计时器避免泄漏
        // （_ticker 为静态，页面重进时复用；这里不停止，由 OnPageLoaded 幂等初始化）
        try
        {
            if (App.MainWindow is Microsoft.UI.Xaml.Window w)
            {
                w.SizeChanged -= OnMainWindowSizeChanged;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "注销窗口尺寸监听失败");
        }
    }

    /// <summary>响应式布局：按主窗口宽度调整右栏（歌曲列表）宽度，避免窄窗口下 UI 挤压/堆叠。</summary>
    private void OnMainWindowSizeChanged(object sender, Microsoft.UI.Xaml.WindowSizeChangedEventArgs? e)
    {
        try
        {
            double width = e is not null ? e.Size.Width : (App.MainWindow?.AppWindow.Size.Width ?? 1200);
            double right = width >= 1100 ? 400 : width >= 860 ? 340 : width >= 700 ? 280 : 240;
            if (RightCol is not null && Math.Abs(RightCol.Width.Value - right) > 1)
            {
                RightCol.Width = new GridLength(right);
            }
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "响应式调整右栏宽度失败");
        }
    }

    private static MediaPlayer CreatePlayer()
    {
        var mp = new MediaPlayer
        {
            AutoPlay = false,
            AudioCategory = MediaPlayerAudioCategory.Media,
            Volume = 0.8
        };
        mp.MediaOpened += (_, _) => { };
        mp.MediaEnded += (_, _) => OnMediaEnded();
        mp.MediaFailed += (_, args) => Log.Warning("播放失败：{Error}", args.ErrorMessage);
        return mp;
    }

    private static DispatcherTimer StartTicker()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += OnTick;
        timer.Start();
        return timer;
    }

    private static void OnTick(object? sender, object e)
    {
        // 更新进度条与歌词滚动（由页面实例转发）
        CurrentPageInstance?.UpdateProgressUi();
    }

    // 当前页面实例（UI 线程单实例，用于静态 Tick/MediaEnded 回调转发）
    private static MusicPlayerPage? _instance;
    private static MusicPlayerPage? CurrentPageInstance
    {
        get => _instance;
        set => _instance = value;
    }

    // ==================== 歌曲列表：添加 / 排序 / 筛选 / 搜索 ====================

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        // 应用为普通权限运行（asInvoker），UWP FolderPicker 正常工作（与全局其它选择器保持一致）
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        var files = MusicPlayerService.ScanFolder(folder.Path);
        if (files.Count == 0)
        {
            ShowTip("该文件夹下没有找到音频文件。");
            return;
        }

        int added = 0;
        foreach (var f in files)
        {
            if (_allSongs.Any(s => string.Equals(s.Path, f, StringComparison.OrdinalIgnoreCase))) continue;
            var song = new SongItem { Path = f, FileName = Path.GetFileName(f) };
            _allSongs.Add(song);
            added++;
        }

        // 异步批量读取标签（去重后统一刷新）
        await LoadTagsAsync(_allSongs.Where(s => string.IsNullOrEmpty(s.Title)).ToList());

        MusicPlayerService.ApplyFavorites(_allSongs);
        _lastSortKey = "path";
        ApplyFilterAndSort();
        ShowTip($"已添加 {added} 首歌曲（共 {_allSongs.Count} 首）。");
        Log.Information("音乐库添加文件夹：{Dir}，新增 {Added} 首", folder.Path, added);
    }

    private static async Task LoadTagsAsync(List<SongItem> songs)
    {
        // 限并发读取标签，避免大量文件时卡 UI
        foreach (var song in songs)
        {
            await MusicPlayerService.LoadSongMetadataAsync(song);
        }
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        // 关键修复：XAML 中 SortCombo 的 SelectedIndex="0" 会在 InitializeComponent 期间提前触发本事件，
        // 此时 SongList 等控件尚未创建（NRE → Frame.Navigate 异常 → 导航失败回退）。必须判空拦截。
        if (SongList is null) return;
        if (SortCombo.SelectedIndex < 0) return;
        _lastSortKey = SortCombo.SelectedIndex switch
        {
            1 => "title",
            2 => "artist",
            3 => "album",
            4 => "duration",
            _ => "path",
        };
        ApplyFilterAndSort();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text?.Trim() ?? string.Empty;
        ApplyFilterAndSort();
    }

    private void OnFavFilterClick(object sender, RoutedEventArgs e)
    {
        _favFilterOnly = FavFilterBtn.IsChecked == true;
        ApplyFilterAndSort();
    }

    /// <summary>按 排序 + 收藏筛选 + 搜索 重新生成当前显示列表，并保持当前播放项选中。</summary>
    private void ApplyFilterAndSort()
    {
        // 防御：构造期事件提前触发时 SongList 尚未创建，直接返回避免 NRE
        if (SongList is null) return;

        IEnumerable<SongItem> view = _allSongs;
        if (_favFilterOnly) view = view.Where(s => s.IsFavorite);
        if (!string.IsNullOrEmpty(_searchText)) view = view.Where(s => s.SearchKey.Contains(_searchText));

        var list = view.ToList();
        switch (_lastSortKey)
        {
            case "title": list.Sort((a, b) => string.Compare(a.DisplayTitle, b.DisplayTitle, StringComparison.OrdinalIgnoreCase)); break;
            case "artist": list.Sort((a, b) => string.Compare(a.Artist, b.Artist, StringComparison.OrdinalIgnoreCase)); break;
            case "album": list.Sort((a, b) => string.Compare(a.Album, b.Album, StringComparison.OrdinalIgnoreCase)); break;
            case "duration": list.Sort((a, b) => a.Duration.CompareTo(b.Duration)); break;
            default: list.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase)); break;
        }

        _currentView = list;
        SongList.ItemsSource = null;
        SongList.ItemsSource = _currentView;

        // 保持当前播放项选中
        if (_currentIndex >= 0 && _currentIndex < _currentView.Count)
        {
            SongList.SelectedIndex = _currentIndex;
        }
    }

    // ==================== 播放控制 ====================

    private void OnSongSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_restoring) return;
        if (SongList.SelectedIndex < 0 || SongList.SelectedIndex >= _currentView.Count) return;
        var song = _currentView[SongList.SelectedIndex];
        _currentIndex = SongList.SelectedIndex;
        PlaySong(song);
    }

    private void PlaySong(SongItem song)
    {
        try
        {
            _player!.Source = MediaSource.CreateFromUri(new Uri(song.Path));
            _player.Play();
            _ = RefreshNowPlayingUiAsync(song);
            Log.Information("播放：{Title}（{Path}）", song.DisplayTitle, song.Path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "播放失败：{Path}", song.Path);
            ShowTip($"无法播放：{song.DisplayTitle}");
        }
    }

    private async Task RefreshNowPlayingUiAsync(SongItem song)
    {
        // 标题/艺术家
        TitleText.Text = song.DisplayTitle;
        ArtistText.Text = string.IsNullOrEmpty(song.DisplaySubtitle) ? "未知艺术家" : song.DisplaySubtitle;
        FavToggle.IsChecked = song.IsFavorite;

        // 曲绘（异步）
        CoverImage.Source = await MusicPlayerService.LoadCoverAsync(song.Path) ?? new BitmapImage(new Uri("ms-appx:///Assets/H+_logo-transparent.ico"));

        // 歌词（异步）
        _lyrics = await MusicPlayerService.LoadLyricsAsync(song.Path);
        _lyricIndex = -1;
        BuildLyricPanel();
    }

    private void ShowNowPlaying(SongItem song)
    {
        TitleText.Text = song.DisplayTitle;
        ArtistText.Text = string.IsNullOrEmpty(song.DisplaySubtitle) ? "未知艺术家" : song.DisplaySubtitle;
        FavToggle.IsChecked = song.IsFavorite;
        _ = MusicPlayerService.LoadCoverAsync(song.Path).ContinueWith(t =>
        {
            var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            dq?.TryEnqueue(() =>
            {
                if (t.Result is not null) CoverImage.Source = t.Result;
            });
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (_player!.Source is null)
        {
            // 无播放内容：自动选列表第一首
            if (_currentView.Count == 0) { ShowTip("请先添加音乐文件夹。"); return; }
            SongList.SelectedIndex = 0;
            return;
        }
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _player.Pause();
            PlayBtn.Content = "\uE768"; // Play
        }
        else
        {
            _player.Play();
            PlayBtn.Content = "\uE769"; // Pause
        }
    }

    private void OnPrevClick(object sender, RoutedEventArgs e)
    {
        // 回到上一首（若当前进度>3秒则重头播放，否则上一首）
        if (_currentIndex < 0) return;
        if (_player is { Position.TotalSeconds: > 3 })
        {
            _player.Position = TimeSpan.Zero;
            return;
        }
        SelectSongByOffset(-1);
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        SelectSongByOffset(1);
    }

    private void SelectSongByOffset(int offset)
    {
        if (_currentView.Count == 0) return;
        int next = (_currentIndex + offset + _currentView.Count) % _currentView.Count;
        if (next >= 0 && next < _currentView.Count)
        {
            SongList.SelectedIndex = next;
        }
    }

    /// <summary>MediaEnded：按播放模式决定下一首（列表顺序 / 乱序随机 / 单曲循环）。</summary>
    private static void OnMediaEnded()
    {
        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        dq?.TryEnqueue(() =>
        {
            var page = CurrentPageInstance;
            if (page is null) return;
            switch (_mode)
            {
                case PlayMode.SingleLoop:
                    page.ReplayCurrent();
                    break;
                case PlayMode.Shuffle:
                    if (_currentView.Count > 1)
                    {
                        var rnd = new Random();
                        int next = rnd.Next(0, _currentView.Count);
                        page.SongList.SelectedIndex = next;
                    }
                    else page.ReplayCurrent();
                    break;
                default: // ListOrder
                    if (_currentIndex >= _currentView.Count - 1)
                    {
                        // 播完列表停止
                        page.PlayBtn.Content = "\uE768";
                    }
                    else page.SelectSongByOffset(1);
                    break;
            }
        });
    }

    private void ReplayCurrent()
    {
        if (_currentIndex < 0 || _currentIndex >= _currentView.Count) return;
        PlaySong(_currentView[_currentIndex]);
    }

    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        _mode = _mode switch
        {
            PlayMode.ListOrder => PlayMode.Shuffle,
            PlayMode.Shuffle => PlayMode.SingleLoop,
            _ => PlayMode.ListOrder,
        };
        UpdateModeButton();
        Log.Information("播放模式切换：{Mode}", _mode);
    }

    private void UpdateModeButton()
    {
        ModeBtn.Content = _mode switch
        {
            PlayMode.Shuffle => "乱序",
            PlayMode.SingleLoop => "单曲循环",
            _ => "列表顺序",
        };
    }

    private void OnFavToggleClick(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= _currentView.Count) return;
        var song = _currentView[_currentIndex];
        MusicPlayerService.ToggleFavorite(song, _allSongs);
        FavToggle.IsChecked = song.IsFavorite;
        RefreshListFavIcon();
    }

    private void RefreshListFavIcon()
    {
        // 刷新列表项（收藏图标）
        var tmp = _currentView.ToList();
        SongList.ItemsSource = null;
        SongList.ItemsSource = tmp;
        if (_currentIndex >= 0 && _currentIndex < _currentView.Count) SongList.SelectedIndex = _currentIndex;
        _currentView = tmp;
    }

    // ==================== 进度 / 音量 / 歌词滚动 ====================

    private void UpdateProgressUi()
    {
        if (_player is null || _suppressSliderEvent) return;

        var pos = _player.Position;
        PositionText.Text = $"{pos.Minutes:00}:{pos.Seconds:00}";
        var dur = _player.NaturalDuration;
        if (dur > TimeSpan.Zero)
        {
            DurationText.Text = $"{dur.Minutes:00}:{dur.Seconds:00}";
            _suppressSliderEvent = true;
            ProgressSlider.Value = Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1);
            _suppressSliderEvent = false;
        }

        UpdateLyricHighlight(pos);
    }

    private void OnProgressScrubbed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSliderEvent || _player is null) return;
        var dur = _player.NaturalDuration;
        if (dur <= TimeSpan.Zero) return;
        _player.Position = TimeSpan.FromSeconds(e.NewValue * dur.TotalSeconds);
        PositionText.Text = $"{_player.Position.Minutes:00}:{_player.Position.Seconds:00}";
    }

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_player is not null) _player.Volume = e.NewValue / 100.0;
    }

    // ==================== 歌词显示 ====================

    private void BuildLyricPanel()
    {
        LyricPanel.Children.Clear();
        if (_lyrics.Count == 0)
        {
            LyricEmptyHint.Visibility = Visibility.Visible;
            return;
        }
        LyricEmptyHint.Visibility = Visibility.Collapsed;
        foreach (var line in _lyrics)
        {
            // 每句歌词一个 TextBlock：高度自适应（长句自动换行独占多行），上下留间距避免"黏在一起"
            var tb = new TextBlock
            {
                Text = line.Text,
                FontSize = 16,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(ColorFromHex("#7E96A8")),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 30,
                Margin = new Thickness(0, 5, 0, 5),
                VerticalAlignment = VerticalAlignment.Center,
                LineHeight = 24
            };
            LyricPanel.Children.Add(tb);
        }
    }

    private void UpdateLyricHighlight(TimeSpan pos)
    {
        if (_lyrics.Count == 0 || LyricPanel.Children.Count == 0) return;

        // 找到当前时间所在歌词行
        int idx = _lyrics.Count - 1;
        for (int i = 0; i < _lyrics.Count; i++)
        {
            if (_lyrics[i].Time > pos) { idx = Math.Max(0, i - 1); break; }
        }

        if (idx == _lyricIndex) return;
        _lyricIndex = idx;

        for (int i = 0; i < LyricPanel.Children.Count; i++)
        {
            if (LyricPanel.Children[i] is not TextBlock tb) continue;
            if (i == idx)
            {
                tb.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(ColorFromHex("#00E5FF"));
                tb.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }
            else
            {
                tb.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(ColorFromHex("#7E96A8"));
                tb.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            }
        }

        // 自动滚动到当前行（居中）：用实际布局位置定位，行高自适应（长句多行时同样准确）
        try
        {
            if (LyricPanel.Children[idx] is FrameworkElement cur
                && LyricScroller.ViewportHeight > 0)
            {
                var p = cur.TransformToVisual(LyricPanel).TransformPoint(new Windows.Foundation.Point(0, 0));
                double target = p.Y - (LyricScroller.ViewportHeight - cur.ActualHeight) / 2;
                if (target < 0) target = 0;
                LyricScroller.ChangeView(null, target, null, disableAnimation: false);
            }
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "歌词滚动定位失败");
        }
    }

    private static Windows.UI.Color ColorFromHex(string hex)
    {
        hex = hex.TrimStart('#');
        byte r = Convert.ToByte(hex.Substring(0, 2), 16);
        byte g = Convert.ToByte(hex.Substring(2, 2), 16);
        byte b = Convert.ToByte(hex.Substring(4, 2), 16);
        return Windows.UI.Color.FromArgb(255, r, g, b);
    }

    private void ShowTip(string text)
    {
        Log.Information("音乐播放器提示：{Text}", text);
    }

    /// <summary>应用退出时清理播放器（MainWindow.Closed 调用）。</summary>
    public static void ShutdownForAppExit()
    {
        try
        {
            _ticker?.Stop();
            _ticker = null;
            _player?.Dispose();
            _player = null;
        }
        catch { /* 忽略 */ }
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        CurrentPageInstance = null;
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        CurrentPageInstance = this;
    }
}
