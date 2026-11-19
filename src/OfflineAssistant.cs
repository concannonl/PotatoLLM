// Offline Assistant - a minimal native chat window for a local llama.cpp server.
// Written for C# 5 so it compiles with the csc.exe built into every Windows (.NET Framework 4.x).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace OfflineAssistant
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "OfflineAssistant_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("Offline Assistant is already open.", "Offline Assistant");
                    return;
                }
                ServicePointManager.Expect100Continue = false;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new ChatForm());
            }
        }
    }

    // Settings chosen by the installer. Never shown to the user.
    class Config
    {
        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Config(string path)
        {
            if (!File.Exists(path)) return;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
        }

        public string Get(string key, string def)
        {
            string v;
            return values.TryGetValue(key, out v) && v.Length > 0 ? v : def;
        }

        public int GetInt(string key, int def)
        {
            int v;
            return int.TryParse(Get(key, ""), out v) ? v : def;
        }
    }

    // Puts the engine in a Windows "job" so it is closed automatically if this app closes or crashes.
    static class Job
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll")]
        static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll")]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimits
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters
        {
            public ulong A, B, C, D, E, F;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        static IntPtr handle = IntPtr.Zero;

        public static void Add(Process p)
        {
            try
            {
                if (handle == IntPtr.Zero)
                {
                    handle = CreateJobObject(IntPtr.Zero, null);
                    var info = new ExtendedLimits();
                    info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    int len = Marshal.SizeOf(typeof(ExtendedLimits));
                    IntPtr ptr = Marshal.AllocHGlobal(len);
                    Marshal.StructureToPtr(info, ptr, false);
                    SetInformationJobObject(handle, 9, ptr, (uint)len); // 9 = ExtendedLimitInformation
                    Marshal.FreeHGlobal(ptr);
                }
                AssignProcessToJobObject(handle, p.Handle);
            }
            catch { }
        }
    }

    // Runs llama-server.exe in the background with no window.
    class Engine
    {
        Process proc;
        StreamWriter log;
        readonly object logLock = new object();

        public bool HasExited
        {
            get
            {
                try { return proc == null || proc.HasExited; }
                catch { return true; }
            }
        }

        public void Start(string exe, string args, string logPath, bool appendLog)
        {
            Stop();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath));
                log = new StreamWriter(logPath, appendLog, Encoding.UTF8);
                log.AutoFlush = true;
                log.WriteLine("==== " + DateTime.Now + "  " + exe + " " + args);
            }
            catch { log = null; }

            var psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);

            proc = new Process();
            proc.StartInfo = psi;
            DataReceivedEventHandler onLine = (s, e) =>
            {
                if (e.Data == null || log == null) return;
                lock (logLock) { try { log.WriteLine(e.Data); } catch { } }
            };
            proc.OutputDataReceived += onLine;
            proc.ErrorDataReceived += onLine;
            proc.Start();
            Job.Add(proc);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }

        public void Stop()
        {
            try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
            proc = null;
        }
    }

    // A slim rounded progress bar showing how full the conversation memory is.
    class MemoryBar : Control
    {
        double fraction;

        public MemoryBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public double Fraction
        {
            get { return fraction; }
            set { fraction = Math.Max(0, Math.Min(1, value)); Invalidate(); }
        }

        static GraphicsPath Rounded(RectangleF r)
        {
            float d = r.Height;
            var path = new GraphicsPath();
            if (r.Width < d) { path.AddEllipse(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            path.CloseFigure();
            return path;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float h = 8f;
            var track = new RectangleF(1, (Height - h) / 2f, Width - 2, h);
            using (var path = Rounded(track))
            using (var b = new SolidBrush(Color.FromArgb(222, 225, 230)))
                e.Graphics.FillPath(b, path);

            if (fraction <= 0) return;
            Color fill = fraction < 0.7 ? Color.FromArgb(16, 150, 100)
                       : fraction < 0.9 ? Color.FromArgb(217, 140, 20)
                       : Color.FromArgb(205, 60, 50);
            var bar = new RectangleF(track.X, track.Y, Math.Max(h, (float)(track.Width * fraction)), h);
            using (var path = Rounded(bar))
            using (var b = new SolidBrush(fill))
                e.Graphics.FillPath(b, path);
        }
    }

    // Low-level helpers for smooth, flicker-free updates of the chat history box.
    static class Native
    {
        const int WM_SETREDRAW = 0x000B;
        const int WM_VSCROLL = 0x0115;
        const int SB_BOTTOM = 7;
        const int SB_VERT = 1;
        const int SIF_ALL = 0x17;
        const int EM_GETSCROLLPOS = 0x0400 + 221;
        const int EM_SETSCROLLPOS = 0x0400 + 222;

        [StructLayout(LayoutKind.Sequential)]
        struct ScrollInfo
        {
            public uint cbSize, fMask;
            public int nMin, nMax;
            public uint nPage;
            public int nPos, nTrackPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref POINT lParam);

        [DllImport("user32.dll")]
        static extern bool GetScrollInfo(IntPtr hWnd, int bar, ref ScrollInfo info);

        public static void SetRedraw(Control c, bool on)
        {
            SendMessage(c.Handle, WM_SETREDRAW, (IntPtr)(on ? 1 : 0), IntPtr.Zero);
            if (on) c.Invalidate();
        }

        // True when the view is at (or very near) the bottom, or there's nothing to scroll.
        public static bool IsAtBottom(Control c)
        {
            var si = new ScrollInfo();
            si.cbSize = (uint)Marshal.SizeOf(typeof(ScrollInfo));
            si.fMask = SIF_ALL;
            if (!GetScrollInfo(c.Handle, SB_VERT, ref si)) return true;
            if (si.nPage == 0 || si.nMax - si.nMin + 1 <= si.nPage) return true;
            return si.nPos + (int)si.nPage >= si.nMax - 40;
        }

        public static void ScrollToBottom(Control c)
        {
            SendMessage(c.Handle, WM_VSCROLL, (IntPtr)SB_BOTTOM, IntPtr.Zero);
        }

        public static POINT GetScrollPos(Control c)
        {
            var p = new POINT();
            SendMessage(c.Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref p);
            return p;
        }

        public static void SetScrollPos(Control c, POINT p)
        {
            SendMessage(c.Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref p);
        }
    }

    // One saved chat in the sidebar list.
    class ChatInfo
    {
        public string Id;
        public string Title;
        public DateTime Updated;
        public string FilePath;
        public override string ToString() { return Title; }
    }

    public class ChatForm : Form
    {
        const string AppName = "Offline Assistant";
        // Keep this identical to benchmark/quality-test.ps1 so test results match the app.
        // Today's date lets the model tell past from future (small models otherwise invent results).
        static string SystemPrompt
        {
            get
            {
                string today = DateTime.Now.ToString("dddd d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"));
                return "You are a helpful assistant running privately on the user's own computer, with no internet access. " +
                       "Today is " + today + ". " +
                       "If you are asked about recent or future events, or a fact you don't know, say you don't know instead of guessing. " +
                       "Answer clearly and concisely in plain text. Do not use markdown, tables or headings.";
            }
        }

        RichTextBox transcript;
        TextBox input;
        Button sendButton;
        Button newChatButton;
        Label status;
        MemoryBar memoryBar;
        Label memoryLabel;
        ToolTip tips;
        int usedTokens;            // how much of the context window the chat currently fills
        int liveBase;              // estimate at the start of the current reply
        ListBox chatList;
        ContextMenuStrip chatMenu;

        readonly Engine engine = new Engine();
        string appDir, enginePath, modelPath, logPath, chatsDir;

        // Saved chats
        readonly List<ChatInfo> chats = new List<ChatInfo>();
        string currentChatId;      // null until the first message of a new chat
        bool suppressSelect;
        int genId;                 // ignores replies that belong to a chat we've left
        string partialReply = "";
        string shownReply = "";        // the cleaned reply text currently on screen
        bool placeholderShown;         // the "..." shown before the first word arrives
        bool renderPending;            // new words waiting to be drawn
        bool unlockWrites;             // set if this Windows version won't write to a read-only box
        System.Windows.Forms.Timer renderTimer;
        bool dirty;                // current chat has changes not yet saved
        int ctx, threads, threadsBatch, maxReply;
        string kvCache;            // chat memory format: f16 (normal) or q8_0 (compact)
        volatile string baseUrl = "";

        bool ready, busy;
        volatile bool stopRequested;
        HttpWebRequest currentRequest;
        int assistantStart = -1;
        bool writing;

        readonly List<Dictionary<string, object>> history = new List<Dictionary<string, object>>();
        int historyStart = 0; // older messages before this index are no longer sent
        bool coldStart = true;     // engine has none of this chat cached (new engine or reopened chat)
        bool lastBuildTrimmed;     // the last BuildMessages dropped older messages

        // Reading text is slow on these laptops (~20 tokens/s), so cap how much has to be
        // re-read at once: when a chat is reopened, or when old messages are dropped.
        const int MaxMessageChars = 8000;   // about 1,300 words in one message
        const int ReloadChars = 9000;       // about 3,000 tokens: one to two minutes to re-read

        readonly Font bodyFont = new Font("Segoe UI", 11f);
        readonly Font headFont = new Font("Segoe UI", 10f, FontStyle.Bold);
        readonly Font noteFont = new Font("Segoe UI", 9.5f, FontStyle.Italic);
        readonly Color userColor = Color.FromArgb(37, 99, 235);
        readonly Color botColor = Color.FromArgb(4, 120, 87);
        readonly Color noteColor = Color.FromArgb(115, 115, 115);
        readonly Color textColor = Color.FromArgb(25, 25, 25);
        readonly Color sidebarColor = Color.FromArgb(243, 244, 246);
        readonly Color selectedColor = Color.FromArgb(219, 234, 254);
        readonly Font itemFont = new Font("Segoe UI", 10f);
        readonly Font smallFont = new Font("Segoe UI", 8.5f);

        public ChatForm()
        {
            appDir = Path.GetDirectoryName(Application.ExecutablePath);
            var cfg = new Config(Path.Combine(appDir, "config.ini"));
            enginePath = cfg.Get("engine", Path.Combine(appDir, "engine\\llama-server.exe"));
            modelPath = cfg.Get("model", "");
            ctx = cfg.GetInt("context", 4096);
            threads = cfg.GetInt("threads", 2);
            threadsBatch = cfg.GetInt("threads_batch", Environment.ProcessorCount);
            maxReply = cfg.GetInt("max_reply", 768);
            kvCache = cfg.Get("kv_cache", "f16");
            logPath = Path.Combine(appDir, "logs\\engine.log");
            chatsDir = Path.Combine(appDir, "chats");

            BuildUi();
            LoadChatList();
            Shown += (s, e) => StartEngine();
            FormClosing += (s, e) => { AbandonReply(); SaveCurrentChat(); engine.Stop(); };
        }

        // ---------------- UI ----------------

        void BuildUi()
        {
            Text = AppName;
            Width = 980;
            Height = 680;
            MinimumSize = new Size(640, 420);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(243, 244, 246);
            Font = new Font("Segoe UI", 10f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // Chat history area
            var transcriptPanel = new Panel();
            transcriptPanel.Dock = DockStyle.Fill;
            transcriptPanel.BackColor = Color.White;
            transcriptPanel.Padding = new Padding(18, 14, 8, 8);

            transcript = new RichTextBox();
            transcript.Dock = DockStyle.Fill;
            transcript.BorderStyle = BorderStyle.None;
            transcript.ReadOnly = true;
            transcript.BackColor = Color.White;
            transcript.Font = bodyFont;
            transcript.DetectUrls = false;
            transcript.ScrollBars = RichTextBoxScrollBars.Vertical;

            renderTimer = new System.Windows.Forms.Timer();
            renderTimer.Interval = 60;
            renderTimer.Tick += RenderTick;
            renderTimer.Start();
            transcriptPanel.Controls.Add(transcript);

            // Top bar: status
            var top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 40;
            top.Padding = new Padding(14, 8, 10, 6);

            status = new Label();
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.ForeColor = noteColor;
            tips = new ToolTip();
            tips.AutoPopDelay = 30000;
            tips.InitialDelay = 300;
            tips.ReshowDelay = 100;

            var memoryPanel = new Panel();
            memoryPanel.Dock = DockStyle.Right;
            memoryPanel.Width = 290;

            memoryLabel = new Label();
            memoryLabel.Dock = DockStyle.Fill;
            memoryLabel.TextAlign = ContentAlignment.MiddleRight;
            memoryLabel.ForeColor = noteColor;
            memoryLabel.Padding = new Padding(0, 0, 8, 0);

            memoryBar = new MemoryBar();
            memoryBar.Dock = DockStyle.Right;
            memoryBar.Width = 130;

            memoryPanel.Controls.Add(memoryLabel);
            memoryPanel.Controls.Add(memoryBar);

            top.Controls.Add(status);
            top.Controls.Add(memoryPanel);

            // Sidebar: New chat + saved chats
            var sidebar = new Panel();
            sidebar.Dock = DockStyle.Left;
            sidebar.Width = 230;
            sidebar.BackColor = sidebarColor;
            sidebar.Padding = new Padding(10, 10, 6, 10);

            newChatButton = new Button();
            newChatButton.Text = "+  New chat";
            newChatButton.Dock = DockStyle.Top;
            newChatButton.Height = 36;
            newChatButton.FlatStyle = FlatStyle.System;
            newChatButton.Click += (s, e) => NewChat();

            var listGap = new Panel();
            listGap.Dock = DockStyle.Top;
            listGap.Height = 10;

            chatList = new ListBox();
            chatList.Dock = DockStyle.Fill;
            chatList.BorderStyle = BorderStyle.None;
            chatList.BackColor = sidebarColor;
            chatList.DrawMode = DrawMode.OwnerDrawFixed;
            chatList.ItemHeight = 46;
            chatList.IntegralHeight = false;
            chatList.DrawItem += DrawChatItem;
            chatList.SelectedIndexChanged += (s, e) => OnChatSelected();
            chatList.MouseUp += ChatListMouseUp;
            chatList.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) DeleteChat(chatList.SelectedIndex); };

            chatMenu = new ContextMenuStrip();
            var deleteItem = new ToolStripMenuItem("Delete chat");
            deleteItem.Click += (s, e) => { if (chatMenu.Tag is int) DeleteChat((int)chatMenu.Tag); };
            chatMenu.Items.Add(deleteItem);

            sidebar.Controls.Add(chatList);
            sidebar.Controls.Add(listGap);
            sidebar.Controls.Add(newChatButton);

            var divider = new Panel();
            divider.Dock = DockStyle.Left;
            divider.Width = 1;
            divider.BackColor = Color.FromArgb(220, 220, 224);

            // Bottom: message box + Send
            var bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 112;
            bottom.Padding = new Padding(14, 10, 14, 6);

            var hint = new Label();
            hint.Dock = DockStyle.Bottom;
            hint.Height = 20;
            hint.ForeColor = noteColor;
            hint.Font = new Font("Segoe UI", 8.5f);
            hint.Text = "Press Enter to send, Shift+Enter for a new line. Everything stays on this computer.";

            input = new TextBox();
            input.Multiline = true;
            input.AcceptsReturn = true;
            input.ScrollBars = ScrollBars.Vertical;
            input.Dock = DockStyle.Fill;
            input.Font = bodyFont;
            input.KeyDown += InputKeyDown;

            var gap = new Panel();
            gap.Dock = DockStyle.Right;
            gap.Width = 10;

            sendButton = new Button();
            sendButton.Text = "Send";
            sendButton.Dock = DockStyle.Right;
            sendButton.Width = 96;
            sendButton.FlatStyle = FlatStyle.System;
            sendButton.Click += (s, e) => SendOrStop();

            bottom.Controls.Add(input);
            bottom.Controls.Add(gap);
            bottom.Controls.Add(sendButton);
            bottom.Controls.Add(hint);

            // Fill must be added first so the docked bars take their space before it.
            var main = new Panel();
            main.Dock = DockStyle.Fill;
            main.Controls.Add(transcriptPanel);
            main.Controls.Add(top);
            main.Controls.Add(bottom);

            Controls.Add(main);
            Controls.Add(divider);
            Controls.Add(sidebar);

            SetStatus("Getting ready...");
            sendButton.Enabled = false;
            UpdateMemory(EstimateTokens(BuildMessages()));
        }

        void InputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                SendOrStop();
            }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                e.SuppressKeyPress = true;
                input.SelectAll();
            }
        }

        void SetStatus(string text) { status.Text = text; }

        void Ui(Action a)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
        }

        // Writes text over the current selection. Normally works on the read-only box directly
        // (switching read-only off and on makes Windows repaint everything, which flickers).
        void WriteSelected(string text)
        {
            if (!unlockWrites)
            {
                int before = transcript.TextLength;
                int selected = transcript.SelectionLength;
                transcript.SelectedText = text;
                bool blocked = text.Length > 0 && transcript.TextLength == before && text.Length != selected;
                if (!blocked) return;
                unlockWrites = true;
            }
            transcript.ReadOnly = false;
            transcript.SelectedText = text;
            transcript.ReadOnly = true;
        }

        void InsertAtEnd(string text, Font font, Color color)
        {
            transcript.Select(transcript.TextLength, 0);
            transcript.SelectionFont = font;
            transcript.SelectionColor = color;
            WriteSelected(text);
        }

        // Runs a change to the chat history while keeping the view steady: follows the bottom
        // if the user is there, otherwise leaves them where they are, and keeps their selection.
        // Repaints are paused only for big rewrites or when the user has scrolled up.
        void UpdateTranscript(Action change, bool bigRewrite)
        {
            bool follow = Native.IsAtBottom(transcript);
            int selStart = transcript.SelectionStart, selLength = transcript.SelectionLength;
            Native.POINT pos = Native.GetScrollPos(transcript);
            bool pause = bigRewrite || !follow;
            if (pause) Native.SetRedraw(transcript, false);
            try { change(); }
            finally
            {
                if (selLength > 0) transcript.Select(selStart, selLength);
                if (follow)
                {
                    if (selLength == 0) transcript.ScrollToCaret();
                    Native.ScrollToBottom(transcript);
                }
                else Native.SetScrollPos(transcript, pos);
                if (pause) Native.SetRedraw(transcript, true);
            }
        }

        void Append(string text, Font font, Color color)
        {
            UpdateTranscript(() => InsertAtEnd(text, font, color), false);
        }

        void AppendNote(string text)
        {
            Append(text + "\n\n", noteFont, noteColor);
        }

        void ShowAssistantPlaceholder()
        {
            assistantStart = transcript.TextLength;
            Append("...", bodyFont, noteColor);
            shownReply = "";
            placeholderShown = true;
        }

        // Shows the reply so far. Usually only the new words are added at the end;
        // the whole reply is rewritten only when earlier text changed (e.g. markdown cleaned up).
        void ReplaceAssistantText(string text)
        {
            if (assistantStart < 0) return;
            if (!placeholderShown && text.StartsWith(shownReply, StringComparison.Ordinal))
            {
                string added = text.Substring(shownReply.Length);
                if (added.Length > 0) Append(added, bodyFont, textColor);
            }
            else
            {
                UpdateTranscript(() =>
                {
                    transcript.Select(assistantStart, Math.Max(0, transcript.TextLength - assistantStart));
                    transcript.SelectionFont = bodyFont;
                    transcript.SelectionColor = textColor;
                    WriteSelected(text);
                }, true);
                placeholderShown = false;
            }
            shownReply = text;
        }

        // Draws waiting words at most ~16 times a second, so bursts of tokens don't cause jitter.
        void RenderTick(object sender, EventArgs e)
        {
            if (!renderPending || !busy) return;
            renderPending = false;
            ReplaceAssistantText(Clean(partialReply));
        }

        // Small models often write markdown anyway; show it as clean text.
        static string Clean(string s)
        {
            s = s.Replace("\r\n", "\n");
            s = Regex.Replace(s, "```[A-Za-z0-9_+-]*\\n?", "");
            s = s.Replace("**", "").Replace("__", "").Replace("`", "");
            s = Regex.Replace(s, @"(?m)^#{1,6}\s*", "");
            s = Regex.Replace(s, @"(?m)^(\s*)[\*\-]\s+", "$1• ");
            return s.TrimStart();
        }

        // ---------------- Engine ----------------

        void StartEngine()
        {
            coldStart = true;
            ready = false;
            sendButton.Enabled = false;
            if (!File.Exists(enginePath) || !File.Exists(modelPath))
            {
                SetStatus("Not installed properly");
                AppendNote("Some files are missing. Please run the installer again.");
                return;
            }
            SetStatus("Getting ready... (this takes up to a minute)");
            if (transcript.TextLength == 0) AppendNote("Getting ready. This can take up to a minute after the computer starts.");

            var t = new Thread(() =>
            {
                string common = "-m \"" + modelPath + "\" -c " + ctx + " -t " + threads;
                string tuned = common + " -tb " + threadsBatch + " -np 1 --no-webui";
                // Compact chat memory needs flash attention. Try the full tuned settings first;
                // if this engine version rejects any of them, step back to safer ones.
                bool ok = (kvCache != "f16" && TryStart(tuned + " -ctk " + kvCache + " -ctv " + kvCache + " -fa on", false))
                       || TryStart(tuned, kvCache != "f16")
                       || TryStart(common, true);
                Ui(() => { if (ok) OnReady(); else OnFailed(); });
            });
            t.IsBackground = true;
            t.Start();
        }

        bool TryStart(string args, bool appendLog)
        {
            int port = FreePort();
            baseUrl = "http://127.0.0.1:" + port;
            engine.Start(enginePath, args + " --host 127.0.0.1 --port " + port, logPath, appendLog);
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 300)
            {
                if (engine.HasExited) return false;
                if (Healthy()) return true;
                Thread.Sleep(400);
            }
            engine.Stop();
            return false;
        }

        static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        bool Healthy()
        {
            try
            {
                var r = (HttpWebRequest)WebRequest.Create(baseUrl + "/health");
                r.Proxy = null;
                r.Timeout = 2000;
                using (var resp = (HttpWebResponse)r.GetResponse())
                    return resp.StatusCode == HttpStatusCode.OK;
            }
            catch { return false; }
        }

        void OnReady()
        {
            ready = true;
            sendButton.Enabled = true;
            SetStatus("Ready");
            if (history.Count == 0)
            {
                transcript.Clear();
                AppendNote("Ready. Ask me anything. Everything stays on this computer and nothing is sent to the internet.\n" +
                           "I can make mistakes, especially with sums and facts, so please double-check anything important.");
            }
            input.Focus();
        }

        void OnFailed()
        {
            SetStatus("Couldn't start");
            AppendNote("Sorry, the assistant couldn't start on this computer. Try closing other programs and opening it again. " +
                       "(Technical details were saved in " + logPath + ")");
        }

        // ---------------- Chat ----------------

        static Dictionary<string, object> Msg(string role, string content)
        {
            var d = new Dictionary<string, object>();
            d["role"] = role;
            d["content"] = content;
            return d;
        }

        int PromptBudgetChars()
        {
            // Rough rule: about 3 characters per token for English.
            return Math.Max(2000, (ctx - maxReply - 200) * 3 - SystemPrompt.Length);
        }

        void NewChat()
        {
            AbandonReply();
            SaveCurrentChat();
            currentChatId = null;
            history.Clear();
            historyStart = 0;
            transcript.Clear();
            SelectInList(null);
            UpdateMemory(EstimateTokens(BuildMessages()));
            if (ready)
            {
                AppendNote("New chat started.");
                SetStatus("Ready");
            }
            input.Focus();
        }

        // If a reply is still being written when the user switches chats, stop it and
        // keep whatever was written so far.
        void AbandonReply()
        {
            if (!busy) return;
            StopGenerating();
            genId++;
            busy = false;
            dirty = true;
            renderPending = false;
            sendButton.Text = "Send";
            assistantStart = -1;
            if (partialReply.Trim().Length > 0)
                history.Add(Msg("assistant", partialReply));
            else if (history.Count > 0 && (string)history[history.Count - 1]["role"] == "user")
                history.RemoveAt(history.Count - 1);
            SetStatus(ready ? "Ready" : status.Text);
        }

        // ---------------- Saved chats ----------------

        static string MakeTitle(string firstMessage)
        {
            string t = firstMessage.Replace("\r", " ").Replace("\n", " ").Trim();
            while (t.Contains("  ")) t = t.Replace("  ", " ");
            return t.Length > 60 ? t.Substring(0, 60).TrimEnd() + "..." : t;
        }

        void SaveCurrentChat()
        {
            if (!dirty) return;
            dirty = false;
            if (history.Count == 0)
            {
                // A chat whose only message was withdrawn: remove it from the list.
                ChatInfo empty = FindChat(currentChatId);
                if (empty != null)
                {
                    try { File.Delete(empty.FilePath); } catch { }
                    chats.Remove(empty);
                    currentChatId = null;
                    RefreshList();
                }
                return;
            }
            try
            {
                Directory.CreateDirectory(chatsDir);
                if (currentChatId == null)
                    currentChatId = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

                ChatInfo info = FindChat(currentChatId);
                if (info == null)
                {
                    info = new ChatInfo();
                    info.Id = currentChatId;
                    info.FilePath = Path.Combine(chatsDir, currentChatId + ".json");
                    info.Title = MakeTitle((string)history[0]["content"]);
                    chats.Add(info);
                }
                info.Updated = DateTime.Now;

                var doc = new Dictionary<string, object>();
                doc["id"] = info.Id;
                doc["title"] = info.Title;
                doc["updated"] = info.Updated.ToString("o", CultureInfo.InvariantCulture);
                doc["messages"] = history;
                var json = new JavaScriptSerializer();
                json.MaxJsonLength = int.MaxValue;

                // Write to a temporary file first so a crash can't leave a half-written chat.
                string tmp = info.FilePath + ".tmp";
                File.WriteAllText(tmp, json.Serialize(doc), Encoding.UTF8);
                if (File.Exists(info.FilePath)) File.Delete(info.FilePath);
                File.Move(tmp, info.FilePath);

                RefreshList();
            }
            catch { }
        }

        void LoadChatList()
        {
            chats.Clear();
            try
            {
                if (Directory.Exists(chatsDir))
                {
                    var json = new JavaScriptSerializer();
                    json.MaxJsonLength = int.MaxValue;
                    foreach (var file in Directory.GetFiles(chatsDir, "*.json"))
                    {
                        try
                        {
                            var doc = json.DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
                            if (doc == null) continue;
                            var info = new ChatInfo();
                            info.Id = Path.GetFileNameWithoutExtension(file);
                            info.FilePath = file;
                            info.Title = doc.ContainsKey("title") ? (doc["title"] as string) : null;
                            if (string.IsNullOrEmpty(info.Title)) info.Title = "Chat";
                            DateTime when;
                            info.Updated = doc.ContainsKey("updated") &&
                                DateTime.TryParse(doc["updated"] as string, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out when)
                                ? when : File.GetLastWriteTime(file);
                            chats.Add(info);
                        }
                        catch { } // skip a damaged file rather than failing
                    }
                }
            }
            catch { }
            RefreshList();
        }

        ChatInfo FindChat(string id)
        {
            if (id == null) return null;
            foreach (var c in chats) if (c.Id == id) return c;
            return null;
        }

        void RefreshList()
        {
            chats.Sort((a, b) => b.Updated.CompareTo(a.Updated));
            suppressSelect = true;
            chatList.BeginUpdate();
            chatList.Items.Clear();
            foreach (var c in chats) chatList.Items.Add(c);
            chatList.EndUpdate();
            suppressSelect = false;
            SelectInList(currentChatId);
        }

        void SelectInList(string id)
        {
            suppressSelect = true;
            chatList.SelectedIndex = -1;
            for (int i = 0; i < chatList.Items.Count; i++)
                if (((ChatInfo)chatList.Items[i]).Id == id) { chatList.SelectedIndex = i; break; }
            suppressSelect = false;
            chatList.Invalidate();
        }

        void OnChatSelected()
        {
            if (suppressSelect || chatList.SelectedIndex < 0) return;
            var info = (ChatInfo)chatList.Items[chatList.SelectedIndex];
            if (info.Id == currentChatId) return;
            OpenChat(info);
        }

        void OpenChat(ChatInfo info)
        {
            AbandonReply();
            SaveCurrentChat();

            var loaded = new List<Dictionary<string, object>>();
            try
            {
                var json = new JavaScriptSerializer();
                json.MaxJsonLength = int.MaxValue;
                var doc = json.DeserializeObject(File.ReadAllText(info.FilePath, Encoding.UTF8)) as Dictionary<string, object>;
                var msgs = doc != null && doc.ContainsKey("messages") ? doc["messages"] as object[] : null;
                if (msgs != null)
                    foreach (var m in msgs)
                    {
                        var d = m as Dictionary<string, object>;
                        if (d == null || !d.ContainsKey("role") || !d.ContainsKey("content")) continue;
                        loaded.Add(Msg(d["role"] as string, d["content"] as string ?? ""));
                    }
            }
            catch
            {
                MessageBox.Show("Sorry, that chat couldn't be opened.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                SelectInList(currentChatId);
                return;
            }

            currentChatId = info.Id;
            history.Clear();
            history.AddRange(loaded);
            historyStart = 0;
            coldStart = true;
            dirty = false;

            transcript.Clear();
            Native.SetRedraw(transcript, false); // draw the whole chat in one go
            foreach (var m in history)
            {
                bool isUser = (string)m["role"] == "user";
                Append(isUser ? "You\n" : "Assistant\n", headFont, isUser ? userColor : botColor);
                string text = (string)m["content"];
                Append((isUser ? text.Replace("\r\n", "\n") : Clean(text)) + "\n\n", bodyFont, textColor);
            }
            transcript.Select(transcript.TextLength, 0);
            transcript.ScrollToCaret();
            Native.SetRedraw(transcript, true);
            // A question left unanswered (e.g. the app was closed mid-reply) stays visible,
            // but isn't sent again, so the next message starts cleanly.
            if (history.Count > 0 && (string)history[history.Count - 1]["role"] == "user")
                history.RemoveAt(history.Count - 1);

            UpdateMemory(EstimateTokens(BuildMessages()));
            SelectInList(currentChatId);
            if (ready) SetStatus("Ready");
            input.Focus();
        }

        void DeleteChat(int index)
        {
            if (index < 0 || index >= chatList.Items.Count) return;
            var info = (ChatInfo)chatList.Items[index];
            var answer = MessageBox.Show("Delete \"" + info.Title + "\"?\n\nThis can't be undone.", AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            if (info.Id == currentChatId)
            {
                AbandonReply();
                history.Clear();          // so NewChat doesn't save it again
                currentChatId = null;
            }
            try { File.Delete(info.FilePath); } catch { }
            chats.Remove(info);
            RefreshList();
            if (currentChatId == null) NewChat();
        }

        void ChatListMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            int index = chatList.IndexFromPoint(e.Location);
            if (index < 0) return;
            chatMenu.Tag = index;
            chatMenu.Show(chatList, e.Location);
        }

        static string FriendlyDate(DateTime when)
        {
            DateTime today = DateTime.Today;
            if (when.Date == today) return "Today, " + when.ToString("HH:mm");
            if (when.Date == today.AddDays(-1)) return "Yesterday, " + when.ToString("HH:mm");
            if (when.Year == today.Year) return when.ToString("d MMMM");
            return when.ToString("d MMMM yyyy");
        }

        void DrawChatItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= chatList.Items.Count) return;
            var info = (ChatInfo)chatList.Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(selected ? selectedColor : sidebarColor))
                e.Graphics.FillRectangle(bg, e.Bounds);
            var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            var titleRect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 5, e.Bounds.Width - 12, 20);
            var dateRect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 25, e.Bounds.Width - 12, 17);
            TextRenderer.DrawText(e.Graphics, info.Title, itemFont, titleRect, textColor, flags);
            TextRenderer.DrawText(e.Graphics, FriendlyDate(info.Updated), smallFont, dateRect, noteColor, flags);
        }

        void SendOrStop()
        {
            if (busy) { StopGenerating(); return; }
            if (!ready) return;

            string text = input.Text.Trim();
            if (text.Length == 0) return;
            if (text.Length > Math.Min(PromptBudgetChars() * 8 / 10, MaxMessageChars))
            {
                MessageBox.Show(
                    "That's too long for this computer to read in one go.\n\nPlease paste a shorter piece (about 1,000 words or less).",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            input.Clear();
            history.Add(Msg("user", text));
            dirty = true;

            Append("You\n", headFont, userColor);
            Append(text.Replace("\r\n", "\n") + "\n\n", bodyFont, textColor);
            Append("Assistant\n", headFont, botColor);
            ShowAssistantPlaceholder();
            renderPending = false;

            busy = true;
            writing = false;
            stopRequested = false;
            partialReply = "";
            sendButton.Text = "Stop";
            SaveCurrentChat(); // the chat appears in the list as soon as it starts

            int myId = ++genId;
            bool wasCold = coldStart;
            var messages = BuildMessages();
            coldStart = false;
            bool catchingUp = (wasCold && messages.Count > 2) || lastBuildTrimmed;
            SetStatus(catchingUp ? "Catching up on the conversation... (this can take a minute or two)" : "Reading your message...");
            liveBase = EstimateTokens(messages);
            UpdateMemory(liveBase);
            var t = new Thread(() => Generate(messages, myId));
            t.IsBackground = true;
            t.Start();
        }

        // Keeps the conversation within the model's memory. Old messages are dropped in
        // big chunks so the engine can keep reusing its cached work between replies.
        List<object> BuildMessages()
        {
            int budget = PromptBudgetChars();
            int total = 0;
            for (int i = historyStart; i < history.Count; i++) total += ((string)history[i]["content"]).Length + 12;

            lastBuildTrimmed = false;
            if (total > budget || (coldStart && total > ReloadChars))
            {
                lastBuildTrimmed = total > budget;
                int target = Math.Min(budget * 6 / 10, ReloadChars);
                while (historyStart < history.Count - 1 && total > target)
                {
                    total -= ((string)history[historyStart]["content"]).Length + 12;
                    historyStart++;
                }
                while (historyStart < history.Count - 1 && (string)history[historyStart]["role"] != "user") historyStart++;
            }

            var list = new List<object>();
            list.Add(Msg("system", SystemPrompt));
            for (int i = historyStart; i < history.Count; i++) list.Add(history[i]);
            return list;
        }

        void Generate(List<object> messages, int myId)
        {
            var reply = new StringBuilder();
            int pieces = 0;          // the engine streams about one token per chunk
            int realTotal = -1;      // exact count reported by the engine, if any
            string error = null;
            var json = new JavaScriptSerializer();
            json.MaxJsonLength = int.MaxValue;
            try
            {
                var body = new Dictionary<string, object>();
                body["messages"] = messages;
                body["stream"] = true;
                body["temperature"] = 0.7;
                body["max_tokens"] = maxReply;
                body["cache_prompt"] = true;
                var streamOptions = new Dictionary<string, object>();
                streamOptions["include_usage"] = true;
                body["stream_options"] = streamOptions;
                byte[] data = Encoding.UTF8.GetBytes(json.Serialize(body));

                var req = (HttpWebRequest)WebRequest.Create(baseUrl + "/v1/chat/completions");
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Proxy = null;
                req.Timeout = Timeout.Infinite;
                req.ReadWriteTimeout = 10 * 60 * 1000;
                req.ContentLength = data.Length;
                currentRequest = req;
                using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);

                using (var resp = req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string line;
                    while (!stopRequested && (line = reader.ReadLine()) != null)
                    {
                        if (!line.StartsWith("data:")) continue;
                        string payload = line.Substring(5).Trim();
                        if (payload == "[DONE]") break;
                        object parsed = json.DeserializeObject(payload);
                        int reported = ReadTokenTotal(parsed);
                        if (reported > 0) realTotal = reported;
                        string piece = ExtractDelta(parsed);
                        if (string.IsNullOrEmpty(piece)) continue;
                        reply.Append(piece);
                        int count = ++pieces;
                        string snapshot = reply.ToString();
                        Ui(() => ShowPartial(myId, snapshot, count));
                    }
                }
            }
            catch (Exception ex)
            {
                if (!stopRequested) error = ex.Message;
            }
            string final = reply.ToString();
            int total = realTotal > 0 ? realTotal : -1;
            int counted = pieces;
            Ui(() => FinishReply(myId, final, error, total, counted));
        }

        // Rough estimate used until the engine reports exact numbers: about 3 characters per token.
        static int EstimateTokens(List<object> messages)
        {
            int chars = 0;
            foreach (var m in messages)
            {
                var d = m as Dictionary<string, object>;
                if (d != null && d.ContainsKey("content")) chars += ((d["content"] as string) ?? "").Length;
            }
            return chars / 3 + messages.Count * 5;
        }

        static int ToInt(object o)
        {
            try { return o == null ? 0 : Convert.ToInt32(o, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        // The engine's last message says exactly how many tokens the chat used.
        static int ReadTokenTotal(object parsed)
        {
            var obj = parsed as Dictionary<string, object>;
            if (obj == null) return -1;
            var timings = obj.ContainsKey("timings") ? obj["timings"] as Dictionary<string, object> : null;
            if (timings != null && timings.ContainsKey("predicted_n"))
            {
                int t = (timings.ContainsKey("cache_n") ? ToInt(timings["cache_n"]) : 0)
                      + (timings.ContainsKey("prompt_n") ? ToInt(timings["prompt_n"]) : 0)
                      + ToInt(timings["predicted_n"]);
                if (t > 0) return t;
            }
            var usage = obj.ContainsKey("usage") ? obj["usage"] as Dictionary<string, object> : null;
            if (usage != null)
            {
                int t = usage.ContainsKey("total_tokens") ? ToInt(usage["total_tokens"])
                      : ToInt(usage.ContainsKey("prompt_tokens") ? usage["prompt_tokens"] : null)
                        + ToInt(usage.ContainsKey("completion_tokens") ? usage["completion_tokens"] : null);
                if (t > 0) return t;
            }
            return -1;
        }

        void UpdateMemory(int tokens)
        {
            usedTokens = Math.Max(0, Math.Min(tokens, ctx));
            double fraction = ctx > 0 ? (double)usedTokens / ctx : 0;
            int percent = (int)Math.Round(fraction * 100);
            memoryBar.Fraction = fraction;
            memoryLabel.Text = "Chat memory: " + percent + "% used";

            int wordsTotal = ctx * 3 / 4;
            string tip =
                "Chat memory\n\n" +
                "The assistant can only keep about " + wordsTotal.ToString("N0") + " words of this chat in mind at once.\n" +
                "This bar shows how much of that space the chat is using.\n\n" +
                "When it fills up, the assistant starts forgetting the earliest messages.\n" +
                "For a completely fresh start, click New chat.\n\n" +
                "Using " + usedTokens.ToString("N0") + " of " + ctx.ToString("N0") + " tokens (" +
                (ctx - usedTokens).ToString("N0") + " left). A token is a word or part of a word.";
            tips.SetToolTip(memoryBar, tip);
            tips.SetToolTip(memoryLabel, tip);
        }

        static string ExtractDelta(object parsed)
        {
            var obj = parsed as Dictionary<string, object>;
            if (obj == null || !obj.ContainsKey("choices")) return null;
            var choices = obj["choices"] as object[];
            if (choices == null || choices.Length == 0) return null;
            var choice = choices[0] as Dictionary<string, object>;
            if (choice == null || !choice.ContainsKey("delta")) return null;
            var delta = choice["delta"] as Dictionary<string, object>;
            if (delta == null || !delta.ContainsKey("content")) return null;
            return delta["content"] as string;
        }

        void ShowPartial(int myId, string text, int tokensSoFar)
        {
            if (!busy || myId != genId) return;
            UpdateMemory(liveBase + tokensSoFar);
            partialReply = text;
            renderPending = true;
            if (!writing) { writing = true; SetStatus("Writing..."); }
        }

        void FinishReply(int myId, string final, string error, int realTotal, int tokensCounted)
        {
            if (myId != genId) return; // belongs to a chat the user has left
            UpdateMemory(realTotal > 0 ? realTotal : liveBase + tokensCounted);
            bool wasStopped = stopRequested;
            dirty = true;
            busy = false;
            renderPending = false;
            currentRequest = null;
            sendButton.Text = "Send";

            if (final.Trim().Length > 0)
            {
                ReplaceAssistantText(Clean(final));
                history.Add(Msg("assistant", final));
            }
            else
            {
                ReplaceAssistantText(wasStopped ? "(stopped)" : "(no reply)");
                if (history.Count > 0 && (string)history[history.Count - 1]["role"] == "user")
                    history.RemoveAt(history.Count - 1);
            }
            assistantStart = -1;
            Append("\n\n", bodyFont, textColor);
            SaveCurrentChat();

            if (error != null)
            {
                if (engine.HasExited)
                {
                    AppendNote("The assistant stopped unexpectedly. Restarting it...");
                    StartEngine();
                    return;
                }
                AppendNote("Sorry, something went wrong. Please try again.");
            }
            SetStatus(wasStopped ? "Stopped" : "Ready");
            input.Focus();
        }

        void StopGenerating()
        {
            if (!busy) return;
            stopRequested = true;
            try { if (currentRequest != null) currentRequest.Abort(); } catch { }
        }
    }
}
