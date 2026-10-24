using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using WinRT.Interop;

namespace DisplayDock;

public sealed partial class MainWindow : Window
{
    // Pill boyutu (DIP)
    private const int BarWidthDip = 400;
    private const int BarHeightDip = 56;
    private const int TopMarginDip = 8;

    // Hover ayarları
    private const int HotZoneHeightDip = 3;                                   // ekranın en üst kaç px'i tetikler
    private static readonly TimeSpan ShowDwell = TimeSpan.FromMilliseconds(120); // kenarda bekleme süresi
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(450);  // çıkınca gizleme gecikmesi
    private static readonly TimeSpan IntroTime = TimeSpan.FromMilliseconds(2500);
    private const int LeaveMarginDip = 28;

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;

    // Sürekli polling (40 ms) yerine: olay tabanlı fare hook'u + tek seferlik zamanlayıcılar.
    // Fare hareketsizken uygulama hiç uyanmaz (0 CPU).
    private readonly DispatcherQueueTimer _dwellTimer;
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly LowLevelMouseProc _hookProc; // GC toplamasın diye alanda tutulur
    private IntPtr _hook;

    private bool _visible;
    private bool _pinned;
    private bool _busy;
    private bool _inIntro;

    // Monitör bilgisi önbelleği (hook içinde ucuz kontrol için)
    private IntPtr _cachedMon;
    private RECT _cachedMonRect;
    private double _cachedMonScale = 1;

    private static readonly SolidColorBrush MultiBrush = new(Windows.UI.Color.FromArgb(255, 0xA9, 0xB9, 0xFF));
    private static readonly SolidColorBrush SingleBrush = new(Windows.UI.Color.FromArgb(255, 0xD2, 0xD2, 0xCA));

    public MainWindow()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow;

        // Çerçevesiz, her zaman üstte, görev çubuğunda/Alt+Tab'da görünmeyen pencere
        ExtendsContentIntoTitleBar = true;
        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(true, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }
        // Görev çubuğu ve Alt+Tab'da görünmesin (IsShownInSwitchers burada desteklenmiyor)
        const int GWL_EXSTYLE = -20;
        const long WS_EX_TOOLWINDOW = 0x00000080, WS_EX_APPWINDOW = 0x00040000;
        long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr((ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW));

        // Windows 11 yuvarlak köşe + koyu kenarlık
        int corner = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(_hwnd, 33, ref corner, sizeof(int));
        int border = 0x00282C2C; // COLORREF (0x00BBGGRR) => #2C2C28
        DwmSetWindowAttribute(_hwnd, 34, ref border, sizeof(int));

        _area = DisplayArea.Primary;

        _dwellTimer = DispatcherQueue.CreateTimer();
        _dwellTimer.IsRepeating = false;
        _dwellTimer.Interval = ShowDwell;
        _dwellTimer.Tick += (_, _) => Safe(OnDwellElapsed);

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => Safe(OnHideElapsed);

