using AI_Tool_Box.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Windows.Storage.Pickers;

namespace AI_Tool_Box.ViewModels;

/// <summary>局域网传文件视图模型：设备发现、发送队列（选择/拖放加入，先选文件后发送）、接收确认（自动/手动）、传输进度与速度。</summary>
public partial class FileTransferViewModel : ObservableObject
{
    private readonly ILogger _logger;
    private readonly ILanTransferService _service;

    /// <summary>在线设备集合（Service 保证 UI 线程增删）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<LanPeer> OnlinePeers => _service.OnlinePeers;

    /// <summary>传输记录（发送+接收）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<TransferProgress> Transfers => _service.Transfers;

    /// <summary>待发送文件队列（先选文件，后统一发送）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<QueuedFile> PendingFiles { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private LanPeer? _selectedPeer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedCommand))]
    private QueuedFile? _selectedPendingFile;

    [ObservableProperty]
    private bool _isListening;

    /// <summary>监听开关按钮文字（随状态切换）。</summary>
    public string ListenButtonText => IsListening ? "停止监听" : "开始监听";

    partial void OnIsListeningChanged(bool value) => OnPropertyChanged(nameof(ListenButtonText));

    [ObservableProperty]
    private string _status = "点击「开始监听」进入局域网：UDP 多播发现附近设备，HTTP 会话式传输（借鉴 LocalSend 协议）。";

    [ObservableProperty]
    private string _localInfo = string.Empty;

    /// <summary>当前日志目录（展示在页面上，失败时用户可直接据此找到日志文件）。</summary>
    public string LogDirectory => $"日志目录：{LoggerSetup.CurrentLogRoot}";

    [ObservableProperty]
    private string _alias;

