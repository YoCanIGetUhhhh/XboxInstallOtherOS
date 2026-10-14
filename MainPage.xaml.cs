using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Input;
using Windows.Gaming.Input;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.System.Profile;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace XboxVmHost
{
    public sealed partial class MainPage : Page
    {
        // Every log line is also saved to a file straight away, so if the app
        // crashes we can read how far it got the next time it starts.
        private static string LogPath
        {
            get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "checks.log"); }
        }

        private static string FlagPath { get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "running.flag"); } }
        private static string CrashPath { get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "crash.txt"); } }
        private static string NetFlagPath { get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "net.on"); } }
        private static bool NetOn { get { try { return File.Exists(NetFlagPath); } catch { return false; } } }

        public MainPage()
        {
            this.InitializeComponent();
            string shown = "";
            try
            {
                // A leftover "running" file means the last run never reached its normal end: the app crashed.
                if (File.Exists(FlagPath))
                {
                    var sb = new StringBuilder();
                    sb.Append("!!! THE APP CLOSED UNEXPECTEDLY DURING THE LAST RUN !!!\n");
                    sb.Append("It was starting: ").Append(File.ReadAllText(FlagPath)).Append("\n");
                    try { if (File.Exists(LogPath)) sb.Append("--- app steps ---\n").Append(ReadLogTail(LogPath)); } catch { }
                    try { if (File.Exists(QemuOutPath)) sb.Append("--- QEMU messages (last thing it said before the crash) ---\n").Append(ReadSharedText(QemuOutPath)); } catch { }
                    File.WriteAllText(CrashPath, sb.ToString());
                    File.Delete(FlagPath);
                }
                if (File.Exists(CrashPath)) shown = ReadLogTail(CrashPath);
                else
                {
                    string old = null;
                    try { if (File.Exists(LogPath)) old = ReadLogTail(LogPath); } catch { }
                    try { if (File.Exists(QemuOutPath)) old = (old ?? "") + "--- QEMU messages ---\n" + ReadSharedText(QemuOutPath) + "\n"; } catch { }
                    if (!string.IsNullOrEmpty(old)) shown = "=== LOG FROM LAST RUN ===\n" + old;
                }
            }
            catch (Exception ex) { shown = "(could not read the last log: " + ex.Message + ")"; }
            LogText.Text = string.IsNullOrEmpty(shown) ? "Ready.\n" : shown + "\n";
            RefreshStatus();
            InitVm();
            this.Loaded += (s, e) =>
            {
                FocusFirstButton(); StartMarquee();
                // The window itself reports the pointer wherever it is, even over empty parts of the page.
                try
                {
                    Window.Current.CoreWindow.PointerMoved += (cw, a) =>
                    {
                        try
                        {
                            if (_vmActive) return;
                            var inv = PenguinGrid.TransformToVisual(Window.Current.Content).Inverse;
                            if (inv != null) PenguinLook(inv.TransformPoint(a.CurrentPoint.Position));
                        }
                        catch { }
                    };
                }
                catch { }
            };
            // handledEventsToo = true, so buttons and the log box (which mark pointer moves as handled) do not stop the eyes following.
            this.AddHandler(UIElement.PointerMovedEvent, new Windows.UI.Xaml.Input.PointerEventHandler((s, e) =>
            {
                try { if (!_vmActive) PenguinLook(e.GetCurrentPoint(PenguinGrid).Position); } catch { }
            }), true);
        }

        // Puts the highlight on the first usable button so the controller / arrow keys work straight away.
        private void FocusFirstButton()
        {
            try
            {
                Control c = BootDiskButton.IsEnabled ? (Control)BootDiskButton
                          : BootIsoButton.IsEnabled ? (Control)BootIsoButton
                          : (Control)SpeedSettingsButton;
                c.Focus(FocusState.Keyboard);
            }
            catch { }
        }

        private void ResetLog()
        {
            LogText.Text = "";
            try { File.Delete(LogPath); } catch { }
        }

        private void Log(string line)
        {
            LogText.Text += line + "\n";
            if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("===")) { try { SettingsMsg.Text = line; } catch { } }
            try { File.AppendAllText(LogPath, line + "\n"); } catch { }
        }

        // The penguin's pupils look toward the pointer (positions are in the penguin picture's own pixels).
        private void PenguinLook(Windows.Foundation.Point p)
        {
            try
            {
                MovePupil(PupilLT, 220, 125, p);
                MovePupil(PupilRT, 295, 123, p);
            }
            catch { }
        }

        private static void MovePupil(CompositeTransform t, double cx, double cy, Windows.Foundation.Point p)
        {
            double dx = p.X - cx, dy = p.Y - cy;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1) { t.TranslateX = 0; t.TranslateY = 0; return; }
            double f = Math.Min(1.0, d / 150.0);
            t.TranslateX = dx / d * 3.5 * f;
            t.TranslateY = dy / d * 5.5 * f;
        }

        // Easter egg: tapping the penguin's foot plays a short clip full screen (with sound), then returns to the menu.
        // Hint: the penguin's pupils open wide while the pointer is over the foot.
        private void EggFoot_Entered(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e) { Dilate(1.4, 2500); }
        private void EggFoot_Exited(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e) { Dilate(1.0, 250); }

        private void Dilate(double scale, int ms)
        {
            try
            {
                var sb = new Windows.UI.Xaml.Media.Animation.Storyboard();
                foreach (var t in new CompositeTransform[] { PupilLT, PupilRT })
                {
                    foreach (string prop in new string[] { "ScaleX", "ScaleY" })
                    {
                        var a = new Windows.UI.Xaml.Media.Animation.DoubleAnimation { To = scale, Duration = new Duration(TimeSpan.FromMilliseconds(ms)), EnableDependentAnimation = true,
                            EasingFunction = new Windows.UI.Xaml.Media.Animation.SineEase { EasingMode = Windows.UI.Xaml.Media.Animation.EasingMode.EaseInOut } };
                        Windows.UI.Xaml.Media.Animation.Storyboard.SetTarget(a, t);
                        Windows.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(a, prop);
                        sb.Children.Add(a);
                    }
                }
                sb.Begin();
            }
            catch { }
        }

        private bool _eggHooked;
        private DateTime _eggStart;
        private DispatcherTimer _eggWatchdog;

        private void EggFoot_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                if (_vmActive || EggGrid.Visibility == Visibility.Visible) return;
                e.Handled = true;
                if (EggVideo.MediaPlayer == null) EggVideo.SetMediaPlayer(new Windows.Media.Playback.MediaPlayer());
                var mp = EggVideo.MediaPlayer;
                if (!_eggHooked)
                {
                    _eggHooked = true;
                    mp.MediaEnded += async (s, a) => await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => EggEnd());
                    mp.MediaFailed += async (s, a) => await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        Log("Video failed: " + a.Error + " " + a.ErrorMessage);
                        EggEnd();
                    });
                }
                mp.Volume = 1.0;
                mp.IsMuted = false;
                mp.AutoPlay = true;
                _eggStart = DateTime.UtcNow;
                EggGrid.Visibility = Visibility.Visible;
                mp.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri("ms-appx:///Assets/mutahar_laugh.mp4"));
                mp.Play();
                if (_eggWatchdog == null)
                {
                    _eggWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
                    _eggWatchdog.Tick += (s, a) => EggEnd();
                }
                _eggWatchdog.Stop(); _eggWatchdog.Start();
            }
            catch (Exception ex) { Log("Video error: " + ex.GetType().Name + ": " + ex.Message); EggEnd(); }
        }

        private void EggEnd()
        {
            try { if (_eggWatchdog != null) _eggWatchdog.Stop(); } catch { }
            try { if (EggVideo.MediaPlayer != null) { EggVideo.MediaPlayer.Pause(); EggVideo.MediaPlayer.Source = null; } } catch { }
            EggGrid.Visibility = Visibility.Collapsed;
            FocusFirstButton();
        }

        private void EggGrid_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if ((DateTime.UtcNow - _eggStart).TotalSeconds > 1.5) EggEnd();
        }

        // Status lines that are too long for their box slide slowly sideways so the whole text can be read.
        private DispatcherTimer _marqueeTimer;
        private readonly Dictionary<ScrollViewer, int> _marqPause = new Dictionary<ScrollViewer, int>();
        private readonly Dictionary<ScrollViewer, bool> _marqAtEnd = new Dictionary<ScrollViewer, bool>();

        private void StartMarquee()
        {
            if (_marqueeTimer != null) return;
            var list = new ScrollViewer[] { StatusDiskScroll, StatusIsoScroll, StatusSpeedScroll };
            foreach (var sv in list) { _marqPause[sv] = 50; _marqAtEnd[sv] = false; }
            _marqueeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _marqueeTimer.Tick += (s, e) =>
            {
                foreach (var sv in list)
                {
                    try
                    {
                        double max = sv.ExtentWidth - sv.ViewportWidth;
                        if (max <= 1) { if (sv.HorizontalOffset > 0) sv.ChangeView(0, null, null, true); continue; }
                        int p = _marqPause[sv];
                        if (p > 0) { _marqPause[sv] = p - 1; continue; }
                        double off = sv.HorizontalOffset;
                        if (off >= max - 0.5)
                        {
                            if (!_marqAtEnd[sv]) { _marqAtEnd[sv] = true; _marqPause[sv] = 50; }
                            else { sv.ChangeView(0, null, null, true); _marqAtEnd[sv] = false; _marqPause[sv] = 50; }
                        }
                        else sv.ChangeView(Math.Min(max, off + 1.5), null, null, true);
                    }
                    catch { }
                }
            };
            _marqueeTimer.Start();
        }

        [DllImport("qemu.dll", EntryPoint = "qemu_dll_main")]
        private static extern int qemu_dll_main(IntPtr outfile, int argc, IntPtr argv);

        [DllImport("qemu.dll", EntryPoint = "qemu_dll_hmp", CharSet = CharSet.Ansi)]
        private static extern int qemu_dll_hmp([MarshalAs(UnmanagedType.LPStr)] string cmd, StringBuilder outText, int cap, int timeoutMs);

        [DllImport("qemu.dll", EntryPoint = "qemu_dll_stop")]
        private static extern void qemu_dll_stop();

        private string QemuOutPath
        {
            get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "qemu-out.txt"); }
        }

        // QEMU's text output file stays open inside the C runtime after QEMU finishes,
        // so it has to be opened in a mode that allows another writer to still have it open.
        // Only the END of a log is read (the GPU mode can write many megabytes, and a huge text in the log box
        // made the app crash when Linux stopped and again every time the app was opened).
        private const int MaxLogChars = 60000;

        private static string ReadSharedText(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return ReadTail(fs);
        }

        private static string ReadLogTail(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return ReadTail(fs);
        }

        private static string ReadTail(FileStream fs)
        {
            bool cut = fs.Length > MaxLogChars;
            if (cut) fs.Seek(-MaxLogChars, SeekOrigin.End);
            using (var sr = new StreamReader(fs))
            {
                string s = sr.ReadToEnd();
                if (!cut) return s;
                int nl = s.IndexOf('\n');
                if (nl >= 0 && nl < 2000) s = s.Substring(nl + 1);
                return "(... earlier lines left out, the file is " + (fs.Length / 1024) + " KB ...)\n" + s;
            }
        }

        private bool _qemuUsed;

        // =====================================================================
        // The virtual PC's screen, keyboard, mouse
        // =====================================================================
        [DllImport("qemu.dll", EntryPoint = "qemu_dll_get_frame")]
        private static extern int qemu_dll_get_frame(byte[] dst, int capBytes, out int w, out int h, ref int seq);

        [DllImport("qemu.dll", EntryPoint = "qemu_dll_key")]
        private static extern void qemu_dll_key(int keyNumber, int down);

        [DllImport("qemu.dll", EntryPoint = "qemu_dll_abs")]
        private static extern void qemu_dll_abs(int x, int y, int wheel, int buttons);

        [DllImport("qemu.dll", EntryPoint = "qemu_dll_mouse")]
        private static extern void qemu_dll_mouse(int dx, int dy, int wheel, int buttons);

        private bool _vmActive;
        private volatile bool _vmDone;
        private volatile int _vmResult = -999;
        private Thread _vmThread;
        private DispatcherTimer _vmTimer;
        private byte[] _frameBuf;
        private int _lastW, _lastH;
        private int _frameSeq;
        private WriteableBitmap _bitmap;
        private bool _stopAsked;
        private DateTime _stopAskedAt;
        private bool _gotFrame;
        private CoreCursor _savedCursor;
        private bool _vmInitDone;
        private readonly Dictionary<VirtualKey, int> _vkMap = new Dictionary<VirtualKey, int>();

        private int _mouseButtons;     // 1 left, 2 right, 4 middle (from the USB mouse)

        private void InitVm()
        {
            if (_vmInitDone) return;
            _vmInitDone = true;
            try
            {
                // Fallback table, used only when the keyboard does not report scan codes.
                string[] rows = { "qwertyuiop", "asdfghjkl", "zxcvbnm", "1234567890" };
                int[] firsts = { 0x10, 0x1E, 0x2C, 0x02 };
                for (int r = 0; r < rows.Length; r++)
                    for (int i = 0; i < rows[r].Length; i++)
                        _vkMap[(VirtualKey)char.ToUpper(rows[r][i])] = firsts[r] + i;
                _vkMap[VirtualKey.Escape] = 0x01;
                _vkMap[VirtualKey.Back] = 0x0E;
                _vkMap[VirtualKey.Tab] = 0x0F;
                _vkMap[VirtualKey.Enter] = 0x1C;
                _vkMap[VirtualKey.Space] = 0x39;
                _vkMap[VirtualKey.Shift] = 0x2A;
                _vkMap[VirtualKey.Control] = 0x1D;
                _vkMap[VirtualKey.Menu] = 0x38;
                _vkMap[VirtualKey.Up] = 0xC8;
                _vkMap[VirtualKey.Left] = 0xCB;
                _vkMap[VirtualKey.Right] = 0xCD;
                _vkMap[VirtualKey.Down] = 0xD0;
                _vkMap[VirtualKey.Delete] = 0xD3;
                for (int f = 0; f < 10; f++) _vkMap[(VirtualKey)((int)VirtualKey.F1 + f)] = 0x3B + f;
                _vkMap[VirtualKey.F11] = 0x57;
                _vkMap[VirtualKey.F12] = 0x58;

                CoreWindow cw = Window.Current.CoreWindow;
                cw.Dispatcher.AcceleratorKeyActivated += VmAccelerator;
                cw.PointerMoved += VmPointerMoved;
                cw.PointerPressed += VmPointerChanged;
                cw.PointerReleased += VmPointerChanged;
                cw.PointerWheelChanged += VmPointerWheel;
                MouseDevice.GetForCurrentView().MouseMoved += VmMouseMoved;
                SystemNavigationManager.GetForCurrentView().BackRequested += VmBackRequested;

                try { VmGrid.PointerMoved += VmGridPointerMoved; } catch { }
                try { VmGrid.PointerPressed += VmGridButtons; VmGrid.PointerReleased += VmGridButtons; } catch { }
                try { VmGrid.PointerWheelChanged += VmGridWheel; } catch { }

                _vmTimer = new DispatcherTimer();
                _vmTimer.Interval = TimeSpan.FromMilliseconds(16);
                _vmTimer.Tick += VmTick;
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(LogPath, "InitVm failed: " + ex.Message + "\n"); } catch { }
            }
        }

        // ---- status panel ----
        // Virtual RAM, chosen with the RAM button and remembered in ram.cfg. 640 is the safe default.
        private static readonly int[] RamChoices = { 640, 1024, 2048, 3072, 4096 };
        private static string RamCfgPath { get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "ram.cfg"); } }
        private static int VmRamMb
        {
            get
            {
                try
                {
                    int v;
                    if (File.Exists(RamCfgPath) && int.TryParse(File.ReadAllText(RamCfgPath).Trim(), out v) && Array.IndexOf(RamChoices, v) >= 0)
                        return (v > 640 && AppLimitMb() < 1500) ? 640 : v;   // big sizes only when the Xbox allows them
                }
                catch { }
                return 640;
            }
        }

        private void RamButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int i = Array.IndexOf(RamChoices, VmRamMb);
                File.WriteAllText(RamCfgPath, RamChoices[(i + 1) % RamChoices.Length].ToString());
            }
            catch { }
            RefreshStatus();
        }

        // =====================================================================
        // Settings: CPU type, code cache size, number of CPUs, disk type, screen size.
        // Each is remembered in its own small file. Defaults are the settings that are known to work.
        // =====================================================================
        private static readonly string[] CpuChoices = { "qemu64", "Nehalem" };
        private static readonly int[] TbChoices = { 64, 128, 256 };
        private static readonly int[] CpuCountChoices = { 1, 2 };
        private static string CpuCfgPath { get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "cpu.cfg"); } }
        private static string TbCfgPath { get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "tb.cfg"); } }
        private static string SmpCfgPath { get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "smp.cfg"); } }

        private static string VmCpuModel
        {
            get
            {
                try
                {
                    if (File.Exists(CpuCfgPath))
                    {
                        string v = File.ReadAllText(CpuCfgPath).Trim();
                        if (Array.IndexOf(CpuChoices, v) >= 0) return v;
                    }
                }
                catch { }
                return "qemu64";
            }
        }

        private static int VmTbMb
        {
            get
            {
                try
                {
                    int v;
                    if (File.Exists(TbCfgPath) && int.TryParse(File.ReadAllText(TbCfgPath).Trim(), out v) && Array.IndexOf(TbChoices, v) >= 0) return v;
                }
                catch { }
                return 128;
            }
        }

        private static int VmCpus
        {
            get
            {
                try
                {
                    int v;
                    if (File.Exists(SmpCfgPath) && int.TryParse(File.ReadAllText(SmpCfgPath).Trim(), out v) && Array.IndexOf(CpuCountChoices, v) >= 0) return v;
                }
                catch { }
                return 2;
            }
        }

        // Disk type: 0 = IDE (the old safe way), 1 = virtio (faster), 2 = virtio with unsafe cache (fast installs only).
        private static readonly string[] DiskModeNames = { "IDE (safe)", "Virtio (fast)", "Virtio, fast install (unsafe)" };
        private static string DiskModeCfgPath { get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "diskmode.cfg"); } }
        private static int VmDiskMode
        {
            get
            {
                try
                {
                    int v;
                    if (File.Exists(DiskModeCfgPath) && int.TryParse(File.ReadAllText(DiskModeCfgPath).Trim(), out v) && v >= 0 && v < DiskModeNames.Length) return v;
                }
                catch { }
                return 0;
            }
        }

        // Screen size the virtual graphics card asks Linux to use. "Auto" lets Linux choose.
        private static readonly string[] ScreenChoices = { "Auto", "1280x720", "1024x768", "800x600", "GPU" };
        private static string ScreenCfgPath { get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "screen.cfg"); } }
        private static string VmScreen
        {
            get
            {
                try
                {
                    if (File.Exists(ScreenCfgPath))
                    {
                        string v = File.ReadAllText(ScreenCfgPath).Trim();
                        if (v == "GPU test") v = "GPU"; // name used by the test builds
                        if (Array.IndexOf(ScreenChoices, v) >= 0) return v;
                    }
                }
                catch { }
                return "Auto";
            }
        }

        private static string SpeedText
        {
            get { return VmCpuModel + ", " + VmCpus + (VmCpus == 1 ? " CPU" : " CPUs") + ", cache " + VmTbMb + " MB, disk " + DiskModeNames[VmDiskMode] + ", screen " + VmScreen; }
        }

        private void SpeedSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            MainGrid.Visibility = Visibility.Collapsed;
            SettingsGrid.Visibility = Visibility.Visible;
            RefreshStatus();
            try { CpuModelButton.Focus(FocusState.Keyboard); } catch { }
        }

        private void SpeedBackButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsGrid.Visibility = Visibility.Collapsed;
            MainGrid.Visibility = Visibility.Visible;
            RefreshStatus();
            try { SpeedSettingsButton.Focus(FocusState.Keyboard); } catch { }
        }

        private void CpuModelButton_Click(object sender, RoutedEventArgs e)
        {
            try { File.WriteAllText(CpuCfgPath, CpuChoices[(Array.IndexOf(CpuChoices, VmCpuModel) + 1) % CpuChoices.Length]); } catch { }
            RefreshStatus();
        }

        private void TbButton_Click(object sender, RoutedEventArgs e)
        {
            try { File.WriteAllText(TbCfgPath, TbChoices[(Array.IndexOf(TbChoices, VmTbMb) + 1) % TbChoices.Length].ToString()); } catch { }
            RefreshStatus();
        }

        private void CpusButton_Click(object sender, RoutedEventArgs e)
        {
            try { File.WriteAllText(SmpCfgPath, CpuCountChoices[(Array.IndexOf(CpuCountChoices, VmCpus) + 1) % CpuCountChoices.Length].ToString()); } catch { }
            RefreshStatus();
        }

        private void DiskModeButton_Click(object sender, RoutedEventArgs e)
        {
            try { File.WriteAllText(DiskModeCfgPath, ((VmDiskMode + 1) % DiskModeNames.Length).ToString()); } catch { }
            RefreshStatus();
        }

        private void ScreenSizeButton_Click(object sender, RoutedEventArgs e)
        {
            try { File.WriteAllText(ScreenCfgPath, ScreenChoices[(Array.IndexOf(ScreenChoices, VmScreen) + 1) % ScreenChoices.Length]); } catch { }
            RefreshStatus();
        }

        private void SpeedResetButton_Click(object sender, RoutedEventArgs e)
        {
            try { File.Delete(CpuCfgPath); } catch { }
            try { File.Delete(TbCfgPath); } catch { }
            try { File.Delete(SmpCfgPath); } catch { }
            try { File.Delete(DiskModeCfgPath); } catch { }
            try { File.Delete(ScreenCfgPath); } catch { }
            RefreshStatus();
        }

        private static string DiskPath
        {
            get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "vmdisk.img"); }
        }

        // Second "storage" disk (shows up in Linux as /dev/sdb). Made when the main disk already exists.
        private static string MediaPath
        {
            get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "media.img"); }
        }

        // New disk size, chosen with the size button and remembered in disk.cfg. 10 GB is the default.
        private static readonly int[] DiskChoices = { 10, 15, 20, 25, 30, 35, 40, 50 };
        private static string DiskCfgPath { get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "disk.cfg"); } }
        private static int NewDiskGb
        {
            get
            {
                try
                {
                    int v;
                    if (File.Exists(DiskCfgPath) && int.TryParse(File.ReadAllText(DiskCfgPath).Trim(), out v) && Array.IndexOf(DiskChoices, v) >= 0)
                        return v;
                }
                catch { }
                return 10;
            }
        }

        private void DiskSizeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int i = Array.IndexOf(DiskChoices, NewDiskGb);
                File.WriteAllText(DiskCfgPath, DiskChoices[(i + 1) % DiskChoices.Length].ToString());
            }
            catch { }
            RefreshStatus();
        }

        private static string SizeText(string path)
        {
            try
            {
                long gb = new FileInfo(path).Length / (1024L * 1024L * 1024L);
                return gb + " GB";
            }
            catch { return "?"; }
        }

        private static FileInfo NewestIso()
        {
            try
            {
                FileInfo[] isos = new DirectoryInfo(ApplicationData.Current.LocalFolder.Path).GetFiles("*.iso");
                if (isos.Length == 0) return null;
                Array.Sort(isos, (x, y) => y.LastWriteTimeUtc.CompareTo(x.LastWriteTimeUtc));
                return isos[0];
            }
            catch { return null; }
        }

        private void RefreshStatus()
        {
            try
            {
                FileInfo iso = NewestIso();
                StatusIso.Text = iso == null ? "none yet (use \"Pick ISO file\")"
                    : iso.Name + "  (" + (iso.Length / (1024 * 1024)) + " MB)";
                bool haveDisk = File.Exists(DiskPath);
                bool haveMedia = File.Exists(MediaPath);
                StatusDisk.Text = !haveDisk ? "not created yet"
                    : SizeText(DiskPath) + " main disk" + (haveMedia ? " + " + SizeText(MediaPath) + " storage disk" : "");
                StatusRam.Text = VmRamMb + " MB   (Xbox app limit " + AppLimitMb() + " MB)";
                StatusNet.Text = NetOn ? "ON (NAT, 10.0.2.15)" : "off";
                StatusSpeed.Text = SpeedText;
                CpuModelButton.Content = "CPU type: " + VmCpuModel + " (press to change)";
                TbButton.Content = "Code cache: " + VmTbMb + " MB (press to change)";
                CpusButton.Content = "Number of CPUs: " + VmCpus + " (press to change)";
                DiskModeButton.Content = "Disk type: " + DiskModeNames[VmDiskMode] + " (press to change)";
                ScreenSizeButton.Content = "Screen size: " + VmScreen + " (press to change)";
                SpeedNote.Text = _qemuUsed
                    ? "QEMU has already run since the app opened. Close the app and open it again to use new settings."
                    : "Defaults are the recommended settings. Change ONE setting at a time.";
                RamButton.Content = "Virtual RAM: " + VmRamMb + " MB (press to change)";
                NetToggleButton.Content = NetOn ? "Network: ON (press to turn off)" : "Network: off (press to turn on)";
                BootDiskButton.IsEnabled = haveDisk && !_qemuUsed;
                BootIsoButton.IsEnabled = iso != null && !_qemuUsed;
                bool haveState = StateValid();
                ResumeButton.IsEnabled = haveState && !_qemuUsed;
                ResumeButton.Content = haveState ? "Resume saved state (saved " + StateWhen() + ")" : "Resume saved state (none saved)";
                DiskSizeButton.Content = "Disk size: " + NewDiskGb + " GB";
                CreateDiskButton.Content = !haveDisk ? "Create virtual disk"
                    : (!haveMedia ? "Create storage disk" : "Disks created");
                CreateDiskButton.IsEnabled = !haveDisk || !haveMedia;
            }
            catch { }
        }

        // ---- start / stop ----
        private void DisableQemuButtons()
        {
            BootDiskButton.IsEnabled = false;
            BootIsoButton.IsEnabled = false;
            ResumeButton.IsEnabled = false;
        }

        private void CreateDiskButton_Click(object sender, RoutedEventArgs e)
        {
            ResetLog();
            int gb = NewDiskGb;
            try
            {
                bool main = !File.Exists(DiskPath);
                string path = main ? DiskPath : MediaPath;
                Log(main ? "=== CREATE VIRTUAL DISK (" + gb + " GB) ===" : "=== CREATE STORAGE DISK (" + gb + " GB) ===");
                if (File.Exists(path))
                {
                    Log("Both disks already exist, so nothing was changed.");
                    return;
                }
                using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                    fs.SetLength((long)gb * 1024L * 1024L * 1024L);
                if (main)
                {
                    Log("Created an empty " + gb + " GB virtual disk.");
                    Log("Ready. Now press \"Boot from ISO\" and install Linux onto it.");
                }
                else
                {
                    Log("Created an empty " + gb + " GB storage disk. Linux will see it as a second disk (/dev/sdb).");
                    Log("Next: press \"Start Linux from disk\", then format and mount it inside Linux.");
                }
            }
            catch (Exception ex) { Log("Could not create the disk: " + ex.GetType().Name + ": " + ex.Message); }
            RefreshStatus();
        }

        // "Export log" adds what is in the log box to ONE file. Every press appends a new dated section, so older logs stay in the file.
        private static string ExportFolderPath { get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "log-exports"); } }

        private void ExportLogButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string text = LogText.Text;
                if (string.IsNullOrWhiteSpace(text)) { Log("Nothing to export yet - the log is empty."); return; }
                Directory.CreateDirectory(ExportFolderPath);
                string name = "log-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt";
                File.WriteAllText(Path.Combine(ExportFolderPath, name), text.TrimEnd('\r', '\n') + "\r\n");
                Log("Log saved to: LocalState\\log-exports\\" + name);
            }
            catch (Exception ex) { Log("Could not export the log: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogText.Text = "Log cleared.\n";
            try { File.Delete(LogPath); } catch { }
            try { File.Delete(CrashPath); } catch { }
            try { File.Delete(QemuOutPath); } catch { }
        }

        private static bool MouseOn { get { return true; } }   // always on: the Xbox arrow is the only pointing device UWP apps get

        private void NetToggleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (NetOn) File.Delete(NetFlagPath);
                else File.WriteAllText(NetFlagPath, "on");
            }
            catch (Exception ex) { Log("Could not change the network setting: " + ex.Message); }
            RefreshStatus();
        }

        private static long AppLimitMb()
        {
            try { return (long)(MemoryManager.AppMemoryUsageLimit / (1024UL * 1024UL)); } catch { return 0; }
        }

        // Starts Linux from the virtual disk only (no ISO attached).
        private async void BootDiskButton_Click(object sender, RoutedEventArgs e)
        {
            if (!File.Exists(DiskPath))
            {
                ResetLog();
                Log("There is no virtual disk yet. Press \"Create virtual disk\" first, then install Linux onto it with \"Boot from ISO\".");
                return;
            }
            if (!await ConfirmDiscardState()) return;
            var extra = new List<string>();
            AddDisk(extra, DiskPath, 0, true);
            extra.AddRange(new string[] { "-boot", "order=c" });
            AddMedia(extra);
            AddNetwork(extra);
            AddPointer(extra);
            StartVm("START FROM DISK (virtual RAM " + VmRamMb + " MB, app limit " + AppLimitMb() + " MB)", VmRamMb.ToString(), extra.ToArray());
        }

        // Starts from the newest ISO. If the virtual disk exists it is attached too, so the installer can use it.
        private async void BootIsoButton_Click(object sender, RoutedEventArgs e)
        {
            FileInfo iso = NewestIso();
            if (iso == null)
            {
                ResetLog();
                Log("No .iso file found. Use \"Pick ISO file\" first.");
                return;
            }
            if (!await ConfirmDiscardState()) return;
            var extra = new List<string>();
            bool haveDisk = File.Exists(DiskPath);
            if (haveDisk) AddDisk(extra, DiskPath, 0, false);
            if (haveDisk) AddMedia(extra);
            extra.AddRange(new string[] { "-cdrom", iso.FullName, "-boot", "order=d,menu=on" });
            AddNetwork(extra);
            AddPointer(extra);
            StartVm("BOOT FROM ISO (" + iso.Name + ", virtual RAM " + VmRamMb + " MB, app limit " + AppLimitMb() + " MB" + (haveDisk ? ", with virtual disk" : "") + ")",
                VmRamMb.ToString(), extra.ToArray());
        }

        // Gives the virtual PC a network card (virtio) behind QEMU's built-in NAT.
        // Inside Linux it appears as eth0/ens3 with address 10.0.2.15, gateway 10.0.2.2, DNS 10.0.2.3.
        // The virtual PC gets a pointer that knows absolute positions (a tablet),
        // so the Xbox arrow and Debian's arrow stay together.
        // Attaches a disk file either as an old IDE drive or as a faster virtio drive (see Disk type in Settings).
        private void AddDisk(List<string> extra, string path, int index, bool bootFirst)
        {
            int mode = VmDiskMode;
            if (mode == 0)
            {
                extra.AddRange(new string[] { "-drive", "file=" + path + ",format=raw,if=ide,index=" + index });
                return;
            }
            string id = "hd" + index;
            string cache = mode == 2 ? "unsafe" : "writeback";
            extra.AddRange(new string[] { "-drive", "file=" + path + ",format=raw,if=none,id=" + id + ",cache=" + cache });
            extra.AddRange(new string[] { "-device", "virtio-blk-pci,drive=" + id + (bootFirst ? ",bootindex=0" : "") });
        }

        private void AddMedia(List<string> extra)
        {
            if (File.Exists(MediaPath)) AddDisk(extra, MediaPath, 1, false);
        }

        private void AddPointer(List<string> extra)
        {
            if (MouseOn) extra.AddRange(new string[] { "-device", "virtio-tablet-pci" });
        }

        private void AddNetwork(List<string> extra)
        {
            // romfile= (empty) because the network card's boot ROM file is not packaged and is not needed here.
            if (NetOn) extra.AddRange(new string[] { "-netdev", "user,id=n0", "-device", "virtio-net-pci,netdev=n0,romfile=" });
        }

        private void StartVm(string title, string memoryMb, string[] extra)
        {
            ResetLog();
            Log("=== " + title + " ===");
            if (_qemuUsed)
            {
                Log("QEMU has already run once in this app session. Close the app and open it again to start the virtual PC again.");
                return;
            }
            _qemuUsed = true;
            DisableQemuButtons();
            InitVm();

            string biosDir = Path.Combine(Windows.ApplicationModel.Package.Current.InstalledLocation.Path, "pc-bios");
            try { File.Delete(QemuOutPath); } catch { }

            var list = new List<string>();
            list.AddRange(new string[] {
                "qemu-system-x86_64",
                "-L", biosDir,
                "-M", "pc,usb=off,hpet=off",
                "-accel", "tcg,thread=" + (VmCpus > 1 ? "multi" : "single") + ",tb-size=" + VmTbMb,
                "-cpu", VmCpuModel,
                "-smp", VmCpus.ToString(),
                "-m", memoryMb,
                "-nodefaults",
                "-monitor", "none", "-serial", "none"
            });
            // "GPU": virtual graphics card (Virgl) drawn by the Xbox GPU through ANGLE (Direct3D 11). Everything else keeps the plain card.
            if (VmScreen == "GPU") list.AddRange(new string[] { "-display", "egl-headless,gl=es", "-device", "virtio-vga-gl" });
            else list.AddRange(new string[] { "-display", "none" });
            if (VmScreen == "GPU") { }
            else if (VmScreen == "Auto") list.AddRange(new string[] { "-vga", "std" });
            else
            {
                string[] wh = VmScreen.Split('x');
                list.AddRange(new string[] { "-device", "VGA,edid=on,xres=" + wh[0] + ",yres=" + wh[1] });
            }
            list.AddRange(extra);
            string[] args = list.ToArray();
            string[] savedArgs = (string[])args.Clone();
            for (int i = 0; i < savedArgs.Length; i++) if (savedArgs[i] == biosDir) savedArgs[i] = "{BIOS}";
            _curArgs = savedArgs;
            if (_resumeArgs != null)
            {
                // Remember the ORIGINAL arguments (without -incoming), so saving again after a resume stores the full list with the disks.
                _curArgs = new string[_resumeArgs.Length - 2];
                Array.Copy(_resumeArgs, _curArgs, _curArgs.Length);
                args = _resumeArgs;
                for (int i = 0; i < args.Length; i++) if (args[i] == "{BIOS}") args[i] = biosDir;
                _resumeArgs = null;
            }

            IntPtr outp = Marshal.StringToHGlobalAnsi(QemuOutPath);
            IntPtr argv = Marshal.AllocHGlobal(IntPtr.Size * (args.Length + 1));
            for (int i = 0; i < args.Length; i++)
                Marshal.WriteIntPtr(argv, i * IntPtr.Size, Marshal.StringToHGlobalAnsi(args[i]));
            Marshal.WriteIntPtr(argv, args.Length * IntPtr.Size, IntPtr.Zero);

            // Written to disk right now, so it survives a crash. Removed again when the run ends normally.
            string cmd = string.Join(" ", args);
            try { File.Delete(CrashPath); } catch { }
            try { File.WriteAllText(FlagPath, title + "\nCommand: " + cmd + "\nStarted: " + DateTime.Now.ToString("HH:mm:ss") + "\n"); } catch { }
            Log("Command: " + cmd);
            Log("Step 1: handing over to QEMU...");

            if (_frameBuf == null) _frameBuf = new byte[1280 * 1024 * 4];
            _frameSeq = 0;
            _bitmap = null;
            _gotFrame = false;
            _stopAsked = false;
            _mouseButtons = 0;
            _vmDone = false;
            _vmResult = -999;
            _tablet = MouseOn;
            _havePos = false; _havePosX = false; _gotCore = false; _gotRawMouse = false;
            _debugOn = false; VmDebug.Visibility = _debugOn ? Visibility.Visible : Visibility.Collapsed;

            _vmThread = new Thread(() =>
            {
                try { File.AppendAllText(LogPath, "Step 2: QEMU thread started\n"); } catch { }
                try { _vmResult = qemu_dll_main(outp, args.Length, argv); }
                catch (Exception ex) { _vmResult = -998; try { File.AppendAllText(LogPath, "QEMU call threw: " + ex.GetType().Name + ": " + ex.Message + "\n"); } catch { } }
                try { File.AppendAllText(LogPath, "Step 3: QEMU returned code " + _vmResult + "\n"); } catch { }
                _vmDone = true;
            }, 16 * 1024 * 1024);

            VmImage.Source = null;
            VmStatus.Text = "Starting the virtual PC...";
            MainGrid.Visibility = Visibility.Collapsed;
            CreditBox.Visibility = Visibility.Collapsed;
            VmGrid.Visibility = Visibility.Visible;
            _vmActive = true;
            if (!MouseOn) HideCursor();   // the Xbox arrow stays visible on purpose
            _vmThread.Start();
            if (_vmTimer != null) _vmTimer.Start();
            if (_resuming) WatchResume();
        }


        // ---- save state / resume state ----
        // One saved state at a time. It only matches the disk as it was at that moment,
        // so starting from the disk (or an ISO) throws it away, and resuming uses it up.
        private string[] _curArgs;
        private string[] _resumeArgs;
        private bool _resuming;
        private bool _saving;
        private volatile bool _saveOk;

        private static string StatePath { get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "vmstate.bin"); } }
        private static string StateArgsPath { get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "vmstate.args"); } }
        private static string StateInfoPath { get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "vmstate.info"); } }

        private static string DiskStamp()
        {
            var sb = new StringBuilder();
            foreach (string p in new string[] { DiskPath, MediaPath })
            {
                try
                {
                    if (File.Exists(p)) { var fi = new FileInfo(p); sb.Append(fi.Length).Append('/').Append(fi.LastWriteTimeUtc.Ticks); }
                    else sb.Append("none");
                }
                catch { sb.Append("?"); }
                sb.Append(';');
            }
            return sb.ToString();
        }

        private static string AppVersionText()
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return v.Major + "." + v.Minor + "." + v.Build + "." + v.Revision;
        }

        // info file lines: 0 = app version, 1 = disk stamp, 2 = time saved
        private static string[] StateInfo()
        {
            try { return File.ReadAllLines(StateInfoPath); } catch { return null; }
        }

        private static bool StateValid()
        {
            try
            {
                if (!File.Exists(StatePath) || !File.Exists(StateArgsPath)) return false;
                string[] info = StateInfo();
                if (info == null || info.Length < 3) return false;
                return info[0] == AppVersionText() && info[1] == DiskStamp();
            }
            catch { return false; }
        }

        private static string StateWhen()
        {
            string[] info = StateInfo();
            return info != null && info.Length >= 3 ? info[2] : "?";
        }

        private static void DeleteState()
        {
            try { File.Delete(StatePath); } catch { }
            try { File.Delete(StateArgsPath); } catch { }
            try { File.Delete(StateInfoPath); } catch { }
        }

        private async System.Threading.Tasks.Task<bool> ConfirmDiscardState()
        {
            // Only ask when there is a saved state that could really be resumed. Leftover scraps are removed quietly.
            if (!StateValid()) { DeleteState(); return true; }
            var dlg = new ContentDialog
            {
                Title = "Throw away the saved state?",
                Content = "Starting Linux from the disk replaces your saved state. The saved state cannot be used afterwards.",
                PrimaryButtonText = "Start and discard it",
                CloseButtonText = "Cancel"
            };
            ContentDialogResult r = ContentDialogResult.None;
            try { r = await dlg.ShowAsync(); } catch { return false; }
            if (r != ContentDialogResult.Primary) return false;
            DeleteState();
            RefreshStatus();
            return true;
        }

        private string Hmp(string cmd, int timeoutMs)
        {
            var sb = new StringBuilder(8192);
            int rc = qemu_dll_hmp(cmd, sb, sb.Capacity, timeoutMs);
            try { File.AppendAllText(LogPath, "> " + cmd + " => " + (rc == 0 ? sb.ToString().Trim() : "(timed out)") + "\n"); } catch { }
            return rc == 0 ? sb.ToString() : null;
        }

        private async void SaveState()
        {
            if (_saving || _stopAsked || !_vmActive || !_gotFrame || _resuming) { if (_resuming) ShowVmNote("Wait until the virtual PC has finished loading"); return; }
            if (_curArgs == null) return;
            _saving = true;
            VmStatus.Text = "Saving the state... please wait. The picture will freeze.";
            string path = StatePath;
            string result = null;
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    DeleteState();
                    if (Hmp("stop", 20000) == null) { result = "could not pause the virtual PC"; return; }
                    if (Hmp("migrate -d file:" + path, 20000) == null) { result = "QEMU did not answer"; Hmp("cont", 5000); return; }
                    DateTime t0 = DateTime.UtcNow;
                    while (true)
                    {
                        System.Threading.Thread.Sleep(400);
                        string st = Hmp("info migrate", 10000);
                        if (st == null) { result = "QEMU stopped answering"; return; }
                        if (st.Contains("Migration status: completed")) break;
                        if (st.Contains("Migration status: failed") || st.Contains("Migration status: cancelled"))
                        {
                            result = "QEMU reported: " + st.Replace("\r", " ").Replace("\n", " ");
                            Hmp("cont", 5000);
                            return;
                        }
                        if ((DateTime.UtcNow - t0).TotalSeconds > 300) { result = "it took longer than 5 minutes"; Hmp("cont", 5000); return; }
                    }
                    File.WriteAllLines(StateArgsPath, _curArgs);
                }
                catch (Exception ex) { result = ex.GetType().Name + ": " + ex.Message; }
            });
            if (result != null)
            {
                DeleteState();
                _saving = false;
                VmStatus.Text = "Saving failed: " + result;
                await System.Threading.Tasks.Task.Delay(12000);
                if (!_stopAsked) VmStatus.Text = "";
                return;
            }
            _saveOk = true;
            VmStatus.Text = "State saved. Returning to the menu...";
            StopVm();
        }

        // Runs after QEMU has closed, so the disk files are final.
        private void FinishSave()
        {
            _saveOk = false;
            _saving = false;
            try
            {
                File.WriteAllLines(StateInfoPath, new string[] { AppVersionText(), DiskStamp(), DateTime.Now.ToString("d MMM HH:mm") });
                Log("STATE SAVED (" + (new FileInfo(StatePath).Length / (1024 * 1024)) + " MB). Use \"Resume saved state\" in the menu to continue exactly here.");
            }
            catch (Exception ex) { Log("Could not finish saving the state: " + ex.Message); DeleteState(); }
        }

        private void ResumeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!StateValid())
            {
                ResetLog();
                Log("There is no usable saved state (it is only valid for the same app version and an unchanged disk).");
                RefreshStatus();
                return;
            }
            string[] a;
            try { a = File.ReadAllLines(StateArgsPath); }
            catch (Exception ex) { ResetLog(); Log("Could not read the saved state: " + ex.Message); return; }
            var list = new List<string>(a);
            list.Add("-incoming");
            list.Add("file:" + StatePath);
            _resumeArgs = list.ToArray();
            _resuming = true;
            StartVm("RESUME SAVED STATE (saved " + StateWhen() + ")", VmRamMb.ToString(), new string[0]);
            if (_resumeArgs != null) { _resumeArgs = null; _resuming = false; }
        }

        // Once QEMU says it is running again the state is loaded; from then on the disk changes, so the state is used up.
        private async void WatchResume()
        {
            VmStatus.Text = "Loading the saved state...";
            bool ok = false;
            DateTime t0 = DateTime.UtcNow;
            while (_vmActive && !_vmDone && (DateTime.UtcNow - t0).TotalSeconds < 300)
            {
                await System.Threading.Tasks.Task.Delay(1000);
                // QEMU must be fully started before we talk to it, or it crashes. The first picture means it is.
                if (!_gotFrame || (DateTime.UtcNow - t0).TotalSeconds < 6) continue;
                string st = null;
                try { st = await System.Threading.Tasks.Task.Run(() => Hmp("info status", 5000)); } catch { }
                if (st != null && st.Contains("running") && !st.Contains("paused")) { ok = true; break; }
                // The state was saved while paused, so QEMU loads it paused too. Once loading is done, tell it to carry on.
                if (st != null && st.Contains("paused") && !st.Contains("inmigrate"))
                    await System.Threading.Tasks.Task.Run(() => Hmp("cont", 10000));
            }
            if (ok)
            {
                DeleteState();
                _resuming = false;
                VmStatus.Text = "";
                Log("Saved state loaded and used up.");
            }
            else _resuming = false;
        }

        private void StopVm()
        {
            if (_stopAsked) return;
            _stopAsked = true;
            _stopAskedAt = DateTime.UtcNow;
            VmStatus.Text = "Stopping the virtual PC...";
            try { qemu_dll_stop(); } catch { }
        }

        private void EndVm()
        {
            if (!_vmActive) return;
            _vmActive = false;
            try { File.Delete(FlagPath); } catch { }
            if (_saveOk) FinishSave();
            if (_vmTimer != null) _vmTimer.Stop();
            ShowCursor();
            VmGrid.Visibility = Visibility.Collapsed;
            MainGrid.Visibility = Visibility.Visible;
            CreditBox.Visibility = Visibility.Visible;
            VmStatus.Text = "";
            FocusFirstButton();
            Log("");
            if (_vmDone) Log("The virtual PC stopped, code " + _vmResult + ". The app is still open.");
            else Log("The virtual PC did not stop within 15 s. Close the app and open it again.");
            if (!_gotFrame) Log("Note: no picture was ever received from the virtual screen.");
            Log("--- QEMU messages ---");
            try { Log(ReadSharedText(QemuOutPath)); } catch (Exception ex) { Log("(none: " + ex.Message + ")"); }
            Log("--- end ---");
            Log("Input seen by the app: " + _dbgKeysSent + " key presses, " + _dbgMouse + " raw mouse moves, " + _dbgPtr + " window pointer moves, " + _dbgXaml + " page pointer moves (last type " + _dbgType + ").");
            Log("Restarting the app so Linux can be started again...");
            RestartAppSoon();
        }

        // QEMU can only run once per app start, so the app restarts itself when the virtual PC stops.
        private async void RestartAppSoon()
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(2500);
                var r = await Windows.ApplicationModel.Core.CoreApplication.RequestRestartAsync("");
                if (r != Windows.ApplicationModel.Core.AppRestartFailureReason.RestartPending)
                    Log("The app could not restart itself (" + r + "). Close it and open it again to start Linux once more.");
            }
            catch (Exception ex)
            {
                Log("The app could not restart itself (" + ex.GetType().Name + "). Close it and open it again to start Linux once more.");
            }
        }

        private void HideCursor()
        {
            try
            {
                CoreWindow cw = Window.Current.CoreWindow;
                if (_savedCursor == null) _savedCursor = cw.PointerCursor;
                cw.PointerCursor = null;
            }
            catch { }
        }

        private void ShowCursor()
        {
            try
            {
                CoreWindow cw = Window.Current.CoreWindow;
                cw.PointerCursor = _savedCursor ?? new CoreCursor(CoreCursorType.Arrow, 0);
            }
            catch { }
        }

        // ---- every 16 ms: controller, screen, did QEMU end ----
        private void VmTick(object sender, object e)
        {
            if (!_vmActive) return;
            if (_vmDone) { EndVm(); return; }
            if (_stopAsked && (DateTime.UtcNow - _stopAskedAt).TotalSeconds > 15) { EndVm(); return; }
            if (_tablet) FlushAbs();
            if (_debugOn)
            {
                try { VmDebug.Text = "mouse  raw:" + _dbgMouse + "  window:" + _dbgPtr + "  page:" + _dbgXaml + "  type:" + _dbgType + "  keys:" + _dbgKeysSent; } catch { }
            }
            try { PullFrame(); } catch (Exception ex) { VmStatus.Text = "Screen error: " + ex.Message; }
        }

        private void PullFrame()
        {
            int w, h;
            int seq = _frameSeq;
            if (qemu_dll_get_frame(_frameBuf, _frameBuf.Length, out w, out h, ref seq) == 0) return;
            _frameSeq = seq;
            if (w <= 0 || h <= 0) return;
            _lastW = w; _lastH = h;
            if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
            {
                _bitmap = new WriteableBitmap(w, h);
                VmImage.Source = _bitmap;
            }
            using (Stream s = _bitmap.PixelBuffer.AsStream())
            {
                s.Write(_frameBuf, 0, w * h * 4);
            }
            _bitmap.Invalidate();
            if (!_gotFrame) { _gotFrame = true; if (!_stopAsked) VmStatus.Text = ""; }
        }

        // ---- real keyboard ----
        private bool KeyDownNow(CoreWindow w, VirtualKey k)
        {
            return (w.GetKeyState(k) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        }

        private string _dbgKey = "none yet";
        private int _dbgMouse;
        private int _dbgKeysSent;

        private int KeyNumberOf(VirtualKey vkey, CorePhysicalKeyStatus st)
        {
            int vk = (int)vkey;
            if (vk >= 195 && vk <= 218) return 0;   // controller buttons arrive as "keys"; ignore them
            uint sc = st.ScanCode;
            if (sc != 0 && sc < 0x80) return (int)sc | (st.IsExtendedKey ? 0x80 : 0);
            int n;
            if (_vkMap.TryGetValue(vkey, out n)) return n;
            return 0;
        }

        // Key presses arrive here before any on-screen control can take them.
        private void VmAccelerator(CoreDispatcher sender, AcceleratorKeyEventArgs e)
        {
            if (!_vmActive) { MenuAccelerator(e); return; }
            bool down;
            switch (e.EventType)
            {
                case CoreAcceleratorKeyEventType.KeyDown:
                case CoreAcceleratorKeyEventType.SystemKeyDown:
                    down = true; break;
                case CoreAcceleratorKeyEventType.KeyUp:
                case CoreAcceleratorKeyEventType.SystemKeyUp:
                    down = false; break;
                default:
                    return;
            }
            int vk = (int)e.VirtualKey;
            if (vk >= 195 && vk <= 218) return;
            CoreWindow w = Window.Current.CoreWindow;
            // Emergency stop from a real keyboard: Ctrl + Alt + Shift + Q
            if (down && e.VirtualKey == VirtualKey.Q && KeyDownNow(w, VirtualKey.Control) &&
                KeyDownNow(w, VirtualKey.Menu) && KeyDownNow(w, VirtualKey.Shift))
            {
                e.Handled = true;
                StopVm();
                return;
            }
            // Show or hide the mouse counters: Ctrl + Alt + Shift + D
            if (down && e.VirtualKey == VirtualKey.D && KeyDownNow(w, VirtualKey.Control) &&
                KeyDownNow(w, VirtualKey.Menu) && KeyDownNow(w, VirtualKey.Shift))
            {
                e.Handled = true;
                if (!e.KeyStatus.WasKeyDown) { _debugOn = !_debugOn; VmDebug.Visibility = _debugOn ? Visibility.Visible : Visibility.Collapsed; }
                return;
            }
            // Save the state of the virtual PC: Ctrl + Alt + Shift + V
            if (down && e.VirtualKey == VirtualKey.V && KeyDownNow(w, VirtualKey.Control) &&
                KeyDownNow(w, VirtualKey.Menu) && KeyDownNow(w, VirtualKey.Shift))
            {
                e.Handled = true;
                if (!e.KeyStatus.WasKeyDown) SaveState();
                return;
            }
            // Screenshot from a real keyboard: Ctrl + Alt + Shift + S
            if (down && e.VirtualKey == VirtualKey.S && KeyDownNow(w, VirtualKey.Control) &&
                KeyDownNow(w, VirtualKey.Menu) && KeyDownNow(w, VirtualKey.Shift))
            {
                e.Handled = true;
                if (!e.KeyStatus.WasKeyDown) SaveScreenshot();
                return;
            }
            e.Handled = true;
            int n = KeyNumberOf(e.VirtualKey, e.KeyStatus);
            _dbgKey = (down ? "down " : "up ") + e.VirtualKey + " scan=" + e.KeyStatus.ScanCode + " sent=" + n;
            if (down)
            {
                if (e.KeyStatus.WasKeyDown) return;      // the guest does its own key repeat
                if (n != 0) { qemu_dll_key(n, 1); _dbgKeysSent++; }
            }
            else
            {
                if (n != 0) qemu_dll_key(n, 0);
            }
        }

        // ---- Ctrl + Alt + Shift + S when no virtual PC is running (menu and Settings page) ----
        private void MenuAccelerator(AcceleratorKeyEventArgs e)
        {
            try
            {
                if (e.EventType != CoreAcceleratorKeyEventType.KeyDown && e.EventType != CoreAcceleratorKeyEventType.SystemKeyDown) return;
                if (e.VirtualKey != VirtualKey.S) return;
                CoreWindow w = Window.Current.CoreWindow;
                if (KeyDownNow(w, VirtualKey.Control) && KeyDownNow(w, VirtualKey.Menu) && KeyDownNow(w, VirtualKey.Shift))
                {
                    e.Handled = true;
                    if (!e.KeyStatus.WasKeyDown) SaveMenuScreenshot();
                }
            }
            catch { }
        }

        // Draws the page itself into a PNG (saved in LocalState\\screenshots), so it does not need the Xbox's own screen capture.
        private bool _menuShotBusy;
        private async void SaveMenuScreenshot()
        {
            if (_menuShotBusy) return;
            _menuShotBusy = true;
            try
            {
                var rtb = new RenderTargetBitmap();
                await rtb.RenderAsync(this);
                int w = rtb.PixelWidth, h = rtb.PixelHeight;
                if (w <= 0 || h <= 0) { Log("Screenshot failed: nothing to draw"); return; }
                Windows.Storage.Streams.IBuffer buf = await rtb.GetPixelsAsync();
                byte[] pixels = buf.ToArray();
                StorageFolder dir = await ApplicationData.Current.LocalFolder.CreateFolderAsync("screenshots", CreationCollisionOption.OpenIfExists);
                string name = "shot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png";
                StorageFile f = await dir.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
                using (var stream = await f.OpenAsync(FileAccessMode.ReadWrite))
                {
                    var enc = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
                    enc.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
                                     (uint)w, (uint)h, 96, 96, pixels);
                    await enc.FlushAsync();
                }
                Log("Screenshot saved: screenshots\\" + name);
            }
            catch (Exception ex)
            {
                Log("Screenshot failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { _menuShotBusy = false; }
        }

        // ---- screenshot of the virtual screen (saved in LocalState\\screenshots) ----
        private async void SaveScreenshot()
        {
            try
            {
                int w = _lastW, h = _lastH;
                if (w <= 0 || h <= 0 || _frameBuf == null) { ShowVmNote("No picture to save yet"); return; }
                byte[] copy = new byte[w * h * 4];
                Array.Copy(_frameBuf, copy, copy.Length);
                StorageFolder dir = await ApplicationData.Current.LocalFolder.CreateFolderAsync("screenshots", CreationCollisionOption.OpenIfExists);
                string name = "shot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png";
                StorageFile f = await dir.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
                using (var stream = await f.OpenAsync(FileAccessMode.ReadWrite))
                {
                    var enc = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
                    enc.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
                                     (uint)w, (uint)h, 96, 96, copy);
                    await enc.FlushAsync();
                }
                try { File.AppendAllText(LogPath, "Screenshot saved: screenshots\\" + name + "\n"); } catch { }
                ShowVmNote("Screenshot saved: " + name);
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(LogPath, "Screenshot failed: " + ex.GetType().Name + ": " + ex.Message + "\n"); } catch { }
                ShowVmNote("Screenshot failed: " + ex.GetType().Name);
            }
        }

        private async void ShowVmNote(string text)
        {
            VmStatus.Text = text;
            await System.Threading.Tasks.Task.Delay(2500);
            if (VmStatus.Text == text) VmStatus.Text = "";
        }

        // ---- real mouse ----
        private void SendMouse(int dx, int dy, int wheel)
        {
            qemu_dll_mouse(dx, dy, wheel, _mouseButtons);
        }

        // Fallback: if the raw mouse feed (MouseMoved) never arrives, work out movement from pointer positions.
        private bool _gotRawMouse, _havePos;
        private Windows.Foundation.Point _lastPos;
        private bool _tablet;
        private int _dbgPtr, _dbgXaml;
        private bool _gotCore, _havePosX, _debugOn;
        private Windows.Foundation.Point _lastPosX;
        private string _dbgType = "none";
        private void VmPointerMoved(CoreWindow sender, PointerEventArgs e)
        {
            if (!_vmActive) return;
            _dbgPtr++;
            _dbgType = e.CurrentPoint.PointerDevice.PointerDeviceType.ToString();
            if (e.CurrentPoint.PointerDevice.PointerDeviceType == PointerDeviceType.Touch) return;
            _gotCore = true;
            if (_tablet) return;
            var p = e.CurrentPoint.Position;
            if (_havePos && !_gotRawMouse)
            {
                double k = 1.0;
                try { k = Windows.Graphics.Display.DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel; } catch { }
                int dx = (int)Math.Round((p.X - _lastPos.X) * k), dy = (int)Math.Round((p.Y - _lastPos.Y) * k);
                if (dx != 0 || dy != 0) SendMouse(dx, dy, 0);
            }
            _lastPos = p; _havePos = true;
        }

        private void VmMouseMoved(MouseDevice sender, MouseEventArgs args)
        {
            if (!_vmActive) return;
            _gotRawMouse = true;
            if (_tablet) return;
            int dx = args.MouseDelta.X, dy = args.MouseDelta.Y;
            _dbgMouse++;
            if (dx != 0 || dy != 0) SendMouse(dx, dy, 0);
        }

        private void VmPointerChanged(CoreWindow sender, PointerEventArgs e)
        {
            if (!_vmActive) return;
            if (_tablet) return;
            if (e.CurrentPoint.PointerDevice.PointerDeviceType == PointerDeviceType.Touch) return;
            var p = e.CurrentPoint.Properties;
            int b = 0;
            if (p.IsLeftButtonPressed) b |= 1;
            if (p.IsRightButtonPressed) b |= 2;
            if (p.IsMiddleButtonPressed) b |= 4;
            _mouseButtons = b;
            SendMouse(0, 0, 0);
        }

        // Turns a spot on the Xbox screen into a spot on the virtual screen (the picture is centred with black bars).
        private void SendAbs(Windows.Foundation.Point p, int wheel)
        {
            double W = VmGrid.ActualWidth, H = VmGrid.ActualHeight;
            if (W <= 0 || H <= 0 || _lastW <= 0 || _lastH <= 0) return;
            double scale = Math.Min(W / _lastW, H / _lastH);
            double dw = _lastW * scale, dh = _lastH * scale;
            double fx = (p.X - (W - dw) / 2) / dw, fy = (p.Y - (H - dh) / 2) / dh;
            fx = Math.Max(0, Math.Min(1, fx)); fy = Math.Max(0, Math.Min(1, fy));
            _absX = (int)(fx * 32767); _absY = (int)(fy * 32767);
            if (wheel != 0) _absWheel = wheel;
            _absDirty = true;
        }

        // The newest pointer position goes to the virtual PC at most once per timer tick, not once per Xbox event:
        // the emulated processor is slow, and a flood of tiny moves makes the pointer lag.
        private int _absX, _absY, _absWheel;
        private bool _absDirty;
        private void FlushAbs()
        {
            if (!_absDirty) return;
            _absDirty = false;
            int w = _absWheel; _absWheel = 0;
            qemu_dll_abs(_absX, _absY, w, _mouseButtons);
        }

        // Same job again, from the page itself. Used only when the window-level feeds above stay silent.
        private void VmGridPointerMoved(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!_vmActive) return;
            _dbgXaml++;
            if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch) return;
            var p = e.GetCurrentPoint(VmGrid).Position;
            if (_tablet) { SendAbs(p, 0); return; }
            if (_havePosX && !_gotRawMouse && !_gotCore)
            {
                double k = 1.0;
                try { k = Windows.Graphics.Display.DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel; } catch { }
                int dx = (int)Math.Round((p.X - _lastPosX.X) * k), dy = (int)Math.Round((p.Y - _lastPosX.Y) * k);
                if (dx != 0 || dy != 0) SendMouse(dx, dy, 0);
            }
            _lastPosX = p; _havePosX = true;
        }

        private void VmGridButtons(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!_vmActive) return;
            if (!_tablet && (_gotCore || _gotRawMouse)) return;
            if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch) return;
            var p = e.GetCurrentPoint(VmGrid).Properties;
            int b = 0;
            if (p.IsLeftButtonPressed) b |= 1;
            if (p.IsRightButtonPressed) b |= 2;
            if (p.IsMiddleButtonPressed) b |= 4;
            _mouseButtons = b;
            if (_tablet) { SendAbs(e.GetCurrentPoint(VmGrid).Position, 0); _absDirty = true; FlushAbs(); return; }
            SendMouse(0, 0, 0);
        }

        private void VmGridWheel(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!_vmActive) return;
            int d = e.GetCurrentPoint(VmGrid).Properties.MouseWheelDelta;
            if (_tablet) { SendAbs(e.GetCurrentPoint(VmGrid).Position, d > 0 ? 1 : d < 0 ? -1 : 0); FlushAbs(); return; }
            if (_gotCore || _gotRawMouse) return;
            if (d != 0) SendMouse(0, 0, d > 0 ? 1 : -1);
        }

        private void VmPointerWheel(CoreWindow sender, PointerEventArgs e)
        {
            if (!_vmActive) return;
            if (_tablet) return;
            if (e.CurrentPoint.PointerDevice.PointerDeviceType != PointerDeviceType.Mouse) return;
            int d = e.CurrentPoint.Properties.MouseWheelDelta;
            if (d != 0) SendMouse(0, 0, d > 0 ? 1 : -1);
        }

        private void VmBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (!_vmActive) return;
            e.Handled = true;
        }

        // ---- Button: pick ISO ----
        private async void PickIsoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
                picker.ViewMode = PickerViewMode.List;
                picker.FileTypeFilter.Add(".iso");
                picker.FileTypeFilter.Add(".img");

                StorageFile file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    Log("ISO pick cancelled.");
                    return;
                }

                var props = await file.GetBasicPropertiesAsync();
                Log("Picked: " + file.Name + "  (" + (props.Size / (1024 * 1024)) + " MB)");
                Log("Copying into the app's storage folder...");

                Stopwatch sw = Stopwatch.StartNew();
                StorageFolder local = ApplicationData.Current.LocalFolder;
                StorageFile copy = await file.CopyAsync(local, file.Name, NameCollisionOption.ReplaceExisting);
                sw.Stop();

                Log("Copied OK in " + sw.Elapsed.TotalSeconds.ToString("F1") + " s");
                Log("Now at: " + copy.Path);
                RefreshStatus();
            }
            catch (Exception ex)
            {
                Log("ISO pick/copy FAILED: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---- Button: list storage folder ----
        private async void ListFilesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StorageFolder local = ApplicationData.Current.LocalFolder;
                Log("Storage folder: " + local.Path);
                var files = await local.GetFilesAsync();
                if (files.Count == 0)
                {
                    Log("(empty)");
                    return;
                }
                foreach (var f in files)
                {
                    var p = await f.GetBasicPropertiesAsync();
                    Log(" - " + f.Name + "  (" + (p.Size >= 1024 * 1024 ? (p.Size / (1024 * 1024)) + " MB" : ((p.Size + 1023) / 1024) + " KB") + ")");
                }
            }
            catch (Exception ex)
            {
                Log("List failed: " + ex.Message);
            }
        }
    }
}
