using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Codex 额度卡")]
[assembly: System.Reflection.AssemblyVersion("1.0.1.0")]

internal static class Taskbar
{
    internal const int CardWidth = 112;
    internal const int CardHeight = 36;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public Rectangle Bounds { get { return Rectangle.FromLTRB(Left, Top, Right, Bottom); } }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]
    internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    private static double ScaleFor(IntPtr tray)
    {
        uint dpi = tray == IntPtr.Zero ? 0 : GetDpiForWindow(tray);
        return dpi == 0 ? 1 : dpi / 96.0;
    }

    private static Size CardSize(double scale)
    {
        return new Size((int)Math.Round(CardWidth * scale), (int)Math.Round(CardHeight * scale));
    }

    internal static Rectangle FallbackBounds(int? center)
    {
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        var screen = tray == IntPtr.Zero ? Screen.PrimaryScreen : Screen.FromHandle(tray);
        double scale = ScaleFor(tray);
        var work = screen.WorkingArea;
        NativeRect raw;
        if (tray != IntPtr.Zero && GetWindowRect(tray, out raw) && raw.Bounds.Width > raw.Bounds.Height &&
            raw.Top > work.Top && raw.Top < work.Bottom)
        {
            work = Rectangle.FromLTRB(work.Left, work.Top, work.Right, raw.Top);
        }
        return AboveTaskbar(work, center ?? work.Right - (int)Math.Round(360 * scale), CardSize(scale), (int)Math.Round(10 * scale));
    }

    internal static Rectangle AboveTaskbar(Rectangle area, int center, Size size, int gap)
    {
        int left = Math.Max(area.Left, Math.Min(center - size.Width / 2, area.Right - size.Width - gap));
        int top = Math.Max(area.Top, area.Bottom - size.Height - gap);
        return new Rectangle(left, top, size.Width, size.Height);
    }

    internal static Rectangle FindSlot(Rectangle bar, Rectangle weather, IEnumerable<Rectangle> controls, Size size, int gap)
    {
        int top = bar.Top + (bar.Height - size.Height) / 2;
        foreach (int left in new[] { weather.Left - gap - size.Width, weather.Right + gap })
        {
            var slot = new Rectangle(left, top, size.Width, size.Height);
            if (bar.Contains(slot) && !controls.Any(slot.IntersectsWith)) return slot;
        }
        return Rectangle.Empty;
    }

    internal static Rectangle Locate()
    {
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        NativeRect raw;
        if (tray == IntPtr.Zero || !IsWindowVisible(tray) || !GetWindowRect(tray, out raw)) return Rectangle.Empty;
        Rectangle screen = Screen.FromHandle(tray).Bounds;
        Rectangle bar = raw.Bounds;
        if (!screen.Contains(bar) || bar.Width <= bar.Height) return Rectangle.Empty;

        IntPtr foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != tray && GetWindowRect(foreground, out raw) && raw.Bounds.Contains(screen))
        {
            var name = new StringBuilder(80);
            GetClassName(foreground, name, name.Capacity);
            if (name.ToString() != "Progman" && name.ToString() != "WorkerW") return Rectangle.Empty;
        }

        var root = AutomationElement.FromHandle(tray);
        var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        Rectangle weather = Rectangle.Empty;
        var controls = new List<Rectangle>();
        foreach (AutomationElement button in buttons)
        {
            var data = button.Current;
            var rect = data.BoundingRectangle;
            if (data.IsOffscreen || rect.IsEmpty) continue;
            var bounds = Rectangle.FromLTRB((int)Math.Floor(rect.Left), (int)Math.Floor(rect.Top),
                (int)Math.Ceiling(rect.Right), (int)Math.Ceiling(rect.Bottom));
            controls.Add(bounds);
            if (data.AutomationId == "WidgetsButton") weather = bounds;
        }
        if (weather.IsEmpty || !bar.Contains(weather)) return Rectangle.Empty;
        double scale = ScaleFor(tray);
        return FindSlot(bar, weather, controls, CardSize(scale), (int)Math.Round(10 * scale));
    }
}

internal static class ChatGptDesktop
{
    internal static bool IsClientImage(string image)
    {
        string folder = Path.GetDirectoryName(image);
        string package = Path.GetDirectoryName(folder);
        return String.Equals(Path.GetFileName(folder), "app", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(package).StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) &&
            String.Equals(Path.GetFileName(Path.GetDirectoryName(package)), "WindowsApps", StringComparison.OrdinalIgnoreCase);
    }

