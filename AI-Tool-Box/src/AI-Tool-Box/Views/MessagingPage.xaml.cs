using AI_Tool_Box.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using System;

namespace AI_Tool_Box.Views;

/// <summary>通讯软件接入页：Telegram Bot 长轮询 + Webhook 推送。</summary>
public sealed partial class MessagingPage : Page
{
    private readonly ILogger _logger;
    private readonly IBotBridgeService _bot;
    private readonly IApiKeyService _keys;
    private readonly DispatcherQueue _dispatcher;

    /// <summary>初始化页面。</summary>
    public MessagingPage()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger ?? Serilog.Log.Logger;
        _bot = App.Services.GetRequiredService<IBotBridgeService>();
        _keys = App.Services.GetRequiredService<IApiKeyService>();
        _dispatcher = DispatcherQueue.GetForCurrentThread() ?? DispatcherQueueController.CreateOnDedicatedThread().DispatcherQueue;

        TokenBox.Text = _keys.Load("telegram") ?? string.Empty;
        Unloaded += (_, _) => _bot.Stop();
        _logger.Information("MessagingPage 已加载");
    }

    private void AppendLog(string line) => _dispatcher.TryEnqueue(() =>
    {
        BotLogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}";
    });

    private void OnStartBotClick(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            BotStatusText.Text = "请填写 Bot Token（从 @BotFather 获取）。";
            return;
        }

        try
        {
            _keys.Save("telegram", token);
            _bot.StartTelegramAsync(token, AiSwitch.IsOn, AppendLog);
            BotStatusText.Text = "运行中：在 Telegram @ 你的 Bot 发送消息即可。";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "启动 Telegram Bot 失败");
            BotStatusText.Text = $"启动失败：{ex.Message}";
        }
    }

    private void OnStopBotClick(object sender, RoutedEventArgs e)
    {
        _bot.Stop();
        BotStatusText.Text = "已停止。";
        AppendLog("用户停止 Bot");
    }

    private async void OnSendWebhookClick(object sender, RoutedEventArgs e)
    {
        var url = WebhookUrlBox.Text?.Trim() ?? string.Empty;
        var text = WebhookTextBox.Text ?? string.Empty;
        if (url.Length == 0 || text.Trim().Length == 0)
        {
            WebhookResultText.Text = "请填写 Webhook 地址与消息内容。";
            return;
        }

        try
        {
            WebhookResultText.Text = "发送中…";
            var kind = KindBox.SelectedItem as string ?? "自定义 JSON";
            WebhookResultText.Text = await _bot.SendWebhookAsync(url, text, kind);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Webhook 推送失败");
            WebhookResultText.Text = $"发送失败：{ex.Message}";
        }
    }
}