        // Düşük seviye fare hook'u: UI thread'inin mesaj döngüsünde çalışır.
        _hookProc = HookCallback;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) App.Trace($"Fare hook'u kurulamadı: {Marshal.GetLastWin32Error()}");

        Closed += (_, _) => Cleanup();

        // Açılışta 2,5 sn göster ki çalıştığı belli olsun (ilk gösterimde Activate: XAML'ın çizilmesi için)
        _inIntro = true;
        ShowBar(_area);
        StartHideTimer(IntroTime);
        App.Trace("Başladı, açılış gösterimi yapıldı.");
    }

    private static void Safe(Action a)
    {
        try { a(); } catch (Exception ex) { App.Log(ex); }
    }

    // ---------------- Konum / hover ----------------

    private DisplayArea _area;
    private bool _everShown;

    private double Scale => GetDpiForWindow(_hwnd) / 96.0;

    private void Reposition()
    {
        // Seçili ekranın üst ortası. (DPI farklı ekranlarda iki kez yapmak ölçeği düzeltir.)
        for (int i = 0; i < 2; i++)
        {
            RectInt32 b = _area.OuterBounds;
            double s = Scale;
            int w = (int)Math.Round(BarWidthDip * s);
            int h = (int)Math.Round(BarHeightDip * s);
            _appWindow.MoveAndResize(new RectInt32(b.X + (b.Width - w) / 2, b.Y + (int)(TopMarginDip * s), w, h));
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Bu yol çok sık çağrılır: tahsis yok, WinRT çağrısı yok, sadece ucuz Win32 + aritmetik.
        if (nCode >= 0 && wParam == WM_MOUSEMOVE)
        {
            try
            {
                int x = Marshal.ReadInt32(lParam);
                int y = Marshal.ReadInt32(lParam, 4);
                OnMouseMove(x, y);
            }
            catch (Exception ex) { App.Log(ex); }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void OnMouseMove(int x, int y)
    {
        if (!_visible)
        {
            bool hot = IsInHotZone(x, y);
            if (!hot) { if (_dwellTimer.IsRunning) _dwellTimer.Stop(); }
            else if (!_dwellTimer.IsRunning) _dwellTimer.Start();
            return;
        }

        if (_pinned || _busy || _inIntro) return;

        if (IsInsideBar(x, y)) { if (_hideTimer.IsRunning) _hideTimer.Stop(); }
        else if (!_hideTimer.IsRunning) StartHideTimer(HideDelay);
    }

    private bool IsInHotZone(int x, int y)
    {
        // Fare hangi ekrandaysa o ekranın üst ortası tetikler
        IntPtr mon = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        if (mon != _cachedMon)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return false;
            _cachedMon = mon;
            _cachedMonRect = mi.rcMonitor;
            _cachedMonScale = GetDpiForMonitor(mon, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        }

        RECT r = _cachedMonRect;
        double s = _cachedMonScale;
        int halfW = (int)(BarWidthDip * s / 2);
        int centerX = r.Left + (r.Right - r.Left) / 2;
        return y >= r.Top && y <= r.Top + Math.Max(2, HotZoneHeightDip * s)
               && Math.Abs(x - centerX) <= halfW;
    }

    private bool IsInsideBar(int x, int y)
    {
        double s = Scale;
        RectInt32 screen = _area.OuterBounds;
        PointInt32 pos = _appWindow.Position;
        SizeInt32 size = _appWindow.Size;
        int m = (int)(LeaveMarginDip * s);
        return x >= pos.X - m && x <= pos.X + size.Width + m
               && y >= screen.Y && y <= pos.Y + size.Height + m;
    }

    private void OnDwellElapsed()
    {
        if (_visible || !GetCursorPos(out POINT pt) || !IsInHotZone(pt.X, pt.Y)) return;
        ShowBar(DisplayArea.GetFromPoint(new PointInt32(pt.X, pt.Y), DisplayAreaFallback.Nearest));
    }

    private void OnHideElapsed()
    {
        _inIntro = false;
        if (!_visible || _pinned || _busy) return;
        if (GetCursorPos(out POINT pt) && IsInsideBar(pt.X, pt.Y)) return; // hook çıkışta tekrar başlatır
        HideBar();
    }

    private void StartHideTimer(TimeSpan delay)
    {
        _hideTimer.Stop();
        _hideTimer.Interval = delay;
        _hideTimer.Start();
    }

    /// <summary>Sabitleme/işlem bittiğinde fare zaten dışarıdaysa gizlemeyi planla.</summary>
    private void ReevaluateLeave()
    {
        if (_visible && !_pinned && !_busy && !_inIntro
            && GetCursorPos(out POINT pt) && !IsInsideBar(pt.X, pt.Y))
            StartHideTimer(HideDelay);
    }

    private void ShowBar(DisplayArea area)
    {
        _visible = true;
        _dwellTimer.Stop();
        _hideTimer.Stop();
        _area = area;

        SetEfficiencyMode(false); // görünürken akıcı animasyon
        Reposition();
        RefreshState();
        if (!_everShown)
        {
            _everShown = true;
            Activate();             // ilk seferde gerçek aktivasyon (içerik çizilsin)
        }
        else
        {
            _appWindow.Show(false); // sonrakilerde odağı çalmadan göster
        }
        Animate(-14, 0, 0, 1, 200, null);
    }

    private void HideBar()
    {
        _visible = false;
        _hideTimer.Stop();
        Animate(0, -10, 1, 0, 120, () =>
        {
            if (_visible) return;
            _appWindow.Hide();
            // Gizliyken: EcoQoS (verimlilik modu) + çalışma kümesini boşalt
            SetEfficiencyMode(true);
            SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        });
    }

    private void Animate(double fromY, double toY, double fromO, double toO, int ms, Action? completed)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(ms));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var slide = new DoubleAnimation { From = fromY, To = toY, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(slide, SlideTransform);
        Storyboard.SetTargetProperty(slide, "Y");

        var fade = new DoubleAnimation { From = fromO, To = toO, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(fade, Bar);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(slide);
        sb.Children.Add(fade);
        if (completed != null) sb.Completed += (_, _) => completed();
        sb.Begin();
    }

    // ---------------- Ekran modları ----------------

    private async void OnModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn || btn.Tag is not string tag) return;
        if (!Enum.TryParse(tag, out DisplayMode mode)) return;

        SetChecked(mode); // anında geri bildirim
        _busy = true;
        _hideTimer.Stop();
        try
        {
            bool ok = await Task.Run(() => DisplayConfig.Apply(mode));
            // Ekran değişimi sonrası Windows'un yerleşmesini bekle
            await Task.Delay(ok ? 1500 : 200);
            _cachedMon = IntPtr.Zero; // monitör düzeni değişmiş olabilir
            _area = DisplayArea.Primary;
            Reposition();
            RefreshState();
        }
        catch (Exception ex) { App.Log(ex); }
        finally
        {
            _busy = false;
            ReevaluateLeave();
        }
    }

    private void RefreshState()
    {
        DisplayMode mode = DisplayConfig.GetCurrentMode();
        int count = DisplayConfig.GetActiveDisplayCount();

        if (mode == DisplayMode.Unknown)
            mode = count >= 2 ? DisplayMode.Extend : DisplayMode.Unknown;

        SetChecked(mode);

        StatusText.Text = count > 0 ? count.ToString() : "";
        StatusInner.Fill = count >= 2 ? MultiBrush : SingleBrush;
        ToolTipService.SetToolTip(StatusDot, $"Açık ekran: {count}  •  Mod: {ModeName(mode)}");
    }

    private void SetChecked(DisplayMode mode)
    {
        BtnInternal.IsChecked = mode == DisplayMode.Internal;
        BtnExternal.IsChecked = mode == DisplayMode.External;
        BtnExtend.IsChecked = mode == DisplayMode.Extend;
        BtnClone.IsChecked = mode == DisplayMode.Clone;
    }

    private static string ModeName(DisplayMode m) => m switch
    {
        DisplayMode.Internal => "Sadece 1. ekran",
        DisplayMode.External => "Sadece 2. ekran",
        DisplayMode.Extend => "Genişlet",
        DisplayMode.Clone => "Çoğalt",
        _ => "Bilinmiyor",
    };

    // ---------------- Diğer butonlar ----------------

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        _pinned = BtnPin.IsChecked == true;
        PinIcon.Glyph = _pinned ? "\uE840" : "\uE718"; // Pinned / Pin
        if (_pinned) _hideTimer.Stop();
        else ReevaluateLeave();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Cleanup();
        Application.Current.Exit();
    }

    private void Cleanup()
    {
        _dwellTimer.Stop();
        _hideTimer.Stop();
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    // ---------------- Verimlilik modu (EcoQoS) ----------------

    private bool? _ecoState;

    private void SetEfficiencyMode(bool on)
    {
        if (_ecoState == on) return;
        _ecoState = on;
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = 1,          // PROCESS_POWER_THROTTLING_CURRENT_VERSION
            ControlMask = 0x1,    // PROCESS_POWER_THROTTLING_EXECUTION_SPEED
            StateMask = on ? 0x1u : 0u,
        };
        SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */,
            ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
    }

    // ---------------- Win32 ----------------

    private const int WH_MOUSE_LL = 14;
    private static readonly IntPtr WM_MOUSEMOVE = 0x0200;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE { public uint Version; public uint ControlMask; public uint StateMask; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessInformation(IntPtr hProcess, int infoClass,
        ref PROCESS_POWER_THROTTLING_STATE info, int size);

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, nint min, nint max);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
