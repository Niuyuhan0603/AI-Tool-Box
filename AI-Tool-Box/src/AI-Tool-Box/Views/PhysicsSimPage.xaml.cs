using AI_Tool_Box.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Color = Windows.UI.Color;
using Point = Windows.Foundation.Point;

namespace AI_Tool_Box.Views;

/// <summary>
/// 物理题模拟器：运动场景（小球 / 线段障碍 / 弹簧）+ 电路模拟（电池·电阻·电容·电感·开关，MNA 直流与 RC/RL 暂态）。
/// </summary>
public sealed partial class PhysicsSimPage : Page
{
    private const double WorldWidth = 10.0;
    private const double ElementHalfLength = 0.6;

    private readonly ILogger _logger;
    private readonly IAiQuickService _ai;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _transientTimer;
    private readonly List<Ball> _balls = new();
    private readonly List<Segment> _segments = new();
    private readonly List<Segment> _solidSegments = new();   // 固定木板/水平面的碰撞面（不直接绘制线段）
    private readonly List<Plane> _planes = new();
    private readonly List<Board> _boards = new();
    private readonly List<Spring> _springs = new();
    private readonly List<CircuitElement> _elements = new();
    private readonly List<CircuitWire> _wires = new();

    private double _gravity = 9.8;
    private double _restitution = 0.65;
    private double _friction = 0.03;

    // 画布缩放：1 = 世界宽 10 m 铺满画布
    private double _zoom = 1.0;
    private const double MinZoom = 0.5;
    private const double MaxZoom = 4.0;

    // 视图平移（单位：米）：可视区域左下角对应的世界坐标；右键拖动即可平移
    private double _panX;
    private double _panY;
    private bool _panning;
    private Point _panStartPoint;
    private double _panStartX, _panStartY;

    // 交互状态
    private bool _ready;
    private bool _circuitMode;
    private bool _springTool;
    private Spring? _selectedSpring;
    private Ball? _dragBall;
    private Spring? _dragSpringAnchor;
    private CircuitElement? _selectedElement;
    private bool _dragElement;
    private double _dragOffsetX, _dragOffsetY;
    // 固定物体（固定水平面/木板）：右键确认解锁后，左键可拖动
    private Plane? _unlockedPlane;
    private Board? _unlockedBoard;
    private Plane? _dragPlane;
    private Board? _dragBoard;
    private double _solidGrabX, _solidGrabY;
    private bool _wireMode;
    private int _wireStartTerminal = -1;
    private int _nextElementId = 1;
    private bool _updatingParams;
    private double _transientTime;
    private const double TransientDt = 0.001;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>初始化页面与定时器。</summary>
    public PhysicsSimPage()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger ?? Serilog.Log.Logger;
        _ai = App.Services.GetRequiredService<IAiQuickService>();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => { Step(0.016); Render(); };
        _transientTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _transientTimer.Tick += (_, _) => RunTransientSteps();
        Unloaded += (_, _) => { _timer.Stop(); _transientTimer.Stop(); };

