using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ProcessPanel;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    // 主列表：程序级聚合卡片（同一 exe 路径只出现一个主项）
    public ObservableCollection<AppGroup> Groups { get; } = new();

    private string _statusText = "就绪";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnChanged(nameof(StatusText)); }
    }

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        set { _isScanning = value; OnChanged(nameof(IsScanning)); }
    }

    private DispatcherTimer? _timer;

    // 友好名缓存：按 exe 路径缓存，避免每 10 秒重读文件版本信息
    private static readonly ConcurrentDictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SystemCritical = new(StringComparer.OrdinalIgnoreCase)
    {
        "system idle process", "system", "registry", "smss.exe", "csrss.exe", "wininit.exe",
        "services.exe", "lsass.exe", "svchost.exe", "winlogon.exe", "dwm.exe", "explorer.exe",
        "taskmgr.exe", "fontdrvhost.exe", "dllhost.exe", "wmiprvse.exe", "searchindexer.exe",
        "searchhost.exe", "runtimebroker.exe", "startmenuexperiencehost.exe",
        "shellexperiencehost.exe", "textinputhost.exe", "ctfmon.exe", "securityhealthsystray.exe",
        "wudfhost.exe", "conhost.exe", "spoolsv.exe", "mpdefendercoreservice.exe",
        "searchprotocolhost.exe", "searchfilterhost.exe", "backgroundtaskhost.exe",
        "applicationframehost.exe", "settingssynchost.exe", "lockapp.exe", "logonui.exe",
    };

    private static readonly string[] SystemDirs =
    {
        @"\windows\system32", @"\windows\syswow64", @"\windows\winsxs",
        @"\windows\immersivetaskmanager", @"\windows\shell",
        @"\windows\immersivecontrolpanel", @"\windows\servicing",
        @"\windows\defender", @"\windows\securityhealthservice",
    };

    private static readonly HashSet<string> HighRisk = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe",
        "code.exe", "cursor.exe", "sublime_text.exe", "notepad.exe", "notepad++.exe",
        "wechat.exe", "qq.exe", "tim.exe", "discord.exe", "telegram.exe",
        "thunderbird.exe", "outlook.exe", "winword.exe", "excel.exe", "powerpnt.exe",
        "obs64.exe", "obs.exe", "steam.exe", "epicgameslauncher.exe",
        "battle.net.exe", "leagueclient.exe", "valorant.exe",
        "photoshop.exe", "illustrator.exe", "premiere pro.exe", "afterfx.exe",
        "blender.exe", "maya.exe", "3dsmax.exe", "workbuddy.exe",
        "android studio.exe", "idea64.exe", "pycharm64.exe", "webstorm64.exe",
    };

    private static readonly int MyPid = Process.GetCurrentProcess().Id;
    private static readonly string MyPath = Process.GetCurrentProcess().MainModule?.FileName ?? "";

    private static readonly string LayoutFile =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "panel_layout.ini");
    private int _cfgX, _cfgY;
    private bool _movable;

    // ===== 统一扫描调度 =====
    private readonly object _scanGate = new();
    private bool _scanRunning;
    private bool _manualPending;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    // ===== 点击展开 / 悬停视觉 / 扫光序列 =====
    private AppGroup? _expandedGroup;   // 当前展开的程序（仅由点击设置）
    private int _shineVersion;          // 递增即取消尚未播放的扫光
    private DispatcherTimer? _leaveTimer;

    // ===== 材质主题 / 环境反射（不依赖鼠标） =====
    private bool _isMetal = true;                  // 默认深色金属（ini 可覆盖）
    private bool _topMost;                         // 置顶常驻（ini 可覆盖）
    private double _metalAlpha = 0.80;             // 金属底不透明度（唯一可调项，ini 可覆盖）
    private DispatcherTimer? _ambientTimer;        // 反射低频漂移（约 8 次/秒，非逐帧）
    private readonly Stopwatch _ambientClock = Stopwatch.StartNew();

    // 悬停：只取消宽限计时，绝不触发展开
    private void Group_MouseEnter(object sender, MouseEventArgs e)
    {
        _leaveTimer?.Stop();
    }

    // 点击：展开 / 收起（同一程序再点收起；点另一个则切换）
    private void Group_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;                          // 子项按钮等已处理，不再冒泡为展开
        if (((FrameworkElement)sender).DataContext is not AppGroup g) return;

        e.Handled = true;

        if (ReferenceEquals(_expandedGroup, g) && g.IsExpanded)
        {
            Collapse(g);                                 // 再次点击同一程序 → 收起
            return;
        }

        if (_expandedGroup != null) Collapse(_expandedGroup);   // 切换：先彻底清理旧程序
        _expandedGroup = g;
        g.IsExpanded = true;
        PlayShine(g);
        EnsureVisible(g);
    }

    // 离开整个卡片（主项与子项同属一个 Border）→ 快速收起
    private void Group_MouseLeave(object sender, MouseEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AppGroup g) return;
        if (!ReferenceEquals(_expandedGroup, g)) return;

        _leaveTimer?.Stop();
        _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _leaveTimer.Tick += (_, _) =>
        {
            _leaveTimer?.Stop();
            if (ReferenceEquals(_expandedGroup, g)) Collapse(g);
        };
        _leaveTimer.Start();
    }

    // 统一收起：立即取消动画、清空填充与彩色边框、隐藏按钮
    private void Collapse(AppGroup g)
    {
        _shineVersion++;                   // 作废本轮扫光序列
        _leaveTimer?.Stop();
        g.IsExpanded = false;
        g.Confirming = false;
        foreach (var c in g.Children)
        {
            c.IsShining = false;           // 移除光带动画时钟
            c.IsLit = false;               // 清空填充色与彩色边框
            c.Confirming = false;
        }
        if (ReferenceEquals(_expandedGroup, g)) _expandedGroup = null;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LoadLayout();
        if (_cfgX != 0 && _cfgY != 0)
        {
            Left = _cfgX;
            Top = _cfgY;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            const int offsetRight = 340;
            Left = wa.Right - Width - offsetRight;
            Top = wa.Bottom - Height;
        }

        ApplyZOrder();
        if (!_movable) Native.MakeNonActivating(this);
        Activated += (_, _) => { if (!_topMost) Native.SendToBottom(this); };

        TiltRoot.Cursor = _movable ? Cursors.SizeAll : Cursors.Arrow;
        TiltRoot.MouseLeftButtonDown += (_, _) => { if (_movable) DragMove(); };
        LocationChanged += (_, _) => { if (_movable) SaveLayout(); };

        // ===== 材质与交互初始化 =====
        NoiseLayer.Fill = MetalMaterial.NoiseBrush;
        ApplyTheme(_isMetal, persist: true);   // 首次启动补写 theme 字段，保证 ini 完整

        RequestScan(false);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) => RequestScan(false);
        _timer.Start();

        // --preview：启动时置顶 8 秒让用户看到位置；--topmost N：置顶 N 秒（调试/定位用）
        var cliArgs = Environment.GetCommandLineArgs();
        int topSec = 0;
        int ti = Array.FindIndex(cliArgs, a => a.Equals("--topmost", StringComparison.OrdinalIgnoreCase));
        if (ti >= 0 && ti + 1 < cliArgs.Length && int.TryParse(cliArgs[ti + 1], out var n) && n > 0)
            topSec = n;
        else if (cliArgs.Any(a => a.Equals("--preview", StringComparison.OrdinalIgnoreCase)))
            topSec = 8;

        if (topSec > 0) Native.PreviewToTopThenBottom(this, topSec);

        // 监听「重复启动」信号：被单实例拦下时，本窗口置顶 3 秒提示用户它一直在运行
        StartShowListener();

        TopMostMenu.IsChecked = _topMost;
        UpdateAlphaChecks();
    }

    // ================= 材质透明度（唯一可调参数） =================

    private void SetAlpha_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string tag) return;
        if (!double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)) return;

        _metalAlpha = a;
        ApplyMetalAlpha();
        UpdateAlphaChecks();
        SaveLayout();
    }

    /// <summary>
    /// 只改变金属底与中央暗区的 alpha（颜色、反射、倒角、噪声一律不动）。
    /// 暗区按同一比例缩放，否则底座变透、暗区仍盖着，档位调整会看不出差别。
    /// </summary>
    private void ApplyMetalAlpha()
    {
        double a = Math.Clamp(_metalAlpha, 0.30, 1.00);
        double factor = a / 0.96;                       // 相对基准 96% 的比例

        // 液态玻璃底：比金属更清透的蓝调深色（仅 RGB 变化，alpha 仍由档位控制）
        byte baseAlpha = (byte)Math.Round(a * 255.0);
        var baseBrush = new LinearGradientBrush(
            Color.FromArgb(baseAlpha, 0x14, 0x18, 0x20),
            Color.FromArgb(baseAlpha, 0x1C, 0x22, 0x2E),
            new Point(0, 0), new Point(1, 1));
        baseBrush.Freeze();
        MetalBaseBorder.Background = baseBrush;

        // 中央暗区（颜色 #0B0D10 不变，仅 alpha 按比例缩放）
        byte vigAlpha = (byte)Math.Round(0x9E * Math.Min(1.0, factor));
        var vigBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.38),
            Center = new Point(0.5, 0.38),
            RadiusX = 0.75,
            RadiusY = 0.80,
        };
        vigBrush.GradientStops.Add(new GradientStop(Color.FromArgb(vigAlpha, 0x0B, 0x0D, 0x10), 0));
        vigBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0x0B, 0x0D, 0x10), 1));
        vigBrush.Freeze();
        VignetteRect.Fill = vigBrush;
    }

    private void UpdateAlphaChecks()
    {
        bool Near(double v) => Math.Abs(_metalAlpha - v) < 0.001;
        Alpha96.IsChecked = Near(0.96);
        Alpha88.IsChecked = Near(0.88);
        Alpha80.IsChecked = Near(0.80);
        Alpha70.IsChecked = Near(0.70);
        Alpha60.IsChecked = Near(0.60);
    }

    /// <summary>置顶开关：勾上后始终显示在所有窗口之上（不抢焦点），否则常驻最底层。</summary>
    private void ToggleTopMost_Click(object sender, RoutedEventArgs e)
    {
        _topMost = !_topMost;
        TopMostMenu.IsChecked = _topMost;
        ApplyZOrder();
        SaveLayout();
    }

    private void ApplyZOrder()
    {
        if (_topMost) Native.SetTopMost(this);
        else Native.SendToBottom(this);
    }

    private void StartShowListener()
    {
        var ev = App.ShowEvent;
        if (ev == null) return;

        ThreadPool.RegisterWaitForSingleObject(ev, (_, _) =>
        {
            if (_closing) return;
            Dispatcher.BeginInvoke(() => Native.PreviewToTopThenBottom(this, 3));
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private void LoadLayout()
    {
        _cfgX = 0; _cfgY = 0; _movable = true;
        if (!File.Exists(LayoutFile)) return;
        foreach (var line in File.ReadAllLines(LayoutFile))
        {
            var sp = line.Split('=');
            if (sp.Length != 2) continue;
            var k = sp[0].Trim().ToLower();
            var v = sp[1].Trim();
            if (k == "pos_x") int.TryParse(v, out _cfgX);
            else if (k == "pos_y") int.TryParse(v, out _cfgY);
            else if (k == "movable") bool.TryParse(v, out _movable);
            else if (k == "theme") _isMetal = !v.Equals("glass", StringComparison.OrdinalIgnoreCase);
            else if (k == "topmost") bool.TryParse(v, out _topMost);
            else if (k == "metal_alpha"
                     && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var ma))
                _metalAlpha = Math.Clamp(ma, 0.30, 1.00);
        }
    }

    private void SaveLayout()
    {
        try
        {
            File.WriteAllText(LayoutFile,
                $"pos_x={Math.Round(Left)}\npos_y={Math.Round(Top)}\nmovable={_movable}" +
                $"\ntheme={(_isMetal ? "metal" : "glass")}\ntopmost={_topMost}" +
                $"\nmetal_alpha={_metalAlpha.ToString("0.00", CultureInfo.InvariantCulture)}\n");
        }
        catch { }
    }

    private void ToggleMove_Click(object sender, RoutedEventArgs e)
    {
        _movable = !_movable;
        TiltRoot.Cursor = _movable ? Cursors.SizeAll : Cursors.Arrow;
        if (!_movable) { Native.MakeNonActivating(this); SaveLayout(); }
    }

    // ================= 材质主题 / 倾斜 / 反射 =================

    private void ToggleTheme_Click(object sender, RoutedEventArgs e) => ApplyTheme(!_isMetal, persist: true);

    /// <summary>切换材质：只替换资源字典与两个材质层的可见性，不重建窗口、不动列表数据。</summary>
    private void ApplyTheme(bool metal, bool persist)
    {
        _isMetal = metal;

        var uri = new Uri(metal ? "Themes/LiquidTheme.xaml" : "Themes/GlassTheme.xaml", UriKind.Relative);
        Resources.MergedDictionaries[0] = new ResourceDictionary { Source = uri };

        GlassLayer.Visibility = metal ? Visibility.Collapsed : Visibility.Visible;
        MetalLayer.Visibility = metal ? Visibility.Visible : Visibility.Collapsed;
        NoiseLayer.Opacity = metal ? 0.03 : 0.0;

        if (metal) { StartAmbientReflection(); ApplyMetalAlpha(); }
        else { StopAmbientReflection(); ResetReflection(); }

        CollapseAllExpanded();
        if (persist) SaveLayout();
    }

    private void CollapseAllExpanded()
    {
        _leaveTimer?.Stop();
        _expandedGroup = null;
        foreach (var g in Groups)
        {
            g.IsExpanded = false;
            g.Confirming = false;
            foreach (var c in g.Children)
            {
                c.IsShining = false;
                c.IsLit = false;
                c.Confirming = false;
            }
        }
    }

    /// <summary>
    /// 一次性扫光：子项文字先完成布局，再逐项 0.5 秒；同一时刻只有一条光带。
    /// 每次点击展开都重新开始；收起/切换/关闭时通过 _shineVersion 立即作废。
    /// </summary>
    private async void PlayShine(AppGroup g)
    {
        int ver = ++_shineVersion;

        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (ver != _shineVersion || !g.IsExpanded) return;

            foreach (var c in g.Children.ToList())
            {
                if (ver != _shineVersion || !g.IsExpanded) return;

                c.IsShining = true;            // 填充层渐显 + 光带扫过
                await Task.Delay(500);
                if (ver != _shineVersion) { c.IsShining = false; return; }

                c.IsLit = true;                // 先让低饱和填充与彩色边框常驻
                c.IsShining = false;           // 再撤掉光带
            }
        }
        catch
        {
            // async void 不得让异常冒到 Dispatcher；异常时静默结束本轮序列
        }
    }

    /// <summary>
    /// 环境反射的缓慢自动漂移：不读取鼠标坐标、不改变面板姿态。
    /// 约 8 次/秒的极低频更新（26 秒一个周期，位移仅 5-9 px），
    /// 既保留"有生命感"的动态反射，又不产生逐帧渲染开销。
    /// </summary>
    private void StartAmbientReflection()
    {
        if (_ambientTimer != null) return;
        _ambientTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _ambientTimer.Tick += (_, _) => UpdateAmbientReflection();
        _ambientTimer.Start();
    }

    private void StopAmbientReflection()
    {
        _ambientTimer?.Stop();
        _ambientTimer = null;
    }

    private void UpdateAmbientReflection()
    {
        if (!_isMetal) return;

        double t = _ambientClock.Elapsed.TotalSeconds;
        double w = Math.PI * 2.0 / 26.0;              // 26 秒一个完整周期
        double dx = Math.Sin(t * w) * 7.0;
        double dy = Math.Cos(t * w * 0.7) * 5.0;

        WarmOffset.X = dx;
        WarmOffset.Y = dy;
        CoolOffset.X = -dx * 1.4;
        CoolOffset.Y = -dy * 1.2;
        StripOffset.X = dx * 1.8;
        StripOffset.Y = dy * 0.4;
    }

    private void ResetReflection()
    {
        WarmOffset.X = WarmOffset.Y = 0;
        CoolOffset.X = CoolOffset.Y = 0;
        StripOffset.X = StripOffset.Y = 0;
    }

    /// <summary>金属层按圆角轮廓裁剪，避免反射光斑溢出圆角。</summary>
    private void MetalClip_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid g || g.ActualWidth <= 0 || g.ActualHeight <= 0) return;
        g.Clip = new RectangleGeometry(new Rect(0, 0, g.ActualWidth, g.ActualHeight), 15, 15);
    }

    /// <summary>展开后做最小必要滚动，让当前程序卡片尽量完整可见；不抢用户手动滚动。</summary>
    private void EnsureVisible(AppGroup g)
    {
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (GroupsList.ItemContainerGenerator.ContainerFromItem(g) is not FrameworkElement container) return;
                if (container.ActualHeight <= 0) return;

                var pos = container.TransformToAncestor(Scroller).Transform(new Point(0, 0));
                double top = pos.Y + Scroller.VerticalOffset;
                double bottom = top + container.ActualHeight;
                double viewTop = Scroller.VerticalOffset;
                double viewBottom = viewTop + Scroller.ViewportHeight;

                if (bottom > viewBottom)
                    Scroller.ScrollToVerticalOffset(Math.Min(viewTop + (bottom - viewBottom), Scroller.ScrollableHeight));
                else if (top < viewTop)
                    Scroller.ScrollToVerticalOffset(Math.Max(top, 0));
            }
            catch { /* 容器尚未生成或已回收：忽略 */ }
        }, DispatcherPriority.ContextIdle);
    }

    /// <summary>子项卡片按真实圆角形状裁剪，保证扫光不越过轮廓。</summary>
    private void ChildCard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Border b || b.ActualWidth <= 0 || b.ActualHeight <= 0) return;

        // 尺寸未实际变化时不重建几何，避免频繁分配
        if (b.Clip is RectangleGeometry old
            && Math.Abs(old.Rect.Width - b.ActualWidth) < 0.5
            && Math.Abs(old.Rect.Height - b.ActualHeight) < 0.5) return;

        b.Clip = new RectangleGeometry(new Rect(0, 0, b.ActualWidth, b.ActualHeight), 8, 8);
    }

    // ================= 扫描调度（手动/自动统一入口） =================

    private void ScanBtn_Click(object sender, RoutedEventArgs e) => RequestScan(true);

    private void RequestScan(bool manual)
    {
        if (_closing) return;
        if (manual) _timer?.Stop();

        lock (_scanGate)
        {
            if (_scanRunning)
            {
                if (manual) _manualPending = true;
                return;
            }
            _scanRunning = true;
        }

        IsScanning = true;
        RunScan();
    }

    private void RunScan()
    {
        Task.Run(() =>
        {
            List<AppGroup>? groups = null;
            try { groups = ComputeGroups(); }
            catch { }

            Dispatcher.Invoke(() =>
            {
                if (!_closing && !Dispatcher.HasShutdownStarted)
                {
                    if (groups != null) ApplyGroups(groups);
                    else StatusText = "扫描失败，保留上次结果";
                }
                IsScanning = false;
            });

            bool again;
            lock (_scanGate)
            {
                again = _manualPending;
                _manualPending = false;
                _scanRunning = false;
            }

            if (again && !_closing)
            {
                Dispatcher.Invoke(() => { if (!_closing) IsScanning = true; });
                RunScan();
            }
            else if (!_closing)
            {
                Dispatcher.Invoke(() => { _timer?.Stop(); _timer?.Start(); });
            }
        });
    }

    // 纯计算（后台线程）：收集→过滤→分类→程序聚合
    private static List<AppGroup> ComputeGroups()
    {
        var visiblePids = Native.GetVisibleWindowPids();
        int activePid = Native.GetForegroundPid();

        // 友好名缓存上限：进程路径频繁变化时避免无限增长
        if (NameCache.Count > 512) NameCache.Clear();

        var all = new List<ProcessItem>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == MyPid) continue;
                var path = p.MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(path)) continue;
                if (SystemCritical.Contains(p.ProcessName)) continue;
                if (SystemDirs.Any(d => path.IndexOf(d, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                if (path.Equals(MyPath, StringComparison.OrdinalIgnoreCase)) continue;

                long mem = p.WorkingSet64;
                string desc = NameCache.GetOrAdd(path, FriendlyName);
                all.Add(new ProcessItem
                {
                    Pid = p.Id,
                    Name = p.ProcessName,
                    Display = desc,
                    MemoryBytes = mem,
                    Path = path,
                    IsForeground = visiblePids.Contains(p.Id),
                    IsActive = p.Id == activePid,
                });
            }
            catch { }
        }

        return ProcessGrouper.Build(all);
    }

    private void ApplyGroups(List<AppGroup> groups)
    {
        double off = Scroller?.VerticalOffset ?? 0;
        _shineVersion++;                   // 作废旧对象的扫光序列
        string? expandedKey = _expandedGroup?.Key;
        AppGroup? newExpanded = null;
        var groupConfirm = Groups.Where(x => x.Confirming).ToDictionary(x => x.Key, x => x.ConfirmMessage);
        var itemConfirm = Groups.SelectMany(g => g.Children).Where(c => c.Confirming)
                                .ToDictionary(c => c.Pid, c => c.ConfirmMessage);

        Groups.Clear();
        foreach (var g in groups)
        {
            // 整组确认态：按分组键保留
            if (groupConfirm.TryGetValue(g.Key, out var gmsg)) { g.Confirming = true; g.ConfirmMessage = gmsg; }

            // 子项确认态：按 PID 保留
            foreach (var c in g.Children)
                if (itemConfirm.TryGetValue(c.Pid, out var cmsg)) { c.Confirming = true; c.ConfirmMessage = cmsg; }

            // 刷新期间保持当前展开状态（用最新数据重建，不重播扫光）
            if (expandedKey != null && string.Equals(g.Key, expandedKey, StringComparison.OrdinalIgnoreCase))
            {
                g.IsExpanded = true;
                foreach (var c in g.Children) c.IsLit = true;
                newExpanded = g;
            }

            Groups.Add(g);
        }

        _expandedGroup = newExpanded;      // 引用指向重建后的新对象

        Scroller?.ScrollToVerticalOffset(off);
        int procCount = groups.Sum(g => g.Count);
        StatusText = $"共 {groups.Count} 个程序 / {procCount} 个进程 | 下次刷新 10s";
    }

    private static string FriendlyName(string path)
    {
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(vi.FileDescription)) return vi.FileDescription;
            if (!string.IsNullOrWhiteSpace(vi.ProductName)) return vi.ProductName;
        }
        catch { }
        return Path.GetFileNameWithoutExtension(path) ?? path;
    }

    // ================= 次级操作：打开文件位置 =================

    private void PathMenu_Click(object sender, RoutedEventArgs e)
    {
        string path = ((FrameworkElement)sender).Tag switch
        {
            AppGroup g => g.Path,
            ProcessItem it => it.Path,
            _ => ""
        };
        OpenPath(path);
    }

    private static void OpenPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    // ================= 整组结束（主项，内联确认） =================

    private void EndGroup_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not AppGroup g) return;

        bool anyHigh = g.Children.Any(c => HighRisk.Contains(c.Name));
        g.ConfirmMessage = anyHigh
            ? $"「{g.Display}」共 {g.Count} 个进程，其中含高危程序，结束可能丢失未保存内容。确定全部结束？"
            : $"确定结束「{g.Display}」的全部 {g.Count} 个进程吗？";
        g.Confirming = true;
    }

    private void ConfirmGroup_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not AppGroup g) return;
        g.Confirming = false;
        KillGroup(g);
    }

    private void CancelGroup_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is AppGroup g) g.Confirming = false;
    }

    private void KillGroup(AppGroup g)
    {
        int failed = 0; string firstFail = "";
        foreach (var c in g.Children.ToList())
        {
            if (!KillProcess(c.Pid, c.Path, out var reason))
            {
                failed++;
                if (firstFail == "") firstFail = reason;
            }
        }

        if (failed > 0)
        {
            MessageBox.Show($"有 {failed} 个进程无法结束：\n{firstFail}\n\n若是系统/其他用户进程，请以管理员身份运行本面板再试。",
                "结束失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText = $"部分失败：{g.Display}（{failed} 个）";
        }
        else
        {
            StatusText = $"已结束：{g.Display}（{g.Count} 个进程）";
        }

        _expandedGroup = null;
        _shineVersion++;
        g.IsExpanded = false;              // 结束后收起，避免残留空展开区
        _timer?.Stop();
        RequestScan(false);
    }

    // ================= 单进程结束（子项，内联确认） =================

    private void End_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not ProcessItem item) return;
        item.ConfirmMessage = HighRisk.Contains(item.Name)
            ? $"结束「{item.Display}」(PID {item.Pid}) 属于高危操作，可能丢失未保存内容。确定继续？"
            : $"确定结束「{item.Display}」(PID {item.Pid}) 吗？";
        item.Confirming = true;
    }

    private void ConfirmEnd_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not ProcessItem item) return;
        item.Confirming = false;
        KillSingle(item);
    }

    private void CancelEnd_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ProcessItem item) item.Confirming = false;
    }

    private void KillSingle(ProcessItem item)
    {
        if (!KillProcess(item.Pid, item.Path, out var failReason))
        {
            MessageBox.Show($"无法结束「{item.Display}」：\n{failReason}\n\n若是系统进程或其他用户的进程，请以管理员身份运行本面板再试。",
                "结束失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText = $"结束失败：{item.Display}";
        }
        else
        {
            StatusText = $"已结束：{item.Display} (PID {item.Pid})";
        }
        _timer?.Stop();
        RequestScan(false);
    }

    // 按 PID 取“活的”句柄结束进程；杀前核对路径身份，防 PID 复用误杀。
    private static bool KillProcess(int pid, string expectedPath, out string reason)
    {
        reason = "";
        try
        {
            Process proc;
            try { proc = Process.GetProcessById(pid); }
            catch (ArgumentException) { return true; }
            try
            {
                if (proc.HasExited) return true;

                string live = "";
                try { live = proc.MainModule?.FileName ?? ""; } catch { }
                if (!string.IsNullOrEmpty(expectedPath) && !string.IsNullOrEmpty(live)
                    && !live.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "进程已变化（PID 被系统复用给其它程序），已跳过以避免误杀。";
                    return false;
                }

                try { if (proc.MainWindowHandle != IntPtr.Zero) proc.CloseMainWindow(); } catch { }
                if (!proc.WaitForExit(1500))
                {
                    try { proc.Kill(true); }
                    catch { try { proc.Kill(); } catch (Exception kex) { reason = kex.Message; } }
                }
                try { if (!proc.WaitForExit(1500)) reason = "进程强杀后仍然存在（可能被系统/其他用户保护，或会自启）。"; }
                catch { }
                return reason == "";
            }
            finally { proc.Dispose(); }
        }
        catch (Exception ex) { reason = ex.Message; return false; }
    }

    private bool _forceClose;

    private void ExitMenu_Click(object sender, RoutedEventArgs e)
    {
        _forceClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose) { e.Cancel = true; return; }
        _closing = true;
        _timer?.Stop();
        _leaveTimer?.Stop();
        StopAmbientReflection();
        _shineVersion++;                    // 关闭时取消所有待播动画
        base.OnClosing(e);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
