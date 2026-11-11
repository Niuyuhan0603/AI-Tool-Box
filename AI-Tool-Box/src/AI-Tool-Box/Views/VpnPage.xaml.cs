using AI_Tool_Box.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace AI_Tool_Box.Views;

/// <summary>VPN 页：加密代理节点接入（一键连接 + 连通性测试 + 流量统计）。</summary>
public sealed partial class VpnPage : Page
{
    public VpnViewModel ViewModel { get; }

    public VpnPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<VpnViewModel>();
        DataContext = ViewModel;
    }
}