        ApplyPreset(FreeFallScene());
        _ready = true;
        _logger.Information("PhysicsSimPage 已加载");
    }

    // ==================== 模式切换 ====================

    private void OnModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _timer.Stop();
        _transientTimer.Stop();
        _circuitMode = ModeRadios.SelectedIndex == 1;
        MotionPanel.Visibility = _circuitMode ? Visibility.Collapsed : Visibility.Visible;
        CircuitPanel.Visibility = _circuitMode ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = _circuitMode
            ? "电路模式：添加元件后拖动摆放，点「连线」依次点两个端子建立导线，再点「求解（直流）」。"
            : "运动模式：可拖球、拖弹簧、放置弹簧工具；小球受重力与弹簧力作用。";
        Render();
    }

    // ==================== 运行控制（运动） ====================

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_circuitMode) return;
        ReadParams();
        _timer.Start();
        StatusText.Text = "运行中…";
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        StatusText.Text = "已暂停。";
    }

    private void OnResetClick(object sender, RoutedEventArgs e) => ApplySceneJson(SceneBox.Text, "已重置。");

    private void OnApplySceneClick(object sender, RoutedEventArgs e) => ApplySceneJson(SceneBox.Text, "场景已应用。");

    private void ReadParams()
    {
        _gravity = ParseDouble(GravityBox.Text, 9.8);
        _restitution = Math.Clamp(ParseDouble(RestitutionBox.Text, 0.65), 0, 1);
        _friction = Math.Clamp(ParseDouble(FrictionBox.Text, 0.03), 0, 1);
    }

    // ==================== 预设 ====================

    private static SceneDto FreeFallScene() => new()
    {
        Gravity = 9.8,
        Restitution = 0.65,
        Friction = 0.03,
        Balls = new List<BallDto> { new() { X = 2, Y = 5, VX = 0, VY = 0, R = 0.3 } },
        Segments = new List<SegmentDto> { new() { X1 = 0, Y1 = 0, X2 = 10, Y2 = 0 } },
    };

    private void OnPresetFallClick(object sender, RoutedEventArgs e) => ApplySceneJson(JsonSerializer.Serialize(FreeFallScene(), JsonOptions), "自由落体场景已加载。");

    private void OnPresetProjectileClick(object sender, RoutedEventArgs e)
    {
        var scene = new SceneDto
        {
            Gravity = 9.8,
            Restitution = 0.6,
            Friction = 0.05,
            Balls = new List<BallDto> { new() { X = 0.6, Y = 0.4, VX = 7, VY = 8, R = 0.25 } },
            Segments = new List<SegmentDto> { new() { X1 = 0, Y1 = 0, X2 = 10, Y2 = 0 } },
        };
        ApplySceneJson(JsonSerializer.Serialize(scene, JsonOptions), "抛体运动场景已加载。");
    }

    private void OnPresetInclineClick(object sender, RoutedEventArgs e)
    {
        var scene = new SceneDto
        {
            Gravity = 9.8,
            Restitution = 0.5,
            Friction = 0.08,
            Balls = new List<BallDto> { new() { X = 1.2, Y = 4.2, VX = 0, VY = 0, R = 0.28 } },
            Segments = new List<SegmentDto>
            {
                new() { X1 = 0, Y1 = 0, X2 = 10, Y2 = 0 },
                new() { X1 = 1.5, Y1 = 0, X2 = 6.5, Y2 = 3.2 },
            },
        };
        ApplySceneJson(JsonSerializer.Serialize(scene, JsonOptions), "斜面场景已加载。");
    }

    /// <summary>添加一块固定水平面（不可拖动，参与碰撞），直接写回场景 JSON 以保持「重置」一致。</summary>
    private void OnAddPlaneClick(object sender, RoutedEventArgs e)
    {
        var height = SimCanvas.ActualHeight > 1 ? WorldHeight : 6;
        var y = Math.Clamp(height * 0.5, 0.8, 6.0);
        MutateScene(scene => scene.Planes.Add(new PlaneDto { X1 = 1.0, Y = y, X2 = 7.0 }),
            $"已添加固定水平面（y={Format(y)} m，固定不可移动）。");
    }

    /// <summary>添加一块固定木板（静态矩形，四条边参与碰撞）。</summary>
    private void OnAddBoardClick(object sender, RoutedEventArgs e)
    {
        var height = SimCanvas.ActualHeight > 1 ? WorldHeight : 6;
        var y = Math.Clamp(height * 0.35, 0.2, 4.0);
        MutateScene(scene => scene.Boards.Add(new BoardDto { X = 2.5, Y = y, W = 2.4, H = 0.35 }),
            $"已添加固定木板（{Format(2.4)}×{Format(0.35)} m，固定不可移动）。");
    }

    /// <summary>修改当前场景 JSON（新增/删除元素后重新加载并渲染）。</summary>
    private void MutateScene(Action<SceneDto> mutate, string message)
    {
        try
        {
            var scene = JsonSerializer.Deserialize<SceneDto>(SceneBox.Text, JsonOptions) ?? FreeFallScene();
            scene.Planes ??= new List<PlaneDto>();
            scene.Boards ??= new List<BoardDto>();
            mutate(scene);
            SceneBox.Text = JsonSerializer.Serialize(scene, JsonOptions);
            LoadScene(scene);
            Render();
            StatusText.Text = message;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "修改物理场景失败");
            StatusText.Text = $"添加元素失败：{ex.Message}";
        }
    }

    // ==================== 场景解析 ====================

    private void ApplyPreset(SceneDto scene)
    {
        SceneBox.Text = JsonSerializer.Serialize(scene, JsonOptions);
        ApplySceneJson(SceneBox.Text, string.Empty);
    }

    private void ApplySceneJson(string json, string successMessage)
    {
        try
        {
            _timer.Stop();
            var scene = JsonSerializer.Deserialize<SceneDto>(json ?? string.Empty, JsonOptions)
                        ?? throw new InvalidOperationException("场景 JSON 为空。");
            LoadScene(scene);
            if (!string.IsNullOrEmpty(successMessage)) StatusText.Text = successMessage;
            Render();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "解析物理场景失败");
            StatusText.Text = $"场景解析失败：{ex.Message}";
        }
    }

    private void LoadScene(SceneDto scene)
    {
        _gravity = scene.Gravity <= 0 ? 9.8 : scene.Gravity;
        _restitution = Math.Clamp(scene.Restitution, 0, 1);
        _friction = Math.Clamp(scene.Friction, 0, 1);
        GravityBox.Text = Format(_gravity);
        RestitutionBox.Text = Format(_restitution);
        FrictionBox.Text = Format(_friction);

        _balls.Clear();
        foreach (var b in scene.Balls)
        {
            _balls.Add(new Ball
            {
                X = b.X, Y = Math.Max(b.Y, b.R), VX = b.VX, VY = b.VY,
                R = b.R <= 0 ? 0.3 : b.R,
                Mass = b.Mass <= 0 ? 0.5 : b.Mass,
            });
        }

        _segments.Clear();
        foreach (var s in scene.Segments) _segments.Add(new Segment { X1 = s.X1, Y1 = s.Y1, X2 = s.X2, Y2 = s.Y2 });
        // 兜底地面与左右墙
        if (!_segments.Any(s => Math.Abs(s.Y1) < 1e-6 && Math.Abs(s.Y2) < 1e-6))
            _segments.Add(new Segment { X1 = 0, Y1 = 0, X2 = WorldWidth, Y2 = 0 });
        _segments.Add(new Segment { X1 = 0, Y1 = 0, X2 = 0, Y2 = 20 });
        _segments.Add(new Segment { X1 = WorldWidth, Y1 = 0, X2 = WorldWidth, Y2 = 20 });

        // 固定水平面 / 固定木板（静态，不可拖动；参与小球碰撞）
        scene.Planes ??= new List<PlaneDto>();
        scene.Boards ??= new List<BoardDto>();
        _planes.Clear();
        foreach (var p in scene.Planes)
        {
            _planes.Add(new Plane
            {
                X1 = p.X1,
                X2 = Math.Max(p.X1 + 0.1, p.X2),
                Y = Math.Max(0.05, p.Y),
            });
        }
        _boards.Clear();
        foreach (var b in scene.Boards)
        {
            _boards.Add(new Board
            {
                X = b.X,
                Y = Math.Max(0, b.Y),
                W = Math.Max(0.2, b.W),
                H = Math.Max(0.05, b.H),
            });
        }

        RebuildSolidSegments();

        _panX = 0;
        _panY = 0;

        _springs.Clear();
        _selectedSpring = null;
    }

    /// <summary>按当前固定水平面/木板重建碰撞面（移动固定物体后必须调用）。</summary>
    private void RebuildSolidSegments()
    {
        _solidSegments.Clear();
        foreach (var plane in _planes)
            _solidSegments.Add(new Segment { X1 = plane.X1, Y1 = plane.Y, X2 = plane.X2, Y2 = plane.Y });
        foreach (var board in _boards)
        {
            var top = board.Y + board.H;
            var right = board.X + board.W;
            _solidSegments.Add(new Segment { X1 = board.X, Y1 = top, X2 = right, Y2 = top });
            _solidSegments.Add(new Segment { X1 = board.X, Y1 = board.Y, X2 = right, Y2 = board.Y });
            _solidSegments.Add(new Segment { X1 = board.X, Y1 = board.Y, X2 = board.X, Y2 = top });
            _solidSegments.Add(new Segment { X1 = right, Y1 = board.Y, X2 = right, Y2 = top });
        }
    }

    // ==================== AI 生成场景 ====================

    private async void OnAiSceneClick(object sender, RoutedEventArgs e)
    {
        var prompt = AiPromptBox.Text;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            StatusText.Text = "请先输入题目描述。";
            return;
        }
        BusyRing.IsActive = true;
        try
        {
            StatusText.Text = $"AI（{_ai.Describe()}）生成场景中…";
            var instruction =
                "你是物理场景生成器。根据题目输出严格的 JSON（不要代码块、不要解释），坐标系单位米/秒，y 轴向上，世界宽 10 米：\n"
                + "{\"gravity\":9.8,\"restitution\":0.6,\"friction\":0.03,"
                + "\"balls\":[{\"x\":1.0,\"y\":5.0,\"vx\":0,\"vy\":0,\"r\":0.3,\"mass\":0.5}],"
                + "\"segments\":[{\"x1\":0,\"y1\":0,\"x2\":10,\"y2\":0}],"
                + "\"planes\":[{\"x1\":1,\"y\":2,\"x2\":7}],\"boards\":[{\"x\":2.5,\"y\":1.5,\"w\":2.4,\"h\":0.35}]}\n"
                + "segments 表示地面/斜面/挡板（直线段）；planes 表示固定水平面；boards 表示固定木板（矩形）。只输出 JSON。";
            var result = await _ai.AskAsync(instruction, prompt);
            var json = ExtractJson(result);
            if (json is null)
            {
                StatusText.Text = $"AI 输出不是有效 JSON：{result}";
                return;
            }
            SceneBox.Text = json;
            ApplySceneJson(json, "AI 场景已应用，点击「开始」运行。");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "AI 生成物理场景失败");
            StatusText.Text = $"AI 生成失败：{ex.Message}";
        }
        finally
        {
            BusyRing.IsActive = false;
        }
    }

    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    // ==================== 弹簧工具 ====================

    private void OnSpringToolClick(object sender, RoutedEventArgs e)
    {
        _springTool = !_springTool;
        AddSpringButton.Content = _springTool ? "弹簧工具（开）" : "弹簧工具";
        StatusText.Text = _springTool
            ? "弹簧工具已开启：在画布空白处点击放置弹簧（自动连到最近小球；没有小球时自动创建一个）。"
            : "弹簧工具已关闭。";
        Render();
    }

    private void OnSpringParamChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _selectedSpring is null) return;
        _selectedSpring.K = Math.Max(0, ParseDouble(SpringKBox.Text, 20));
        _selectedSpring.L0 = Math.Max(0.05, ParseDouble(SpringL0Box.Text, 1.5));
        _selectedSpring.Damping = Math.Max(0, ParseDouble(SpringCBox.Text, 0.2));
        Render();
    }

    private void AddSpringAt(double wx, double wy)
    {
        var ball = _balls.OrderBy(b => Dist(b.X, b.Y, wx, wy)).FirstOrDefault();
        if (ball is null || Dist(ball.X, ball.Y, wx, wy) > 4.0)
        {
            var l0 = Math.Max(0.05, ParseDouble(SpringL0Box.Text, 1.5));
            ball = new Ball { X = wx, Y = Math.Max(0.2, wy - l0), R = 0.22, Mass = 0.5 };
            _balls.Add(ball);
        }
        var spring = new Spring
        {
            AnchorX = wx,
            AnchorY = wy,
            Ball = ball,
            K = Math.Max(0, ParseDouble(SpringKBox.Text, 20)),
            L0 = Math.Max(0.05, ParseDouble(SpringL0Box.Text, 1.5)),
            Damping = Math.Max(0, ParseDouble(SpringCBox.Text, 0.2)),
        };
        _springs.Add(spring);
        _selectedSpring = spring;
        StatusText.Text = $"已添加弹簧（k={Format(spring.K)} N/m，L₀={Format(spring.L0)} m），拖动端点可调整。";
        Render();
    }

    // ==================== 物理步进 ====================

    private void Step(double dt)
    {
        const int subSteps = 4;
        var h = dt / subSteps;
        for (var s = 0; s < subSteps; s++)
        {
            foreach (var ball in _balls)
            {
                double ax = 0, ay = -_gravity;
                foreach (var spring in _springs.Where(sp => ReferenceEquals(sp.Ball, ball)))
                {
                    var dx = ball.X - spring.AnchorX;
                    var dy = ball.Y - spring.AnchorY;
                    var len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < 1e-6) continue;
                    var ux = dx / len;
                    var uy = dy / len;
                    var vRadial = ball.VX * ux + ball.VY * uy; // 远离锚点为正
                    var force = -spring.K * (len - spring.L0) - spring.Damping * vRadial;
                    ax += force * ux / Math.Max(0.01, ball.Mass);
                    ay += force * uy / Math.Max(0.01, ball.Mass);
                }
                ball.VX += ax * h;
                ball.VY += ay * h;
                ball.X += ball.VX * h;
                ball.Y += ball.VY * h;
                ResolveCollisions(ball);
            }
        }

        foreach (var ball in _balls)
        {
            ball.Trail.Add(new Point(ball.X, ball.Y));
            if (ball.Trail.Count > 160) ball.Trail.RemoveAt(0);
        }
    }

    private void ResolveCollisions(Ball ball)
    {
        foreach (var seg in _segments) ResolveCollision(ball, seg);
        foreach (var seg in _solidSegments) ResolveCollision(ball, seg);
    }

    private void ResolveCollision(Ball ball, Segment seg)
    {
        var (cx, cy) = ClosestPointOnSegment(ball.X, ball.Y, seg);
        var dx = ball.X - cx;
        var dy = ball.Y - cy;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist > ball.R) return;

        double nx, ny;
        if (dist > 1e-9)
        {
            nx = dx / dist;
            ny = dy / dist;
        }
        else
        {
            var sx = seg.X2 - seg.X1;
            var sy = seg.Y2 - seg.Y1;
            var len = Math.Sqrt(sx * sx + sy * sy);
            if (len < 1e-9) return;
            nx = -sy / len;
            ny = sx / len;
            if (ny < 0) { nx = -nx; ny = -ny; }
        }

        var penetration = ball.R - dist;
        ball.X += nx * penetration;
        ball.Y += ny * penetration;

        var vn = ball.VX * nx + ball.VY * ny;
        if (vn >= 0) return;
        var tx = ball.VX - vn * nx;
        var ty = ball.VY - vn * ny;
        var bounce = -vn * _restitution;
        var keep = 1 - _friction;
        ball.VX = tx * keep + nx * bounce;
        ball.VY = ty * keep + ny * bounce;

        if (Math.Abs(ball.VY) < 0.12 && ny > 0.9) ball.VY = 0;
    }

    private static (double X, double Y) ClosestPointOnSegment(double px, double py, Segment s)
    {
        var dx = s.X2 - s.X1;
        var dy = s.Y2 - s.Y1;
        var lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-12) return (s.X1, s.Y1);
        var t = Math.Clamp(((px - s.X1) * dx + (py - s.Y1) * dy) / lenSq, 0, 1);
        return (s.X1 + t * dx, s.Y1 + t * dy);
    }

    // ==================== 画布交互 ====================

    private (double X, double Y) ToWorld(Point p)
    {
        var scale = ViewScale;
        return (p.X / scale + _panX, (SimCanvas.ActualHeight - p.Y) / scale + _panY);
    }

    /// <summary>世界坐标 → 画布像素坐标（含缩放与平移）。</summary>
    private Point ToScreen(double wx, double wy)
    {
        var scale = ViewScale;
        return new Point((wx - _panX) * scale, SimCanvas.ActualHeight - (wy - _panY) * scale);
    }

    /// <summary>当前像素/米比例（含缩放）。</summary>
    private double ViewScale => SimCanvas.ActualWidth / WorldWidth * _zoom;

    /// <summary>当前可见世界高度（米）。</summary>
    private double WorldHeight => SimCanvas.ActualHeight / ViewScale;

    private void ApplyZoom(double zoom, string? message = null)
    {
        // 按钮缩放：以可视区中心为基准
        ZoomAt(zoom, new Point(SimCanvas.ActualWidth / 2, SimCanvas.ActualHeight / 2));
        if (message is not null) StatusText.Text = message;
    }

    /// <summary>以指定画布点为锚点缩放（该点下的世界坐标保持不动）。</summary>
    private void ZoomAt(double zoom, Point anchor)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var h = SimCanvas.ActualHeight;
        if (h < 40 || SimCanvas.ActualWidth < 40)
        {
            _zoom = zoom;
            return;
        }
        var oldScale = ViewScale;
        var wx = anchor.X / oldScale + _panX;
        var wy = (h - anchor.Y) / oldScale + _panY;

        _zoom = zoom;
        var newScale = ViewScale;
        _panX = wx - anchor.X / newScale;
        _panY = wy - (h - anchor.Y) / newScale;
        Render();
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ApplyZoom(_zoom * 1.25, $"已放大至 {_zoom * 100:0}%");

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ApplyZoom(_zoom / 1.25, $"已缩小至 {_zoom * 100:0}%");

    private void OnZoomResetClick(object sender, RoutedEventArgs e)
    {
        _panX = 0;
        _panY = 0;
        ApplyZoom(1.0, "缩放与平移已复位。");
    }

    private void OnSimWheel(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(SimCanvas);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;
        // 滚轮上下滑动 = 以鼠标位置为锚点缩放
        ZoomAt(delta > 0 ? _zoom * 1.1 : _zoom / 1.1, point.Position);
    }

    private async void OnSimPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_ready) return;
        var pt = e.GetCurrentPoint(SimCanvas);

        // 右键：命中固定水平面/木板时弹出确认框解锁；否则 = 平移可视区域
        if (pt.Properties.IsRightButtonPressed)
        {
            var hit = ToWorld(pt.Position);
            var plane = HitPlane(hit.X, hit.Y);
            var board = plane is null ? HitBoard(hit.X, hit.Y) : null;
            if (plane is not null || board is not null)
            {
                e.Handled = true;
                var kind = plane is not null ? "固定水平面" : "固定木板";
                if (await ConfirmUnlockAsync(kind))
                {
                    _unlockedPlane = plane;
                    _unlockedBoard = board;
                    StatusText.Text = $"已解锁{kind}：按住左键拖动即可移动，松开鼠标后自动重新固定。";
                    _logger.Information("已解锁固定物体：{Kind}", kind);
                    Render();
                }
                return;
            }

            _panning = true;
            _panStartPoint = pt.Position;
            _panStartX = _panX;
            _panStartY = _panY;
            SimCanvas.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        var world = ToWorld(pt.Position);

        if (_circuitMode)
        {
            // 连线：优先命中端子
            if (_wireMode)
            {
                var terminal = FindTerminal(pt.Position);
                if (terminal >= 0)
                {
                    if (_wireStartTerminal < 0)
                    {
                        _wireStartTerminal = terminal;
                        StatusText.Text = "已选择第一个端子，请点击第二个端子完成导线。";
                    }
                    else if (_wireStartTerminal == terminal)
                    {
                        _wireStartTerminal = -1;
                        StatusText.Text = "已取消连线。";
                    }
                    else
                    {
                        if (!_wires.Any(w => (w.TerminalA == _wireStartTerminal && w.TerminalB == terminal)
                                             || (w.TerminalA == terminal && w.TerminalB == _wireStartTerminal)))
                        {
                            _wires.Add(new CircuitWire { TerminalA = _wireStartTerminal, TerminalB = terminal });
                        }
                        StatusText.Text = "已建立导线。";
                        _wireStartTerminal = -1;
                    }
                    Render();
                    return;
                }
            }

            var element = ElementAt(world.X, world.Y);
            if (element is not null)
            {
                SelectElement(element);
                _dragElement = true;
                _dragOffsetX = world.X - element.X;
                _dragOffsetY = world.Y - element.Y;
                SimCanvas.CapturePointer(e.Pointer);
                Render();
            }
            else
            {
                _selectedElement = null;
                UpdateParamPanel();
                Render();
            }
            return;
        }

        // 运动模式：已解锁的固定物体 > 拖弹簧锚点 > 拖球 > 放置弹簧
        // 解锁是一次性的：命中后立即把解锁状态“消费”掉，松开鼠标即重新固定，避免长期保持可移动。
        var unlockedPlane = _unlockedPlane;
        var unlockedBoard = _unlockedBoard;
        if (unlockedPlane is not null && HitPlane(world.X, world.Y) == unlockedPlane)
        {
            _unlockedPlane = null;
            _unlockedBoard = null;
            _dragPlane = unlockedPlane;
            _solidGrabX = world.X - unlockedPlane.X1;
            _solidGrabY = world.Y - unlockedPlane.Y;
            SimCanvas.CapturePointer(e.Pointer);
            StatusText.Text = "拖动固定水平面中…（松开鼠标即重新固定）";
            return;
        }
        if (unlockedBoard is not null && HitBoard(world.X, world.Y) == unlockedBoard)
        {
            _unlockedPlane = null;
            _unlockedBoard = null;
            _dragBoard = unlockedBoard;
            _solidGrabX = world.X - unlockedBoard.X;
            _solidGrabY = world.Y - unlockedBoard.Y;
            SimCanvas.CapturePointer(e.Pointer);
            StatusText.Text = "拖动固定木板中…（松开鼠标即重新固定）";
            return;
        }
        if (unlockedPlane is not null || unlockedBoard is not null)
        {
            // 解锁后点了别处：取消解锁，需要重新右键确认
            _unlockedPlane = null;
            _unlockedBoard = null;
            StatusText.Text = "已取消解锁（如需移动固定物体请再次右键点击并确认）。";
            Render();
        }

        var springAnchor = _springs.FirstOrDefault(s => Dist(s.AnchorX, s.AnchorY, world.X, world.Y) < 0.35);
        if (springAnchor is not null)
        {
            _dragSpringAnchor = springAnchor;
            _selectedSpring = springAnchor;
            SimCanvas.CapturePointer(e.Pointer);
            Render();
            return;
        }

        var ball = _balls.FirstOrDefault(b => Dist(b.X, b.Y, world.X, world.Y) <= b.R + 0.15);
        if (ball is not null)
        {
            _dragBall = ball;
            SimCanvas.CapturePointer(e.Pointer);
            Render();
            return;
        }

        if (_springTool)
        {
            AddSpringAt(world.X, world.Y);
        }
    }

    private void OnSimPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_ready) return;
        var pt = e.GetCurrentPoint(SimCanvas);

        if (_panning)
        {
            var scale = Math.Max(1e-6, ViewScale);
            _panX = _panStartX - (pt.Position.X - _panStartPoint.X) / scale;
            _panY = _panStartY + (pt.Position.Y - _panStartPoint.Y) / scale;
            Render();
            return;
        }

        // 兜底：若 PointerReleased 没有送达（例如系统弹窗抢走焦点、指针在窗口外松开），
        // 这里检测左键是否仍按住，一旦松开立即结束拖动并重新固定。
        if ((_dragPlane is not null || _dragBoard is not null) && !pt.Properties.IsLeftButtonPressed)
        {
            EndSolidDrag();
            return;
        }

        var world = ToWorld(pt.Position);

        if (_circuitMode)
        {
            if (_dragElement && _selectedElement is not null)
            {
                _selectedElement.X = Math.Clamp(world.X - _dragOffsetX, 0.7, WorldWidth - 0.7);
                _selectedElement.Y = Math.Clamp(world.Y - _dragOffsetY, 0.3, WorldHeight - 0.3);
                Render();
            }
            return;
        }

        if (_dragPlane is not null)
        {
            var length = Math.Max(0.1, _dragPlane.X2 - _dragPlane.X1);
            var x1 = Math.Clamp(world.X - _solidGrabX, 0, Math.Max(0, WorldWidth - length));
            _dragPlane.X1 = x1;
            _dragPlane.X2 = x1 + length;
            _dragPlane.Y = Math.Clamp(world.Y - _solidGrabY, 0.05, 30);
            RebuildSolidSegments();
            Render();
            return;
        }

        if (_dragBoard is not null)
        {
            _dragBoard.X = Math.Clamp(world.X - _solidGrabX, 0, Math.Max(0, WorldWidth - _dragBoard.W));
            _dragBoard.Y = Math.Clamp(world.Y - _solidGrabY, 0, 30);
            RebuildSolidSegments();
            Render();
            return;
        }

        if (_dragSpringAnchor is not null)
        {
            _dragSpringAnchor.AnchorX = world.X;
            _dragSpringAnchor.AnchorY = world.Y;
            Render();
            return;
        }

        if (_dragBall is not null)
        {
            _dragBall.X = Math.Max(_dragBall.R, world.X);
            _dragBall.Y = Math.Max(_dragBall.R, world.Y);
            Render();
        }
    }

    private void OnSimPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _panning = false;
        _dragElement = false;
        _dragSpringAnchor = null;
        _dragBall = null;
        SimCanvas.ReleasePointerCapture(e.Pointer);
        EndSolidDrag();
        UpdateParamPanel();
        Render();
    }

    private void OnSimPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _panning = false;
        _dragElement = false;
        _dragSpringAnchor = null;
        _dragBall = null;
        EndSolidDrag();
    }

    /// <summary>
    /// 结束固定物体拖动并立即重新固定（松手即停，不允许长期保持“可移动”状态）。
    /// 同时清空一次性解锁标记：下一次移动必须重新右键确认。
    /// </summary>
    private void EndSolidDrag()
    {
        // 只有真的在拖动时才清理解锁状态：右键弹窗确认后右键抬起也会走到这里，
        // 若此时清空解锁标记，用户就无法再用左键拖动。
        if (_dragPlane is null && _dragBoard is null) return;
        _dragPlane = null;
        _dragBoard = null;
        _unlockedPlane = null;
        _unlockedBoard = null;
        StatusText.Text = "已结束拖动，物体重新固定（如需再次移动请右键点击并确认）。";
        Render();
    }

    // ==================== 固定物体（水平面 / 木板） ====================

    /// <summary>命中固定水平面（容差按屏幕像素换算到世界坐标）。</summary>
    private Plane? HitPlane(double wx, double wy)
    {
        var tol = 12 / Math.Max(1e-6, ViewScale);
        return _planes.FirstOrDefault(p =>
            wx >= p.X1 - tol && wx <= p.X2 + tol && Math.Abs(wy - p.Y) <= tol);
    }

    /// <summary>命中固定木板矩形。</summary>
    private Board? HitBoard(double wx, double wy)
    {
        var tol = 10 / Math.Max(1e-6, ViewScale);
        return _boards.FirstOrDefault(b =>
            wx >= b.X - tol && wx <= b.X + b.W + tol &&
            wy >= b.Y - tol && wy <= b.Y + b.H + tol);
    }

    /// <summary>右键点中固定物体后的解锁确认框：确认后可用左键拖动它。</summary>
    private async Task<bool> ConfirmUnlockAsync(string kind)
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = SimCanvas.XamlRoot,
                Title = "固定物体",
                Content = $"“{kind}”默认固定不动。是否解锁并允许移动？\n\n解锁后：左键按住它即可拖动，松开结束。",
                PrimaryButtonText = "解锁并移动",
                CloseButtonText = "保持固定",
                DefaultButton = ContentDialogButton.Primary,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "固定物体确认框显示失败");
            return false;
        }
    }

    // ==================== 电路元件操作 ====================

    private void OnAddCircuitElementClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (!Enum.TryParse<CircuitKind>(button.Tag as string, out var kind)) return;

        var heightMeters = SimCanvas.ActualWidth > 1 && SimCanvas.ActualHeight > 1 ? WorldHeight : 6;
        var index = _elements.Count;
        var element = new CircuitElement
        {
            Id = _nextElementId++,
            Kind = kind,
            X = Math.Clamp(2 + (index % 4) * 1.9, 1, WorldWidth - 1),
            Y = Math.Max(1.2, heightMeters - 1.2 - (index / 4) * 1.4),
        };
        element.Label = kind switch
        {
            CircuitKind.Battery => "E",
            CircuitKind.Resistor => "R",
            CircuitKind.Capacitor => "C",
            CircuitKind.Inductor => "L",
            CircuitKind.Switch => "S",
            CircuitKind.Ammeter => "A",
            CircuitKind.Voltmeter => "V",
            _ => "X",
        } + element.Id;
        // 电表按理想表默认内阻：电流表极小、电压表极大
        if (kind == CircuitKind.Ammeter) element.Resistance = 0.001;
        if (kind == CircuitKind.Voltmeter) element.Resistance = 1e6;
        _elements.Add(element);
        SelectElement(element);
        Render();
    }

    private void OnWireModeClick(object sender, RoutedEventArgs e)
    {
        _wireMode = !_wireMode;
        _wireStartTerminal = -1;
        WireButton.Content = _wireMode ? "连线（开）" : "连线";
        StatusText.Text = _wireMode
            ? "连线模式：依次点击两个元件端子（小圆点）即可建立导线。"
            : "已退出连线模式。";
        Render();
    }

    private void OnDeleteCircuitElementClick(object sender, RoutedEventArgs e)
    {
        if (_selectedElement is null)
        {
            StatusText.Text = "请先点选要删除的元件。";
            return;
        }
        var id = _selectedElement.Id;
        _elements.Remove(_selectedElement);
        _wires.RemoveAll(w => w.TerminalA / 2 == id || w.TerminalB / 2 == id);
        _selectedElement = null;
        UpdateParamPanel();
        Render();
    }

    private void OnClearCircuitClick(object sender, RoutedEventArgs e)
    {
        _elements.Clear();
        _wires.Clear();
        _selectedElement = null;
        _nextElementId = 1;
        _transientTimer.Stop();
        UpdateParamPanel();
        CircuitResultText.Text = "电路已清空。";
        Render();
    }

    private void SelectElement(CircuitElement element)
    {
        _selectedElement = element;
        UpdateParamPanel();
    }

    private void UpdateParamPanel()
    {
        _updatingParams = true;
        try
        {
            var e = _selectedElement;
            SelectedElementText.Text = e is null
                ? "未选中元件（点击画布中的元件可编辑参数）"
                : $"{CircuitSolver.KindName(e.Kind)} {e.Label}：{CircuitSolver.Describe(e)}";

            EmfBox.Visibility = e?.Kind == CircuitKind.Battery ? Visibility.Visible : Visibility.Collapsed;
            InternalRBox.Visibility = e?.Kind == CircuitKind.Battery ? Visibility.Visible : Visibility.Collapsed;
            ResistanceBox.Visibility = e?.Kind is CircuitKind.Resistor or CircuitKind.Ammeter or CircuitKind.Voltmeter
                ? Visibility.Visible
                : Visibility.Collapsed;
            CapacitanceBox.Visibility = e?.Kind == CircuitKind.Capacitor ? Visibility.Visible : Visibility.Collapsed;
            InductanceBox.Visibility = e?.Kind == CircuitKind.Inductor ? Visibility.Visible : Visibility.Collapsed;
            SwitchClosedToggle.Visibility = e?.Kind == CircuitKind.Switch ? Visibility.Visible : Visibility.Collapsed;

            if (e is null) return;
            EmfBox.Text = Format(e.Emf);
            InternalRBox.Text = Format(e.InternalResistance);
            ResistanceBox.Text = Format(e.Resistance);
            CapacitanceBox.Text = Format(e.Capacitance);
            InductanceBox.Text = Format(e.Inductance);
            SwitchClosedToggle.IsOn = e.Closed;
        }
        finally
        {
            _updatingParams = false;
        }
    }

    private void OnCircuitParamChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingParams || !_ready || _selectedElement is null) return;
        var element = _selectedElement;
        element.Emf = ParseDouble(EmfBox.Text, element.Emf);
        element.InternalResistance = Math.Max(0, ParseDouble(InternalRBox.Text, element.InternalResistance));
        element.Resistance = Math.Max(1e-6, ParseDouble(ResistanceBox.Text, element.Resistance));
        element.Capacitance = Math.Max(1e-12, ParseDouble(CapacitanceBox.Text, element.Capacitance));
        element.Inductance = Math.Max(1e-9, ParseDouble(InductanceBox.Text, element.Inductance));
        SelectedElementText.Text = $"{CircuitSolver.KindName(element.Kind)} {element.Label}：{CircuitSolver.Describe(element)}";
        Render();
    }

    private void OnSwitchToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingParams || !_ready || _selectedElement is null) return;
        _selectedElement.Closed = SwitchClosedToggle.IsOn;
        SelectedElementText.Text = $"{CircuitSolver.KindName(_selectedElement.Kind)} {_selectedElement.Label}：{CircuitSolver.Describe(_selectedElement)}";
        Render();
    }

    private CircuitElement? ElementAt(double wx, double wy)
    {
        return _elements.LastOrDefault(el =>
            Math.Abs(wx - el.X) <= ElementHalfLength + 0.1 && Math.Abs(wy - el.Y) <= 0.4);
    }

    private int FindTerminal(Point canvasPoint)
    {
        foreach (var el in _elements)
        {
            for (var side = 0; side < 2; side++)
            {
                var p = ToScreen(el.X + (side == 0 ? -ElementHalfLength : ElementHalfLength), el.Y);
                var dx = canvasPoint.X - p.X;
                var dy = canvasPoint.Y - p.Y;
                if (dx * dx + dy * dy <= 14 * 14) return el.Id * 2 + side;
            }
        }
        return -1;
    }

    // ==================== 电路求解 ====================

    private void OnSolveDcClick(object sender, RoutedEventArgs e)
    {
        _transientTimer.Stop();
        ResetCircuitState();
        var result = CircuitSolver.Solve(_elements, _wires, 0);
        CircuitResultText.Text = BuildResultText(result, 0);
        Render();
    }

    private void OnRunTransientClick(object sender, RoutedEventArgs e)
    {
        if (_elements.Count == 0)
        {
            CircuitResultText.Text = "电路为空。";
            return;
        }
        _transientTimer.Start();
        StatusText.Text = "暂态仿真运行中（步长 1 ms）…";
    }

    private void OnPauseTransientClick(object sender, RoutedEventArgs e)
    {
        _transientTimer.Stop();
        StatusText.Text = "暂态仿真已暂停。";
    }

    private void OnResetCircuitStateClick(object sender, RoutedEventArgs e)
    {
        ResetCircuitState();
        CircuitResultText.Text = "电容电压 / 电感电流已清零。";
        Render();
    }

    private void ResetCircuitState()
    {
        _transientTime = 0;
        foreach (var el in _elements)
        {
            el.CapacitorVoltage = 0;
            el.InductorCurrent = 0;
        }
    }

    private void RunTransientSteps()
    {
        const int stepsPerTick = 10;
        CircuitSolveResult? result = null;
        for (var i = 0; i < stepsPerTick; i++)
        {
            _transientTime += TransientDt;
            result = CircuitSolver.Solve(_elements, _wires, TransientDt);
            if (result is not { Ok: true }) break;
        }
        if (result is not null) CircuitResultText.Text = BuildResultText(result, _transientTime);
        Render();
    }

    private string BuildResultText(CircuitSolveResult result, double time)
    {
        if (!result.Ok) return result.Message;
        var sb = new StringBuilder();
        sb.AppendLine(result.Message);
        if (time > 0) sb.AppendLine($"仿真时刻 t = {time * 1000:0} ms");
        sb.AppendLine();
        foreach (var el in _elements)
        {
            result.ElementVoltages.TryGetValue(el.Id, out var v);
            result.ElementCurrents.TryGetValue(el.Id, out var i);
            sb.AppendLine($"{el.Label,-4} {CircuitSolver.KindName(el.Kind),-3} U = {Format(v)} V   I = {Format(i)} A"
                          + (el.Kind == CircuitKind.Switch ? $"   {(el.Closed ? "闭合" : "断开")}" : string.Empty));
            if (el.Kind == CircuitKind.Capacitor)
                sb.AppendLine($"      电容电压 Uc = {Format(el.CapacitorVoltage)} V");
            if (el.Kind == CircuitKind.Inductor)
                sb.AppendLine($"      电感电流 IL = {Format(el.InductorCurrent)} A");
        }
        var power = _elements
            .Where(x => x.Kind is CircuitKind.Resistor or CircuitKind.Capacitor or CircuitKind.Inductor)
            .Select(x =>
            {
                result.ElementVoltages.TryGetValue(x.Id, out var v);
                result.ElementCurrents.TryGetValue(x.Id, out var i);
                return Math.Abs(v * i);
            })
            .Sum();
        sb.AppendLine();
        sb.Append($"元件总耗散功率 ≈ {Format(power)} W");
        return sb.ToString();
    }

    // ==================== 渲染 ====================

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => Render();

    private void Render()
    {
        var w = SimCanvas.ActualWidth;
        var h = SimCanvas.ActualHeight;
        if (w < 40 || h < 40) return;

        var scale = ViewScale;
        SimCanvas.Children.Clear();

        var gridBrush = new SolidColorBrush(Color.FromArgb(255, 228, 234, 240));
        var axisBrush = new SolidColorBrush(Color.FromArgb(255, 168, 184, 196));

        // 网格（每格 1 m）随平移一起移动
        var firstX = (int)Math.Floor(_panX);
        for (var px = (firstX - _panX) * scale; px <= w + 0.5; px += scale)
        {
            SimCanvas.Children.Add(new Line { X1 = px, X2 = px, Y1 = 0, Y2 = h, Stroke = gridBrush, StrokeThickness = 1 });
        }
        var firstY = (int)Math.Floor(_panY);
        for (var py = h - (firstY - _panY) * scale; py >= -0.5; py -= scale)
        {
            SimCanvas.Children.Add(new Line { X1 = 0, X2 = w, Y1 = py, Y2 = py, Stroke = gridBrush, StrokeThickness = 1 });
        }

        // 世界坐标轴：x = 0 竖线、y = 0 横线（滚出视口时不绘制）
        var originX = (0 - _panX) * scale;
        var originY = h - (0 - _panY) * scale;
        if (originX >= 0 && originX <= w)
        {
            SimCanvas.Children.Add(new Line { X1 = originX, X2 = originX, Y1 = 0, Y2 = h, Stroke = axisBrush, StrokeThickness = 1.5 });
        }
        if (originY >= 0 && originY <= h)
        {
            SimCanvas.Children.Add(new Line { X1 = 0, X2 = w, Y1 = originY, Y2 = originY, Stroke = axisBrush, StrokeThickness = 1.5 });
        }

        if (_circuitMode) RenderCircuit(scale, h);
        else RenderMotion(scale, h);
    }

    private void RenderMotion(double scale, double h)
    {
        var segBrush = new SolidColorBrush(Color.FromArgb(255, 55, 71, 79));
        var ballBrush = new SolidColorBrush(Color.FromArgb(255, 0, 151, 167));
        var ballEdgeBrush = new SolidColorBrush(Color.FromArgb(255, 0, 105, 120));
        var trailBrush = new SolidColorBrush(Color.FromArgb(70, 0, 151, 167));
        var anchorBrush = new SolidColorBrush(Color.FromArgb(255, 96, 125, 139));

        foreach (var s in _segments)
        {
            if (s.Y2 - s.Y1 > 15) continue;
            var a = ToScreen(s.X1, s.Y1);
            var b = ToScreen(s.X2, s.Y2);
            SimCanvas.Children.Add(new Line
            {
                X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y,
                Stroke = segBrush, StrokeThickness = 3,
            });
        }

        DrawSolidBodies();
        foreach (var spring in _springs) DrawSpring(spring, scale, h, anchorBrush);

        foreach (var ball in _balls)
        {
            if (ball.Trail.Count > 1)
            {
                var trail = new Polyline { Stroke = trailBrush, StrokeThickness = 2 };
                foreach (var p in ball.Trail) trail.Points.Add(ToScreen(p.X, p.Y));
                SimCanvas.Children.Add(trail);
            }

            var ellipse = new Ellipse
            {
                Width = ball.R * 2 * scale,
                Height = ball.R * 2 * scale,
                Fill = ballBrush,
                Stroke = ballEdgeBrush,
                StrokeThickness = 1,
            };
            var c = ToScreen(ball.X, ball.Y);
            Canvas.SetLeft(ellipse, c.X - ball.R * scale);
            Canvas.SetTop(ellipse, c.Y - ball.R * scale);
            SimCanvas.Children.Add(ellipse);
        }
    }

    /// <summary>绘制固定水平面与固定木板（浅色背景下的静态支撑/受力元素，不可拖动）。</summary>
    private void DrawSolidBodies()
    {
        var planeFill = new SolidColorBrush(Color.FromArgb(255, 207, 216, 220));
        var planeStroke = new SolidColorBrush(Color.FromArgb(255, 84, 110, 122));
        foreach (var plane in _planes)
        {
            var a = ToScreen(plane.X1, plane.Y);
            var b = ToScreen(plane.X2, plane.Y);
            var rect = new Rectangle
            {
                Width = Math.Max(1, b.X - a.X),
                Height = 8,
                Fill = planeFill,
                Stroke = planeStroke,
                StrokeThickness = 1.5,
                RadiusX = 2,
                RadiusY = 2,
            };
            Canvas.SetLeft(rect, a.X);
            Canvas.SetTop(rect, a.Y - 4);
            SimCanvas.Children.Add(rect);

            var label = new TextBlock
            {
                Text = "固定水平面",
                FontSize = 11,
                Foreground = planeStroke,
            };
            Canvas.SetLeft(label, a.X + 4);
            Canvas.SetTop(label, a.Y - 22);
            SimCanvas.Children.Add(label);
        }

        var boardFill = new SolidColorBrush(Color.FromArgb(255, 215, 204, 200));
        var boardStroke = new SolidColorBrush(Color.FromArgb(255, 141, 110, 99));
        foreach (var board in _boards)
        {
            var topLeft = ToScreen(board.X, board.Y + board.H);
            var bottomRight = ToScreen(board.X + board.W, board.Y);
            var rect = new Rectangle
            {
                Width = Math.Max(2, bottomRight.X - topLeft.X),
                Height = Math.Max(2, bottomRight.Y - topLeft.Y),
                Fill = boardFill,
                Stroke = boardStroke,
                StrokeThickness = 2,
                RadiusX = 3,
                RadiusY = 3,
            };
            Canvas.SetLeft(rect, topLeft.X);
            Canvas.SetTop(rect, topLeft.Y);
            SimCanvas.Children.Add(rect);

            var label = new TextBlock
            {
                Text = "固定木板",
                FontSize = 11,
                Foreground = boardStroke,
            };
            Canvas.SetLeft(label, topLeft.X + 4);
            Canvas.SetTop(label, topLeft.Y - 18);
            SimCanvas.Children.Add(label);
        }
    }

    private void DrawSpring(Spring spring, double scale, double h, Brush anchorBrush)
    {
        var anchorPoint = ToScreen(spring.AnchorX, spring.AnchorY);
        var ballPoint = ToScreen(spring.Ball.X, spring.Ball.Y);
        var x1 = anchorPoint.X;
        var y1 = anchorPoint.Y;
        var x2 = ballPoint.X;
        var y2 = ballPoint.Y;
        var dx = x2 - x1;
        var dy = y2 - y1;
        var len = Math.Sqrt(dx * dx + dy * dy);
        var selected = ReferenceEquals(spring, _selectedSpring);

        var coil = new Polyline
        {
            Stroke = new SolidColorBrush(selected
                ? Color.FromArgb(255, 245, 124, 0)
                : Color.FromArgb(255, 120, 144, 156)),
            StrokeThickness = selected ? 2.6 : 1.8,
        };
        var segments = 18;
        var amplitude = 5.0;
        var nx = len > 1e-6 ? -dy / len : 1;
        var ny = len > 1e-6 ? dx / len : 0;
        for (var i = 0; i <= segments; i++)
        {
            var t = (double)i / segments;
            var offset = i == 0 || i == segments ? 0 : amplitude * Math.Sin(t * Math.PI * 6);
            coil.Points.Add(new Point(x1 + dx * t + nx * offset, y1 + dy * t + ny * offset));
        }
        SimCanvas.Children.Add(coil);

        var anchor = new Ellipse { Width = 10, Height = 10, Fill = anchorBrush };
        Canvas.SetLeft(anchor, x1 - 5);
        Canvas.SetTop(anchor, y1 - 5);
        SimCanvas.Children.Add(anchor);

        var label = new TextBlock
        {
            Text = $"k={Format(spring.K)}",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 84, 110, 122)),
        };
        Canvas.SetLeft(label, (x1 + x2) / 2 - 22);
        Canvas.SetTop(label, (y1 + y2) / 2 - 18);
        SimCanvas.Children.Add(label);
    }

    private void RenderCircuit(double scale, double h)
    {
        var wireBrush = new SolidColorBrush(Color.FromArgb(255, 69, 90, 100));

        // 导线
        foreach (var wire in _wires)
        {
            var a = TerminalPosition(wire.TerminalA, scale, h);
            var b = TerminalPosition(wire.TerminalB, scale, h);
            SimCanvas.Children.Add(new Line
            {
                X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y,
                Stroke = wireBrush, StrokeThickness = 2,
            });
        }

        foreach (var el in _elements) DrawCircuitElement(el, scale, h);
    }

    private Point TerminalPosition(int terminal, double scale, double h)
    {
        var id = terminal / 2;
        var side = terminal % 2;
        var el = _elements.FirstOrDefault(x => x.Id == id);
        if (el is null) return new Point(0, 0);
        var wx = el.X + (side == 0 ? -ElementHalfLength : ElementHalfLength);
        return ToScreen(wx, el.Y);
    }

    private void DrawCircuitElement(CircuitElement el, double scale, double h)
    {
        var center = ToScreen(el.X, el.Y);
        var cx = center.X;
        var cy = center.Y;
        var half = ElementHalfLength * scale;
        var selected = ReferenceEquals(el, _selectedElement);
        var brush = new SolidColorBrush(selected
            ? Color.FromArgb(255, 2, 119, 189)
            : Color.FromArgb(255, 38, 50, 56));
        const double thickness = 2.4;

        // 引线
        SimCanvas.Children.Add(new Line { X1 = cx - half, X2 = cx - half * 0.45, Y1 = cy, Y2 = cy, Stroke = brush, StrokeThickness = thickness });
        SimCanvas.Children.Add(new Line { X1 = cx + half * 0.45, X2 = cx + half, Y1 = cy, Y2 = cy, Stroke = brush, StrokeThickness = thickness });

        switch (el.Kind)
        {
            case CircuitKind.Resistor:
            {
                var box = new Rectangle
                {
                    Width = half * 0.9, Height = 18,
                    Stroke = brush, StrokeThickness = thickness, RadiusX = 3, RadiusY = 3,
                };
                Canvas.SetLeft(box, cx - half * 0.45);
                Canvas.SetTop(box, cy - 9);
                SimCanvas.Children.Add(box);
                break;
            }
            case CircuitKind.Battery:
            {
                var shortPlate = new Line { X1 = cx - 6, X2 = cx - 6, Y1 = cy - 12, Y2 = cy + 12, Stroke = brush, StrokeThickness = 3 };
                var longPlate = new Line { X1 = cx + 6, X2 = cx + 6, Y1 = cy - 20, Y2 = cy + 20, Stroke = brush, StrokeThickness = 3 };
                SimCanvas.Children.Add(shortPlate);
                SimCanvas.Children.Add(longPlate);
                break;
            }
            case CircuitKind.Capacitor:
            {
                SimCanvas.Children.Add(new Line { X1 = cx - 5, X2 = cx - 5, Y1 = cy - 16, Y2 = cy + 16, Stroke = brush, StrokeThickness = 3 });
                SimCanvas.Children.Add(new Line { X1 = cx + 5, X2 = cx + 5, Y1 = cy - 16, Y2 = cy + 16, Stroke = brush, StrokeThickness = 3 });
                break;
            }
            case CircuitKind.Inductor:
            {
                var coil = new Polyline { Stroke = brush, StrokeThickness = thickness };
                var count = 4;
                for (var i = 0; i < count; i++)
                {
                    var x0 = cx - half * 0.45 + i * (half * 0.9 / count);
                    for (var s = 0; s <= 8; s++)
                    {
                        var t = (double)s / 8;
                        coil.Points.Add(new Point(x0 + half * 0.9 / count * t, cy - 10 * Math.Sin(t * Math.PI)));
                    }
                }
                SimCanvas.Children.Add(coil);
                break;
            }
            case CircuitKind.Switch:
            {
                var left = new Line { X1 = cx - half * 0.45, X2 = cx - 6, Y1 = cy, Y2 = cy, Stroke = brush, StrokeThickness = thickness };
                var right = new Line { X1 = cx + 6, X2 = cx + half * 0.45, Y1 = cy, Y2 = cy, Stroke = brush, StrokeThickness = thickness };
                SimCanvas.Children.Add(left);
                SimCanvas.Children.Add(right);
                SimCanvas.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = brush });
                Canvas.SetLeft(SimCanvas.Children[^1] as Ellipse, cx - 9);
                Canvas.SetTop(SimCanvas.Children[^1] as Ellipse, cy - 3.5);
                SimCanvas.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = brush });
                Canvas.SetLeft(SimCanvas.Children[^1] as Ellipse, cx + 5);
                Canvas.SetTop(SimCanvas.Children[^1] as Ellipse, cy - 3.5);
                if (el.Closed)
                {
                    SimCanvas.Children.Add(new Line { X1 = cx - 6, X2 = cx + 6, Y1 = cy, Y2 = cy, Stroke = brush, StrokeThickness = thickness });
                }
                else
                {
                    SimCanvas.Children.Add(new Line { X1 = cx - 6, X2 = cx + 7, Y1 = cy, Y2 = cy - 12, Stroke = brush, StrokeThickness = thickness });
                }
                break;
            }
            case CircuitKind.Ammeter:
            case CircuitKind.Voltmeter:
            {
                var radius = half * 0.5;
                var circle = new Ellipse
                {
                    Width = radius * 2, Height = radius * 2,
                    Stroke = brush, StrokeThickness = thickness,
                };
                Canvas.SetLeft(circle, cx - radius);
                Canvas.SetTop(circle, cy - radius);
                SimCanvas.Children.Add(circle);
                var letter = new TextBlock
                {
                    Text = el.Kind == CircuitKind.Ammeter ? "A" : "V",
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = brush,
                    Width = radius * 2,
                    TextAlignment = TextAlignment.Center,
                };
                Canvas.SetLeft(letter, cx - radius);
                Canvas.SetTop(letter, cy - 10);
                SimCanvas.Children.Add(letter);
                break;
            }
        }
        foreach (var side in new[] { -1, 1 })
        {
            var tx = cx + side * half;
            var terminalId = el.Id * 2 + (side < 0 ? 0 : 1);
            var isStart = _wireStartTerminal == terminalId;
            var dot = new Ellipse
            {
                Width = 11, Height = 11,
                Fill = new SolidColorBrush(isStart ? Color.FromArgb(255, 245, 124, 0) : Color.FromArgb(255, 255, 255, 255)),
                Stroke = new SolidColorBrush(Color.FromArgb(255, 120, 144, 156)),
                StrokeThickness = 1.6,
            };
            Canvas.SetLeft(dot, tx - 5.5);
            Canvas.SetTop(dot, cy - 5.5);
            SimCanvas.Children.Add(dot);
        }

        var label = new TextBlock
        {
            Text = el.Label,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = brush,
        };
        Canvas.SetLeft(label, cx - 10);
        Canvas.SetTop(label, cy - 42);
        SimCanvas.Children.Add(label);

        var value = new TextBlock
        {
            Text = CircuitSolver.Describe(el),
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 96, 125, 139)),
        };
        Canvas.SetLeft(value, cx - 34);
        Canvas.SetTop(value, cy + 20);
        SimCanvas.Children.Add(value);
    }

    // ==================== 辅助 ====================

    private static double Dist(double x1, double y1, double x2, double y2) =>
        Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));

    private static double ParseDouble(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string Format(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private sealed class Ball
    {
        public double X, Y, VX, VY, R, Mass = 0.5;
        public List<Point> Trail { get; } = new();
    }

    private sealed class Spring
    {
        public double AnchorX, AnchorY;
        public Ball Ball = new();
        public double K = 20;
        public double L0 = 1.5;
        public double Damping = 0.2;
    }

    private sealed class Segment
    {
        public double X1, Y1, X2, Y2;
    }

    /// <summary>固定水平面（静力学元素：只提供支撑面，不可拖动）。</summary>
    private sealed class Plane
    {
        public double X1, X2, Y;
    }

    /// <summary>固定木板（刚性矩形，四条边参与碰撞，不可拖动）。</summary>
    private sealed class Board
    {
        public double X, Y, W, H;
    }

    private sealed class SceneDto
    {
        public double Gravity { get; set; } = 9.8;
        public double Restitution { get; set; } = 0.65;
        public double Friction { get; set; } = 0.03;
        public List<BallDto> Balls { get; set; } = new();
        public List<SegmentDto> Segments { get; set; } = new();
        public List<PlaneDto> Planes { get; set; } = new();
        public List<BoardDto> Boards { get; set; } = new();
    }

    private sealed class BallDto
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double VX { get; set; }
        public double VY { get; set; }
        public double R { get; set; } = 0.3;
        public double Mass { get; set; } = 0.5;
    }

    private sealed class SegmentDto
    {
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
    }

    private sealed class PlaneDto
    {
        public double X1 { get; set; }
        public double X2 { get; set; }
        public double Y { get; set; }
    }

    private sealed class BoardDto
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; } = 2.4;
        public double H { get; set; } = 0.35;
    }
}
