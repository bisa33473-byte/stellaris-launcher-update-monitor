// Stellaris / Paradox 启动器更新监控（C# WinForms 单文件版）
// 编译: build.ps1 (csc /target:winexe)
//
// 功能:
//   * 读 %LOCALAPPDATA%\Paradox Interactive\launcher-v2\logs\cpatch*.log
//     显示更新包大小 / 百分比 / 实时速度 / 剩余时间
//   * 常驻后台, 只在 "Paradox Launcher.exe" 运行时显示小窗, 关掉启动器自动隐藏
//   * 不修改启动器, 只读日志
//
// 参数:
//   --show              立即显示(不管启动器在不在跑), 不自动隐藏
//   --snapshot <png>    渲染一张界面截图后退出(调试用)
//   --log <file>        配合 --snapshot, 回放指定日志
//   --state <file>      把解析结果写入文本后退出(调试用)
//   --install           复制到 %LOCALAPPDATA% 并加入开机启动
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace StellarisUpdateMonitor
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string snapshot = ArgValue(args, "--snapshot");
            string stateFile = ArgValue(args, "--state");
            string logPath = ArgValue(args, "--log");
            bool forceShow = HasArg(args, "--show");

            if (HasArg(args, "--install"))
            {
                Installer.Run();
                return;
            }

            if (HasArg(args, "--uninstall"))
            {
                Installer.Uninstall();
                return;
            }

            if (snapshot != null || stateFile != null)
            {
                var mon = new CpatchMonitor(logPath);
                mon.Update();
                State st = mon.GetState();

                if (stateFile != null)
                {
                    File.WriteAllText(stateFile, Describe(st), new UTF8Encoding(false));
                }
                if (snapshot != null)
                {
                    using (var form = new MonitorForm())
                    {
                        form.SetState(st);
                        form.SaveSnapshot(snapshot);
                    }
                }
                return;
            }

            // 单实例: 已有常驻实例时, 通知它显示一下, 然后退出
            bool createdNew;
            using (var mutex = new Mutex(true, @"Local\StellarisUpdateMonitorExe", out createdNew))
            {
                if (!createdNew)
                {
                    try
                    {
                        using (var ev = EventWaitHandle.OpenExisting(@"Local\StellarisUpdateMonitorShow"))
                        {
                            ev.Set();
                        }
                    }
                    catch { }
                    return;
                }

                EventWaitHandle showEvent;
                try
                {
                    showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\StellarisUpdateMonitorShow");
                }
                catch { showEvent = null; }

                using (var form = new MonitorForm())
                {
                    form.ShowEvent = showEvent;
                    form.AutoMode = !forceShow;
                    if (forceShow) form.ShowNow();
                    Application.Run(form);
                }
            }
        }

        private static string Describe(State s)
        {
            var ci = CultureInfo.InvariantCulture;
            if (s == null) return "idle";
            return string.Format(ci,
                "pct={0:0.0} done={1} total={2} speed={3:0.0} logSpeed={4:0.0} fresh={5} finished={6} completed={7} patching={8} patch={9}/{10} ver={11} launcher={12} file={13}",
                s.Pct, s.Done, s.Total, s.SpeedKB, s.LogSpeed, s.Fresh, s.Finished, s.Completed, s.Patching,
                s.PatchDone, s.PatchTotal, s.Version, s.IsLauncher, Path.GetFileName(s.File));
        }

        private static bool HasArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string ArgValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }

    internal sealed class Entry
    {
        public long Done;
        public long Total;
        public DateTime Ts;
        public double LogSpeed;
    }

    internal sealed class Sample
    {
        public DateTime T;
        public long Done;
    }

    internal sealed class State
    {
        public string File = "";
        public string Version = "";
        public long Done;
        public long Total;
        public double Pct;
        public double SpeedKB;
        public double LogSpeed;
        public double Eta = -1;
        public DateTime Ts;
        public bool Fresh;
        public bool Finished;
        public bool Completed;
        public bool Patching;
        public int PatchDone;
        public int PatchTotal;
        public bool IsLauncher;
    }

    internal sealed class CpatchMonitor
    {
        private static readonly Regex ReProgress = new Regex(
            @"\[daemon\] \[DEBUG\] (?<file>.+?): (?<done>\d+) / (?<total>\d+) \((?<pct>\d+)%\)\s*$",
            RegexOptions.Compiled);
        private static readonly Regex ReSpeed = new Regex(
            @"\[repository\] \[DEBUG\] Download speed for (?<file>.+?): (?<spd>[\d.]+) Kb/s",
            RegexOptions.Compiled);
        private static readonly Regex RePatch = new Regex(
            @"apply-patch: (?<n>\d+) out of (?<m>\d+) files done", RegexOptions.Compiled);
        private static readonly Regex ReDone = new Regex(
            @"SEND: (?<op>[a-z0-9\-]+) done", RegexOptions.Compiled);
        private static readonly Regex ReCmd = new Regex(
            "CMD: .*?\"repository\":\"(?<repo>[^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex ReVer = new Regex(
            @"(?<ver>\d{4}\.\d+(?:\.\d+)?)/", RegexOptions.Compiled);

        private readonly string _logDir;
        private readonly string _replayLog;
        private readonly bool _replay;
        private DateTime _replayNow = DateTime.MinValue;

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Sample>> _samples = new Dictionary<string, List<Sample>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastWrite = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private bool _patchActive;
        private DateTime _patchAt = DateTime.MinValue;
        private int _patchDone;
        private int _patchTotal;
        private DateTime _doneAt = DateTime.MinValue;
        private string _lastRepo = "";

        public CpatchMonitor(string replayLog)
        {
            _logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Paradox Interactive\launcher-v2\logs");
            if (!string.IsNullOrEmpty(replayLog))
            {
                _replay = true;
                _replayLog = replayLog;
            }
        }

        public string LogDirectory { get { return _logDir; } }

        private IEnumerable<string> GetLogFiles()
        {
            if (_replay) return new string[] { _replayLog };
            try
            {
                if (!Directory.Exists(_logDir)) return new string[0];
                return Directory.GetFiles(_logDir, "cpatch*.log");
            }
            catch { return new string[0]; }
        }

        public void Update()
        {
            foreach (string path in GetLogFiles()) ReadAppended(path);
            if (_replay && _replayNow == DateTime.MinValue)
            {
                DateTime latest = DateTime.MinValue;
                foreach (var e in _entries.Values) if (e.Ts > latest) latest = e.Ts;
                if (latest != DateTime.MinValue) _replayNow = latest;
            }
        }

        private void ReadAppended(string path)
        {
            DateTime writeTime;
            try { writeTime = File.GetLastWriteTimeUtc(path); }
            catch { return; }

            DateTime prev;
            if (_lastWrite.TryGetValue(path, out prev) && prev == writeTime) return;

            string text = null;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long off;
                    if (!_offsets.TryGetValue(path, out off))
                    {
                        off = _replay ? 0 : Math.Max(0, fs.Length - 400000);
                    }
                    if (fs.Length < off) off = 0;
                    fs.Position = off;
                    using (var sr = new StreamReader(fs, Encoding.UTF8, false, 8192, true))
                    {
                        text = sr.ReadToEnd();
                    }
                    _offsets[path] = fs.Position;
                }
                _lastWrite[path] = writeTime;
            }
            catch { return; }

            if (string.IsNullOrEmpty(text)) return;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length < 24) continue;
                ParseLine(line);
            }
        }

        private void ParseLine(string line)
        {
            Match m = ReCmd.Match(line);
            if (m.Success)
            {
                _lastRepo = m.Groups["repo"].Value;
                _patchActive = false;
                return;
            }

            m = ReProgress.Match(line);
            if (m.Success)
            {
                if (_patchActive) return; // 打补丁时每个文件都会报进度, 只要整包
                string file = m.Groups["file"].Value;
                long done = long.Parse(m.Groups["done"].Value, CultureInfo.InvariantCulture);
                long total = long.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture);
                DateTime ts = ParseTs(line);

                // 元数据文件（repository.json / manifest / 签名）不是更新包，忽略
                if (total < 131072) return;
                if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return;
                if (file.EndsWith(".asc", StringComparison.OrdinalIgnoreCase)) return;
                if (file.EndsWith("/manifest", StringComparison.OrdinalIgnoreCase) || file == "manifest") return;

                Entry e;
                if (!_entries.TryGetValue(file, out e)) { e = new Entry(); _entries[file] = e; }
                e.Done = done; e.Total = total; e.Ts = ts;

                List<Sample> list;
                if (!_samples.TryGetValue(file, out list))
                {
                    list = new List<Sample>();
                    _samples[file] = list;
                }
                if (list.Count == 0 || list[list.Count - 1].Done != done || list[list.Count - 1].T != ts)
                {
                    list.Add(new Sample { T = ts, Done = done });
                    while (list.Count > 60) list.RemoveAt(0);
                    while (list.Count > 2 && (list[list.Count - 1].T - list[0].T).TotalSeconds > 14) list.RemoveAt(0);
                }
                return;
            }

            m = ReSpeed.Match(line);
            if (m.Success)
            {
                string file = m.Groups["file"].Value;
                Entry e;
                if (_entries.TryGetValue(file, out e))
                {
                    double v;
                    if (double.TryParse(m.Groups["spd"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) e.LogSpeed = v;
                }
                return;
            }

            m = RePatch.Match(line);
            if (m.Success)
            {
                _patchActive = true;
                _patchAt = ParseTs(line);
                _patchDone = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
                _patchTotal = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
                return;
            }

            m = ReDone.Match(line);
            if (m.Success)
            {
                _patchActive = false;
                _doneAt = ParseTs(line);
            }
        }

        private static DateTime ParseTs(string line)
        {
            string s = line.Length >= 19 ? line.Substring(0, 19) : line;
            DateTime t;
            if (DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out t)) return t;
            return DateTime.Now;
        }

        public State GetState()
        {
            DateTime now = _replay && _replayNow != DateTime.MinValue ? _replayNow : DateTime.Now;
            if (_entries.Count == 0) return null;

            DateTime latestTs = DateTime.MinValue;
            string latestKey = null;
            foreach (var kv in _entries)
            {
                if (kv.Value.Ts > latestTs) { latestTs = kv.Value.Ts; latestKey = kv.Key; }
            }
            if (latestKey == null) return null;
            if ((now - latestTs).TotalSeconds > 900) return null;

            string bestKey = null;
            Entry best = null;
            foreach (var kv in _entries)
            {
                if ((latestTs - kv.Value.Ts).TotalSeconds > 120) continue;
                if (best == null || kv.Value.Total > best.Total) { best = kv.Value; bestKey = kv.Key; }
            }
            if (best == null) { best = _entries[latestKey]; bestKey = latestKey; }

            double speed = 0;
            List<Sample> list;
            if (_samples.TryGetValue(bestKey, out list) && list.Count >= 2)
            {
                double dt = (list[list.Count - 1].T - list[0].T).TotalSeconds;
                long bytes = list[list.Count - 1].Done - list[0].Done;
                if (dt > 0.5 && bytes >= 0) speed = bytes / dt / 1024.0;
            }

            long remaining = Math.Max(0, best.Total - best.Done);
            double eta = speed > 1 ? remaining / (speed * 1024.0) : -1;

            bool fresh = (now - best.Ts).TotalSeconds < 6;
            bool completed = _doneAt != DateTime.MinValue && _doneAt >= best.Ts &&
                             (now - _doneAt).TotalSeconds < 900;
            bool patching = !completed && _patchAt != DateTime.MinValue && _patchAt >= best.Ts &&
                            (now - _patchAt).TotalSeconds < 20;

            string ver = "";
            Match vm = ReVer.Match(bestKey);
            if (vm.Success) ver = vm.Groups["ver"].Value;

            return new State
            {
                File = bestKey,
                Version = ver,
                Done = best.Done,
                Total = best.Total,
                Pct = Math.Round(100.0 * best.Done / Math.Max(1, best.Total), 1),
                SpeedKB = Math.Round(speed, 1),
                LogSpeed = Math.Round(best.LogSpeed, 1),
                Eta = eta,
                Ts = best.Ts,
                Fresh = fresh,
                Finished = best.Total > 0 && best.Done >= best.Total,
                Completed = completed,
                Patching = patching,
                PatchDone = _patchDone,
                PatchTotal = _patchTotal,
                IsLauncher = _lastRepo.StartsWith("launcher-v2", StringComparison.OrdinalIgnoreCase) ||
                             Regex.IsMatch(bestKey, @"^\d{4}\.\d+")
            };
        }
    }

    internal sealed class MonitorForm : Form
    {
        private const int BaseWidth = 368;
        private const int BaseHeight = 122;

        private static readonly Color BgColor = Color.FromArgb(25, 28, 38);
        private static readonly Color AccentColor = Color.FromArgb(217, 184, 120);
        private static readonly Color TitleColor = Color.FromArgb(237, 230, 214);
        private static readonly Color DimColor = Color.FromArgb(176, 255, 255, 255);
        private static readonly Color FaintColor = Color.FromArgb(140, 255, 255, 255);

        private readonly CpatchMonitor _monitor = new CpatchMonitor(null);
        private readonly System.Windows.Forms.Timer _uiTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _watchTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _hintTimer = new System.Windows.Forms.Timer();

        private State _state;
        private float _scale = 1f;
        private bool _positioned;
        private bool _suppressed;
        private bool _dragging;
        private Point _dragStart;
        private Rectangle _closeRect;
        private string _lastSignature = "\0";

        public EventWaitHandle ShowEvent;
        public bool AutoMode = true;

        public MonitorForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            BackColor = BgColor;
            Text = "Stellaris 更新监控";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

            _scale = GetScreenScale();
            ClientSize = new Size(Px(BaseWidth), Px(BaseHeight));
            ApplyRoundRegion();
            PositionNearCorner();

            var menu = new ContextMenuStrip();
            menu.Items.Add("刷新", null, delegate { _lastSignature = "\0"; Refresh(); });
            menu.Items.Add("隐藏本次", null, delegate { HideWidget(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出监控", null, delegate { Application.Exit(); });
            ContextMenuStrip = menu;

            _uiTimer.Interval = 1000;
            _uiTimer.Tick += delegate { UiTick(); };
            _uiTimer.Start();

            _hintTimer.Interval = 8000;
            _hintTimer.Tick += delegate
            {
                _hintTimer.Stop();
                if (AutoMode && !LauncherRunning()) Hide();
            };

            _watchTimer.Interval = 3000;
            _watchTimer.Tick += delegate { WatchTick(); };
            _watchTimer.Start();
        }

        private int Px(int dip) { return (int)Math.Round(dip * _scale); }
        private float PxF(float dip) { return dip * _scale; }

        private static float GetScreenScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
            }
            catch { return 1f; }
        }

        private void ApplyRoundRegion()
        {
            using (GraphicsPath path = Rounded(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), PxF(9f)))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        public void SetState(State s)
        {
            _state = s;
            Refresh();
        }

        public void ShowNow()
        {
            _suppressed = false;
            if (!_positioned) PositionNearCorner();
            Show();
            BringToFront();
            Activate();
            _monitor.Update();
            _state = _monitor.GetState();
            _lastSignature = "\0";
            Invalidate();
        }

        private void PositionNearCorner()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Left = wa.Left + Px(24);
            Top = wa.Bottom - Height - Px(96);
            _positioned = true;
        }

        private static bool LauncherRunning()
        {
            try
            {
                Process[] ps = Process.GetProcessesByName("Paradox Launcher");
                bool any = ps.Length > 0;
                for (int i = 0; i < ps.Length; i++) ps[i].Dispose();
                return any;
            }
            catch { return false; }
        }

        private void WatchTick()
        {
            bool running = LauncherRunning();

            if (ShowEvent != null)
            {
                try
                {
                    if (ShowEvent.WaitOne(0)) { AutoMode = false; _suppressed = false; ShowNow(); }
                }
                catch { }
            }

            if (!AutoMode) return;

            if (running)
            {
                if (!Visible && !_suppressed) ShowNow();
            }
            else
            {
                _suppressed = false;
                if (Visible) Hide();
            }
        }

        private void UiTick()
        {
            if (!Visible) return;
            _monitor.Update();
            _state = _monitor.GetState();

            string sig = Signature(_state);
            if (sig != _lastSignature)
            {
                _lastSignature = sig;
                Invalidate();
            }
        }

        private static string Signature(State s)
        {
            if (s == null) return "idle";
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0}|{1}|{2}|{3:0.0}|{4}|{5}|{6}|{7}",
                s.Pct, s.Done, s.Total, s.SpeedKB, s.Fresh, s.Completed, s.Patching, s.PatchDone);
        }

        private void HideWidget()
        {
            _suppressed = true;
            Hide();
        }

        // ---------------------------------------------------------------- paint
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            Render(e.Graphics, _scale);
        }

        public void SaveSnapshot(string path)
        {
            using (var bmp = new Bitmap(BaseWidth, BaseHeight))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(BgColor);
                Render(g, 1f);
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        private void Render(Graphics g, float s)
        {
            int w = (int)Math.Round(BaseWidth * s);
            int h = (int)Math.Round(BaseHeight * s);

            using (var bg = new SolidBrush(BgColor)) g.FillRectangle(bg, 0, 0, w, h);
            using (var border = new Pen(Color.FromArgb(102, 217, 184, 120), Math.Max(1f, PxF(1f))))
            using (GraphicsPath path = Rounded(new RectangleF(0.5f, 0.5f, w - 1f, h - 1f), PxF(9f)))
            {
                g.DrawPath(border, path);
            }

            State st = _state;
            string title, pctText, sizeText, status;

            if (st == null)
            {
                title = "启动器更新监控";
                pctText = "—";
                sizeText = "等待更新";
                status = "已开始监视 · 启动器打开时自动出现";
            }
            else
            {
                if (st.IsLauncher) title = st.Version.Length > 0 ? "启动器更新 · " + st.Version : "启动器更新";
                else title = st.Version.Length > 0 ? "内容下载 · " + st.Version : "内容下载";

                pctText = st.Pct.ToString("0.0", CultureInfo.InvariantCulture) + "%";
                sizeText = FormatSize(st.Done) + " / " + FormatSize(st.Total);

                if (st.Completed) status = "更新已安装 · 重启启动器后生效";
                else if (st.Patching) status = "下载完成，正在应用补丁 " + st.PatchDone + " / " + st.PatchTotal + "…";
                else if (st.Finished) status = "下载完成，正在解压安装…";
                else if (st.Fresh)
                {
                    double speed = st.SpeedKB > 0.5 ? st.SpeedKB : st.LogSpeed;
                    string speedText = FormatSpeed(speed);
                    string eta = FormatEta(st.Eta);
                    status = eta.Length > 0 ? speedText + " · 还剩 " + eta : speedText;
                }
                else status = "超过 6 秒没有新数据（可能暂停或已进入安装阶段）";
            }

            using (var titleFont = new Font("Microsoft YaHei UI", 9f * (s > 1 ? 1f : 1f), FontStyle.Bold, GraphicsUnit.Point))
            using (var pctFont = new Font("Microsoft YaHei UI", 17f, FontStyle.Bold, GraphicsUnit.Point))
            using (var sizeFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point))
            using (var statusFont = new Font("Microsoft YaHei UI", 8.25f, FontStyle.Regular, GraphicsUnit.Point))
            using (var titleBrush = new SolidBrush(TitleColor))
            using (var accentBrush = new SolidBrush(AccentColor))
            using (var dimBrush = new SolidBrush(DimColor))
            using (var faintBrush = new SolidBrush(FaintColor))
            {
                TextRenderer.DrawText(g, title, titleFont, new Point(Px((int)14), Px(9)), TitleColor,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

                // 关闭按钮
                Size closeSize = TextRenderer.MeasureText(g, "✕", titleFont, new Size(0, 0), TextFormatFlags.NoPadding);
                int closeX = w - Px(14) - closeSize.Width;
                TextRenderer.DrawText(g, "✕", titleFont, new Point(closeX, Px(9)), FaintColor,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                _closeRect = new Rectangle(closeX - Px(4), Px(4), closeSize.Width + Px(8), closeSize.Height + Px(10));

                // 百分比
                Point pctPt = new Point(Px(13), Px(27));
                TextRenderer.DrawText(g, pctText, pctFont, pctPt, AccentColor,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                Size pctSize = TextRenderer.MeasureText(g, pctText, pctFont, new Size(0, 0), TextFormatFlags.NoPadding);
                Size sizeSize = TextRenderer.MeasureText(g, sizeText, sizeFont, new Size(0, 0), TextFormatFlags.NoPadding);
                int sizeY = pctPt.Y + pctSize.Height - sizeSize.Height - Px(4);
                TextRenderer.DrawText(g, sizeText, sizeFont, new Point(pctPt.X + pctSize.Width + Px(9), sizeY),
                    DimColor, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

                // 进度条
                int barX = Px(14), barY = Px(78), barW = w - Px(28), barH = Math.Max(3, Px(5));
                using (GraphicsPath track = Rounded(new RectangleF(barX, barY, barW, barH), barH / 2f))
                using (var trackBrush = new SolidBrush(Color.FromArgb(48, 255, 255, 255)))
                {
                    g.FillPath(trackBrush, track);
                }
                double pct = st == null ? 0 : st.Pct;
                double fillW = barW * Math.Max(0, Math.Min(100, pct)) / 100.0;
                if (fillW > 0.5)
                {
                    using (GraphicsPath fill = Rounded(new RectangleF(barX, barY, (float)fillW, barH), barH / 2f))
                    using (var fillBrush = new SolidBrush(AccentColor))
                    {
                        g.FillPath(fillBrush, fill);
                    }
                }

                TextRenderer.DrawText(g, status, statusFont, new Point(Px(14), Px(91)), FaintColor,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
        }

        private static string FormatSize(long bytes)
        {
            double mb = bytes / 1048576.0;
            if (mb >= 1024) return (mb / 1024).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            return mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        private static string FormatSpeed(double kb)
        {
            if (kb >= 1024) return (kb / 1024).ToString("0.00", CultureInfo.InvariantCulture) + " MB/s";
            return kb.ToString("0.0", CultureInfo.InvariantCulture) + " KB/s";
        }

        private static string FormatEta(double seconds)
        {
            if (seconds < 0) return "";
            if (seconds < 60) return Math.Round(seconds).ToString("0", CultureInfo.InvariantCulture) + " 秒";
            if (seconds < 3600) return Math.Round(seconds / 60).ToString("0", CultureInfo.InvariantCulture) + " 分钟";
            return Math.Floor(seconds / 3600).ToString("0", CultureInfo.InvariantCulture) + " 小时 " +
                   Math.Round((seconds % 3600) / 60).ToString("0", CultureInfo.InvariantCulture) + " 分";
        }

        private static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 1f)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---------------------------------------------------------------- input
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (AutoMode && !LauncherRunning())
            {
                // 手动启动且启动器没开：先露个脸 8 秒，告诉用户“已经在后台盯着了”，然后隐身
                _hintTimer.Start();
                return;
            }
            _monitor.Update();
            _state = _monitor.GetState();
            _lastSignature = "\0";
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) return;
            if (_closeRect.Contains(e.Location)) { HideWidget(); return; }
            _dragging = true;
            _dragStart = new Point(e.X, e.Y);
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            Left += e.X - _dragStart.X;
            Top += e.Y - _dragStart.Y;
            _positioned = true;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Capture = false;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyRoundRegion();
        }
    }

    internal static class Installer
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool DeleteFileW(string name);

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "StellarisUpdateMonitor";

        public static void Run()
        {
            try
            {
                string src = Application.ExecutablePath;
                string destDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "StellarisUpdateMonitor");
                Directory.CreateDirectory(destDir);
                string dest = Path.Combine(destDir, "StellarisUpdateMonitor.exe");
                if (!string.Equals(src, dest, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(src, dest, true);
                    // 去掉“来自 Internet”标记，避免每次启动弹“未知发布者”
                    DeleteFileW(dest + ":Zone.Identifier");
                }

                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    key.SetValue(RunValue, "\"" + dest + "\"", Microsoft.Win32.RegistryValueKind.String);
                }

                MessageBox.Show("已安装到:\n" + destDir +
                                "\n\n开机自启: HKCU\\...\\Run\\" + RunValue +
                                "\n\n启动器打开时小窗会自动出现，关闭启动器后自动隐藏。\n" +
                                "想取消开机自启，运行: StellarisUpdateMonitor.exe --uninstall",
                    "Stellaris 更新监控", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ShowError("安装失败", ex);
            }
        }

        public static void Uninstall()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key != null) key.DeleteValue(RunValue, false);
                }
                MessageBox.Show("已取消开机自启（程序文件仍保留，可手动删除）。",
                    "Stellaris 更新监控", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ShowError("取消失败", ex);
            }
        }

        private static void ShowError(string title, Exception ex)
        {
            MessageBox.Show(title + ":\n" + ex.Message, "Stellaris 更新监控",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
