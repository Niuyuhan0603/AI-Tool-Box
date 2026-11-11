namespace AI_Tool_Box.Services;

/// <summary>导航服务契约：负责在主内容区中切换视图。</summary>
public interface INavigationService
{
    /// <summary>获取一个值，指示是否可以向后导航。</summary>
    bool CanGoBack { get; }

    /// <summary>获取一个值，指示是否可以向前导航。</summary>
    bool CanGoForward { get; }

    /// <summary>将主内容区导航框架初始化为指定的 <see cref="Microsoft.UI.Xaml.Controls.Frame"/>。</summary>
    /// <param name="frame">承载页面切换的 Frame 控件。</param>
    void Initialize(Microsoft.UI.Xaml.Controls.Frame frame);

    /// <summary>导航到指定视图类型。</summary>
    /// <typeparam name="TView">目标视图的 <see cref="Microsoft.UI.Xaml.Controls.Page"/> 派生类型。</typeparam>
    /// <param name="parameter">可选的导航参数。</param>
    /// <returns>导航是否成功发起。</returns>
    bool Navigate<TView>(object? parameter = null) where TView : Microsoft.UI.Xaml.Controls.Page;

    /// <summary>导航到指定视图类型。</summary>
    /// <param name="viewType">目标视图类型，需派生自 <see cref="Microsoft.UI.Xaml.Controls.Page"/>。</param>
    /// <param name="parameter">可选的导航参数。</param>
    /// <returns>导航是否成功发起。</returns>
    bool Navigate(Type viewType, object? parameter = null);

    /// <summary>若历史栈非空，则向后导航。</summary>
    void GoBack();

    /// <summary>若存在前向历史，则向前导航。</summary>
    void GoForward();
}