    [ObservableProperty]
    private bool _autoAccept;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSaveDirCommand))]
    private string _saveDirectory;

    /// <summary>初始化 ViewModel（不自动启动，避免页面加载即占端口）。</summary>
    public FileTransferViewModel(ILogger logger, ILanTransferService service)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _alias = _service.Alias;
        _autoAccept = _service.AutoAccept;
        _saveDirectory = _service.SaveDirectory;
        RefreshLocalInfo();
    }

    private void RefreshLocalInfo()
    {
        var port = _service.LocalPort > 0 ? _service.LocalPort : 53317;
        LocalInfo = $"{_service.Alias} @ {_service.LocalIpAddress}:{port} · 指纹 {_service.Fingerprint[..8]}…";
    }

    partial void OnAliasChanged(string value)
    {
        try
        {
            _service.Alias = value;
            RefreshLocalInfo();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "设置设备别名失败");
        }
    }

    partial void OnAutoAcceptChanged(bool value) => _service.AutoAccept = value;

    partial void OnSaveDirectoryChanged(string value)
    {
        try
        {
            _service.SaveDirectory = value;
        }
        catch (Exception ex)
        {
            Status = $"保存目录无效：{ex.Message}";
        }
    }

    private bool CanSend() => SelectedPeer is not null && PendingFiles.Count > 0;

    private bool CanRemoveSelected() => SelectedPendingFile is not null;

    /// <summary>Page 层拖放 / FileOpenPicker 完成后调用：把一批文件加入发送队列（去重、忽略不存在的路径）。</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) continue;
            if (PendingFiles.Any(f => string.Equals(f.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            var fi = new FileInfo(p);
            PendingFiles.Add(new QueuedFile(p, fi.Name, fi.Length));
            added++;
        }
        SendCommand.NotifyCanExecuteChanged();
        Status = added > 0 ? $"已加入 {added} 个待发送文件（队列共 {PendingFiles.Count} 个）。" : "未加入新文件（可能已存在或不可读）。";
    }

    /// <summary>「选择文件…」：FileOpenPicker 多选（WinUI3 中 Picker 必须带主窗口 HWND 初始化）。</summary>
    [RelayCommand]
    private async Task PickFilesAsync()
    {
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0) return;
            AddFiles(files.Select(f => f.Path));
            _logger.Information("用户选择待发送文件 {Count} 个", files.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "选择待发送文件失败");
            Status = $"选择文件失败：{ex.Message}";
        }
    }

    /// <summary>移除队列中选中的文件。</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private void RemoveSelected()
    {
        if (SelectedPendingFile is null) return;
        var name = SelectedPendingFile.FileName;
        PendingFiles.Remove(SelectedPendingFile);
        SelectedPendingFile = null;
        SendCommand.NotifyCanExecuteChanged();
        Status = $"已移除「{name}」（队列剩 {PendingFiles.Count} 个）。";
    }

    /// <summary>启动 / 停止监听。</summary>
    [RelayCommand]
    private void ToggleListening()
    {
        if (!IsListening)
        {
            if (_service.Start())
            {
                IsListening = true;
                RefreshLocalInfo();
                Status = "已启动：正在通过 UDP 多播发现附近设备…（首次使用请在防火墙弹窗中允许本应用）";
                _logger.Information("用户启动局域网传输：{Info}", LocalInfo);
            }
            else
            {
                Status = "启动失败（端口被占用或网络异常），请检查日志。";
            }
        }
        else
        {
            _service.Stop();
            IsListening = false;
            RefreshLocalInfo();
            Status = "已停止：多播发现与 HTTP 传输服务均已关闭。";
            _logger.Information("用户停止局域网传输");
        }
    }

    /// <summary>立即广播 announce，加速设备互相发现。</summary>
    [RelayCommand]
    private void Refresh()
    {
        if (!IsListening)
        {
            Status = "请先启动，再刷新设备列表。";
            return;
        }
        _service.AnnounceNow();
        Status = "已发送 announce 宣告，等待附近设备回应…";
    }

    /// <summary>把发送队列中的所有文件发给选中设备（复用服务批量发送 API），并汇总结果。</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (!CanSend())
        {
            Status = "请先选择在线设备，并往发送队列加入至少一个文件。";
            return;
        }
        var peer = SelectedPeer!;
        var paths = PendingFiles.Select(f => f.Path).ToList();
        var total = paths.Count;
        var totalSize = TransferProgress.FormatSize(PendingFiles.Sum(f => f.Size));
        var recordedBefore = Transfers.Count;
        Status = $"正在向 {peer.Alias} 征询接收 {total} 个文件（共 {totalSize}）…（对方屏幕会弹出确认框）";
        try
        {
            await _service.SendFilesAsync(peer, paths);
            // 汇总本次新增的传输条目（服务内部逐文件更新进度/状态）
            var results = Transfers.Skip(recordedBefore).ToList();
            var failedItems = results.Where(p => p.Status.Contains("失败")
                                                 || p.Status.Contains("取消")
                                                 || p.Status.Contains("拒绝")
                                                 || p.Status.Contains("忙")
                                                 || p.Status.Contains("未选择"))
                                     .ToList();
            var failed = failedItems.Count;
            if (failed > 0)
            {
                // 直接把第一条失败原因写进状态区（不用滚动列表也能看到根因）
                var first = failedItems[0];
                Status = $"发送流程结束：{total - failed}/{total} 个文件成功，{failed} 个失败。首个错误：{first.FileName} → {first.Status}";
            }
            else
            {
                Status = $"发送流程结束：{total} 个文件均已成功，详见传输记录。";
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "发起发送失败");
            Status = $"发送失败：{ex.Message}";
        }
    }

    /// <summary>清空发送队列。</summary>
    [RelayCommand]
    private void ClearFiles()
    {
        PendingFiles.Clear();
        SelectedPendingFile = null;
        SendCommand.NotifyCanExecuteChanged();
        Status = "已清空发送队列。";
    }

    /// <summary>在资源管理器中打开保存目录。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenSaveDir))]
    private void OpenSaveDir()
    {
        try
        {
            if (!Directory.Exists(SaveDirectory)) Directory.CreateDirectory(SaveDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = SaveDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "打开保存目录失败");
            Status = $"打开目录失败：{ex.Message}";
        }
    }

    private bool CanOpenSaveDir() => !string.IsNullOrWhiteSpace(SaveDirectory);
}

/// <summary>待发送文件队列条目（路径、文件名、大小）。</summary>
public sealed record QueuedFile(string Path, string FileName, long Size)
{
    /// <summary>大小文本（自动格式化）。</summary>
    public string SizeText => TransferProgress.FormatSize(Size);
}