    internal static string FindImage(int session)
    {
        foreach (string name in new[] { "ChatGPT", "Codex" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (process.SessionId != session) continue;
                        string image = process.MainModule.FileName;
                        if (IsClientImage(image)) return image;
                    }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
        return null;
    }
}

public sealed class QuotaRing
{
    public string key { get; set; }
    public int? percent { get; set; }
    public string tone { get; set; }
}

public sealed class Quota
{
    public string title { get; set; }
    public string windowLabel { get; set; }
    public int? percent { get; set; }
    public string tone { get; set; }
    public QuotaRing[] rings { get; set; }
}

internal sealed class Card : Form
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref Point destination, ref Size size,
        IntPtr source, ref Point origin, uint key, ref BlendFunction blend, uint flags);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapHeader header, uint usage, out IntPtr bits,
        IntPtr section, uint offset);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ImportantColors;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte Operation, Flags, Opacity, AlphaFormat;
    }

    private readonly System.Windows.Forms.Timer placementTimer = new System.Windows.Forms.Timer { Interval = 1000 };
    private readonly ToolTip tip = new ToolTip { ShowAlways = true, AutoPopDelay = 10000 };
    private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
    private readonly NotifyIcon trayButton;
    private Icon trayImage;
    private bool lightTheme;
    private Size frameSize;
    private bool frameFloating;
    private readonly JavaScriptSerializer json = new JavaScriptSerializer();
    private readonly string root = AppDomain.CurrentDomain.BaseDirectory;
    private readonly int session = Process.GetCurrentProcess().SessionId;
    private Process reader;
    private DateTime nextReaderStart;
    private DateTime lastReading;
    private bool placing;
    private bool? chatGptOpen;
    private Rectangle lastSlot;
    private int missedSlots;
    private Quota quota = new Quota { title = "正在读取 Codex 剩余额度" };

    internal Card()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "Codex 用量卡片";
        AccessibleName = Text;
        tip.SetToolTip(this, quota.title);
        // Create the handle before asynchronous quota updates start arriving.
        CreateHandle();
        lightTheme = UsesLightTheme();
        trayImage = CreateTrayImage(lightTheme);
        trayMenu.Items.Add("显示额度卡", null, delegate { ShowFromTray(); });
        trayButton = new NotifyIcon {
            Icon = trayImage, Text = "Codex 额度卡 · 点击显示", ContextMenuStrip = trayMenu, Visible = true
        };
        trayButton.MouseClick += delegate(object sender, MouseEventArgs click) {
            if (click.Button == MouseButtons.Left) ShowFromTray();
        };
        placementTimer.Tick += async delegate { await Tick(); };
        placementTimer.Start();
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    private static bool UsesLightTheme()
    {
        return Equals(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0), 1);
    }

    private static Icon CreateTrayImage(bool light)
    {
        Color teal = light ? Color.FromArgb(0, 112, 90) : Color.FromArgb(114, 231, 191);
        using (var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bitmap))
        using (var track = new Pen(Color.FromArgb(100, teal), 4))
        using (var meter = new Pen(teal, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawArc(track, 5, 5, 22, 22, 180, 90);
            g.DrawArc(meter, 5, 5, 22, 22, -90, 270);
            IntPtr icon = bitmap.GetHicon();
            try { return (Icon)Icon.FromHandle(icon).Clone(); }
            finally { DestroyIcon(icon); }
        }
    }

    private void ShowFromTray()
    {
        if (chatGptOpen != true && ChatGptDesktop.FindImage(session) == null) return;
        if (Visible) ShowAt(Bounds, frameFloating);
        else ShowAt(FallbackBounds(), floating: true);
    }

    private Rectangle FallbackBounds()
    {
        return Taskbar.FallbackBounds(lastSlot.IsEmpty ? (int?)null : lastSlot.Left + lastSlot.Width / 2);
    }

    private void ShowAt(Rectangle bounds, bool floating)
    {
        if (frameSize != bounds.Size || frameFloating != floating)
        {
            if (!RenderFrame(bounds, floating)) return;
        }
        else if (Location != bounds.Location) Location = bounds.Location;
        if (!Visible) Show();
        const uint noMoveNoResizeNoActivate = 0x0013;
        SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, noMoveNoResizeNoActivate);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var options = base.CreateParams;
            options.ExStyle |= 0x00000080 | 0x08000000 | 0x00080000; // Tool window, no activation, per-pixel alpha.
            return options;
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE
        base.WndProc(ref message);
    }

    private static GraphicsPath Outline(float inset)
    {
        var path = new GraphicsPath();
        float right = Taskbar.CardWidth - inset - 14, bottom = Taskbar.CardHeight - inset - 14;
        path.AddArc(inset, inset, 14, 14, 180, 90);
        path.AddArc(right, inset, 14, 14, 270, 90);
        path.AddArc(right, bottom, 14, 14, 0, 90);
        path.AddArc(inset, bottom, 14, 14, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void StartReader(string clientImage)
    {
        nextReaderStart = DateTime.UtcNow.AddSeconds(15);
        StopReader();
        var process = new Process { StartInfo = new ProcessStartInfo(Path.Combine(root, "node.exe"), "\"" + Path.Combine(root, "reader.cjs") + "\"") {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardInput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 } };
        process.StartInfo.EnvironmentVariables["CODEX_METER_APP_PATH"] = clientImage;
        process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs item) {
            if (String.IsNullOrEmpty(item.Data) || IsDisposed) return;
            try { BeginInvoke(new Action(() => { if (reader == process) Receive(item.Data); })); }
            catch (InvalidOperationException) { }
        };
        process.ErrorDataReceived += delegate { };
        try
        {
            process.Start();
            reader = process;
            reader.BeginOutputReadLine();
            reader.BeginErrorReadLine();
        }
        catch (System.ComponentModel.Win32Exception) { process.Dispose(); }
    }

    private void StopReader()
    {
        var previous = reader;
        reader = null;
        if (previous == null) return;
        try { previous.StandardInput.Close(); }
        catch (InvalidOperationException) { }
        previous.Dispose();
    }

    private void Receive(string line)
    {
        try
        {
            var result = json.Deserialize<Quota>(line);
            if (result == null || String.IsNullOrEmpty(result.title)) return;
            lastReading = DateTime.UtcNow;
            ShowQuota(result);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    private void ShowQuota(Quota value)
    {
        quota = value;
        AccessibleDescription = value.title;
        tip.SetToolTip(this, value.title);
        frameSize = Size.Empty;
        if (Visible) RenderFrame(Bounds, frameFloating);
    }

    private async Task Tick()
    {
        bool light = UsesLightTheme();
        if (light != lightTheme)
        {
            lightTheme = light;
            var nextIcon = CreateTrayImage(light);
            trayButton.Icon = nextIcon;
            trayImage.Dispose();
            trayImage = nextIcon;
            frameSize = Size.Empty;
            if (Visible) RenderFrame(Bounds, frameFloating);
        }
        string clientImage = ChatGptDesktop.FindImage(session);
        bool open = clientImage != null;
        if (chatGptOpen != open)
        {
            chatGptOpen = open;
            trayButton.Text = open ? "Codex 额度卡 · 点击显示" : "Codex 额度卡 · 打开 ChatGPT 后显示";
        }
        if (!open)
        {
            if (Visible) Hide();
            if (reader != null)
            {
                StopReader();
                nextReaderStart = default(DateTime);
                lastReading = default(DateTime);
                ShowQuota(new Quota { title = "正在读取 Codex 剩余额度" });
            }
            return;
        }

        if (!Visible) ShowAt(FallbackBounds(), floating: true);
        if ((reader == null || reader.HasExited) && DateTime.UtcNow >= nextReaderStart) StartReader(clientImage);
        if (lastReading != default(DateTime) && (DateTime.UtcNow - lastReading).TotalSeconds > 100)
        {
            lastReading = default(DateTime);
            ShowQuota(new Quota { title = "暂时无法读取 Codex 剩余额度" });
        }
        if (placing) return;
        placing = true;
        try
        {
            var slot = await Task.Run(() => {
                try { return Taskbar.Locate(); }
                catch (ElementNotAvailableException) { return Rectangle.Empty; }
                catch (InvalidOperationException) { return Rectangle.Empty; }
                catch (COMException) { return Rectangle.Empty; }
            });
            if (IsDisposed || chatGptOpen != true) return;
            if (!slot.IsEmpty)
            {
                lastSlot = slot;
                missedSlots = 0;
                ShowAt(slot, floating: false);
            }
            else if (++missedSlots >= 2)
            {
                ShowAt(FallbackBounds(), floating: true);
            }
        }
        finally { placing = false; }
    }

    private bool RenderFrame(Rectangle bounds, bool floating)
    {
        var size = bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return false;
        var dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return false;
        var dib = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            // DWM expects a top-down bitmap with premultiplied alpha.
            var header = new BitmapHeader {
                Size = (uint)Marshal.SizeOf(typeof(BitmapHeader)), Width = size.Width, Height = -size.Height,
                Planes = 1, BitCount = 32
            };
            IntPtr bits;
            dib = CreateDIBSection(dc, ref header, 0, out bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return false;
            previous = SelectObject(dc, dib);
            if (previous == IntPtr.Zero) return false;
            using (var bitmap = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, bits))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.ScaleTransform(size.Width / (float)Taskbar.CardWidth, size.Height / (float)Taskbar.CardHeight);
                DrawCard(g, quota, lightTheme, floating);
            }
            var destination = bounds.Location;
            var origin = Point.Empty;
            var blend = new BlendFunction { Opacity = 255, AlphaFormat = 1 };
            if (!UpdateLayeredWindow(Handle, IntPtr.Zero, ref destination, ref size, dc, ref origin, 0, ref blend, 2))
                return false;
            // The native layered-window move can leave WinForms' cached bounds stale.
            if (Bounds != bounds) UpdateBounds();
            frameSize = size;
            frameFloating = floating;
            return true;
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(dc, previous);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            DeleteDC(dc);
        }
    }

    private static void DrawCard(Graphics g, Quota quota, bool light, bool floating)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (var body = Outline(1))
        using (var border = new Pen(light ? Color.FromArgb(85, 20, 30, 28) : Color.FromArgb(105, 240, 248, 246)))
        {
            if (floating)
            {
                using (var background = new SolidBrush(light ? Color.FromArgb(236, 242, 240) : Color.FromArgb(31, 43, 40)))
                    g.FillPath(background, body);
            }
            g.DrawPath(border, body);
        }

        if (quota.rings != null && quota.rings.Length == 2)
        {
            DrawRing(g, 7, quota.rings[0].percent, quota.rings[0].tone, "5h", light);
            DrawRing(g, 60, quota.rings[1].percent, quota.rings[1].tone, "周", light);
        }
        else DrawRing(g, 28, quota.percent, quota.tone, quota.windowLabel == "周" ? "周" : "额度", light);
    }

    private static void DrawRing(Graphics g, int x, int? percent, string tone, string label, bool light)
    {
        Color accent = light ? Color.FromArgb(0, 112, 90) : Color.FromArgb(114, 231, 191);
        if (tone == "warning") accent = light ? Color.FromArgb(136, 85, 0) : Color.FromArgb(255, 201, 109);
        if (tone == "danger") accent = light ? Color.FromArgb(169, 48, 52) : Color.FromArgb(255, 141, 142);
        Color foreground = light ? Color.FromArgb(31, 51, 45) : Color.FromArgb(235, 246, 241);
        var dial = new RectangleF(x, 5, 26, 26);
        using (var track = new Pen(Color.FromArgb(67, foreground), 2.4f))
        using (var line = new Pen(accent, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var ink = new SolidBrush(foreground))
        using (var number = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var caption = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.DrawEllipse(track, dial);
            if (percent > 0) g.DrawArc(line, dial, -90, Math.Min(100, percent.Value) * 3.6f);
            g.DrawString(percent.HasValue ? percent.Value + "%" : "—", number, ink, dial, center);
            g.DrawString(label, caption, ink, new RectangleF(x + 28, 5, 21, 26), center);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            placementTimer.Dispose();
            tip.Dispose();
            trayButton.Dispose();
            trayMenu.Dispose();
            trayImage.Dispose();
            StopReader();
        }
        base.Dispose(disposing);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--check"))
        {
            var bar = new Rectangle(0, 1032, 1920, 48);
            var weather = new Rectangle(1522, 1032, 152, 48);
            var size = new Size(Taskbar.CardWidth, Taskbar.CardHeight);
            var expected = new Rectangle(1400, 1038, 112, 36);
            return Taskbar.FindSlot(bar, weather, new[] { weather }, size, 10) == expected &&
                Taskbar.FindSlot(bar, weather, new[] { bar }, size, 10).IsEmpty &&
                Taskbar.AboveTaskbar(new Rectangle(0, 0, 1920, 1032), 1456, size, 10) == new Rectangle(1400, 986, 112, 36) &&
                ChatGptDesktop.IsClientImage(@"C:\Program Files\WindowsApps\OpenAI.Codex_123\app\ChatGPT.exe") &&
                !ChatGptDesktop.IsClientImage(@"C:\Users\Example\AppData\Local\OpenAI\Codex\bin\codex.exe") ? 0 : 1;
        }
        Taskbar.SetProcessDpiAwarenessContext(new IntPtr(-4)); // Per-monitor v2.
        bool first;
        using (var singleton = new Mutex(true, @"Local\CodexUsageTaskbar", out first))
        {
            if (!first) return 0;
            Application.EnableVisualStyles();
            using (var card = new Card()) Application.Run();
            singleton.ReleaseMutex();
        }
        return 0;
    }
}
