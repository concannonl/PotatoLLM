// Offline Assistant - a minimal native chat window for a local llama.cpp server.
// Written for C# 5 so it compiles with the csc.exe built into every Windows (.NET Framework 4.x).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml;

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

    // Works out what this computer can run, so the app needs no settings.
    static class Machine
    {
        [StructLayout(LayoutKind.Sequential)]
        class MemoryStatus
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus status);

        public static double TotalMemoryGB()
        {
            var m = new MemoryStatus();
            return GlobalMemoryStatusEx(m) ? m.ullTotalPhys / (1024.0 * 1024 * 1024) : 4;
        }

        public static int PhysicalCores()
        {
            int cores = 0;
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher("select NumberOfCores from Win32_Processor"))
                    foreach (System.Management.ManagementObject o in searcher.Get())
                        cores += Convert.ToInt32(o["NumberOfCores"]);
            }
            catch { }
            return cores > 0 ? cores : Math.Max(1, Environment.ProcessorCount / 2);
        }
    }

    // The two models the app can use; chosen by how much memory the computer has.
    // See docs/benchmarks.md for how these were picked.
    class ModelChoice
    {
        public string Label, Repo, File, Pattern;
        public double SizeGB;
        public int Context, ReadTps;

        public static ModelChoice ForThisComputer()
        {
            var m = new ModelChoice();
            if (Machine.TotalMemoryGB() >= 7)
            {
                m.Label = "Gemma 3 4B"; m.Repo = "bartowski/google_gemma-3-4b-it-GGUF";
                m.File = "google_gemma-3-4b-it-Q4_K_M.gguf"; m.Pattern = "gemma-3-4b.*Q4_K_M";
                m.SizeGB = 2.5; m.Context = 32768; m.ReadTps = 18;
            }
            else
            {
                m.Label = "Gemma 3 1B"; m.Repo = "bartowski/google_gemma-3-1b-it-GGUF";
                m.File = "google_gemma-3-1b-it-Q4_K_M.gguf"; m.Pattern = "gemma-3-1b.*Q4_K_M";
                m.SizeGB = 0.8; m.Context = 16384; m.ReadTps = 55;
            }
            return m;
        }

        // An already-present copy (from an earlier install, or an offline bundle), if any.
        public string FindLocal(string modelsDir)
        {
            try
            {
                foreach (var f in Directory.GetFiles(modelsDir, "*.gguf"))
                {
                    string name = Path.GetFileName(f);
                    if (Regex.IsMatch(name, Pattern, RegexOptions.IgnoreCase) && !name.Contains("mmproj") &&
                        new FileInfo(f).Length > 100L * 1024 * 1024)
                        return f;
                }
            }
            catch { }
            return null;
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

    // The chat history: a lightweight view the app draws itself. Each frame is drawn off-screen
    // and shown whole (no flicker), and the view glides smoothly to new text instead of jumping.
    class ChatView : Control
    {
        class Block
        {
            public string Text = "";
            public Font Font;
            public Color Color;
            public int SpaceAfter;
            public bool IsHeader;
            public List<string> Lines = new List<string>();
            public List<int> Starts = new List<int>();   // where each line begins in Text
            public int Top;
            public int Height;
        }

        readonly List<Block> blocks = new List<Block>();
        readonly VScrollBar bar = new VScrollBar();
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly Padding pad = new Padding(18, 14, 14, 18);
        double scrollY, targetY;
        bool follow = true;          // keep the newest text in view
        int contentHeight;
        int layoutWidth = -1;
        int updateDepth;
        bool pending;
        int menuBlock = -1;
        const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        public ChatView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;

            bar.Dock = DockStyle.Right;
            bar.Enabled = false;
            bar.Scroll += (s, e) =>
            {
                anim.Stop();
                scrollY = targetY = bar.Value;
                follow = targetY >= MaxScroll - 2;
                Invalidate();
            };
            Controls.Add(bar);

            anim.Interval = 15;
            anim.Tick += AnimTick;

            var copyOne = new ToolStripMenuItem("Copy this message");
            copyOne.Click += (s, e) => CopyMessage();
            var copyAll = new ToolStripMenuItem("Copy the whole chat");
            copyAll.Click += (s, e) => CopyAll();
            menu.Items.Add(copyOne);
            menu.Items.Add(copyAll);
        }

        public bool IsEmpty { get { return blocks.Count == 0; } }

        int ViewHeight { get { return Math.Max(1, ClientSize.Height); } }
        int MaxScroll { get { return Math.Max(0, contentHeight - ViewHeight); } }
        int TextWidth { get { return Math.Max(40, ClientSize.Width - bar.Width - pad.Left - pad.Right); } }

        // ---- Public API ----

        public void Clear()
        {
            blocks.Clear();
            scrollY = targetY = 0;
            follow = true;
            anim.Stop();
            Changed();
        }

        // Adds text as a new block. Trailing newlines become spacing: one = small gap (used after
        // the "You" / "Assistant" labels), two or more = paragraph gap. Returns the block index.
        public int AddBlock(string text, Font font, Color color)
        {
            return InsertBlock(blocks.Count, text, font, color);
        }

        // Inserts a block before position 'at' (blocks after it move down by one).
        public int InsertBlock(int at, string text, Font font, Color color)
        {
            at = Math.Max(0, Math.Min(at, blocks.Count));
            string body = (text ?? "").Replace("\r\n", "\n");
            int trailing = 0;
            while (body.EndsWith("\n")) { body = body.Substring(0, body.Length - 1); trailing++; }
            int space = trailing == 0 ? 0 : trailing == 1 ? 2 : font.Height * 3 / 4;
            if (body.Length == 0)
            {
                if (blocks.Count > 0)
                {
                    var last = blocks[blocks.Count - 1];
                    last.SpaceAfter = Math.Max(last.SpaceAfter, space);
                    Changed();
                }
                return blocks.Count - 1;
            }
            var b = new Block();
            b.Text = body;
            b.Font = font;
            b.Color = color;
            b.SpaceAfter = space;
            b.IsHeader = trailing == 1;
            Wrap(b, 0);
            blocks.Insert(at, b);
            Changed();
            return at;
        }

        // Replaces a block's text. When text was only added at the end (the usual case while a
        // reply streams in), only the last line onwards is re-wrapped.
        public void SetBlock(int index, string text, Font font, Color color)
        {
            if (index < 0 || index >= blocks.Count) return;
            var b = blocks[index];
            string body = (text ?? "").Replace("\r\n", "\n");
            bool extends = b.Font == font && b.Lines.Count > 0 && body.StartsWith(b.Text, StringComparison.Ordinal);
            int from = extends ? b.Starts[b.Starts.Count - 1] : 0;
            if (extends)
            {
                b.Lines.RemoveAt(b.Lines.Count - 1);
                b.Starts.RemoveAt(b.Starts.Count - 1);
            }
            else
            {
                b.Lines.Clear();
                b.Starts.Clear();
            }
            b.Text = body;
            b.Font = font;
            b.Color = color;
            Wrap(b, from);
            Changed();
        }

        public void BeginUpdate() { updateDepth++; }

        public void EndUpdate()
        {
            if (updateDepth > 0) updateDepth--;
            if (updateDepth == 0 && pending) Changed();
        }

        public void ScrollToEndNow()
        {
            anim.Stop();
            follow = true;
            scrollY = targetY = MaxScroll;
            SyncBar();
            Invalidate();
        }

        // ---- Layout ----

        static int Measure(string s, Font f)
        {
            if (s.Length == 0) return 0;
            return TextRenderer.MeasureText(s, f, new Size(int.MaxValue, int.MaxValue), Flags).Width;
        }

        // Word-wraps b.Text from position 'from' (which must be the start of a line) to the end.
        void Wrap(Block b, int from)
        {
            int width = TextWidth;
            string t = b.Text;
            int pos = from;
            while (true)
            {
                int nl = t.IndexOf('\n', pos);
                int end = nl < 0 ? t.Length : nl;
                WrapParagraph(b, t, pos, end, width);
                if (nl < 0) break;
                pos = nl + 1;
            }
            if (b.Lines.Count == 0) { b.Lines.Add(""); b.Starts.Add(0); }
            b.Height = b.Lines.Count * b.Font.Height + b.SpaceAfter;
        }

        static void WrapParagraph(Block b, string t, int start, int end, int width)
        {
            if (start >= end) { b.Lines.Add(""); b.Starts.Add(start); return; }
            int lineStart = start;
            while (lineStart < end)
            {
                int fit = -1;
                int j = lineStart;
                while (j <= end)
                {
                    int space = j < end ? t.IndexOf(' ', j, end - j) : -1;
                    int wordEnd = space < 0 ? end : space;
                    if (Measure(t.Substring(lineStart, wordEnd - lineStart), b.Font) > width) break;
                    fit = wordEnd;
                    if (space < 0) break;
                    j = wordEnd + 1;
                }
                if (fit < 0)
                {
                    // A single word wider than the view: break it by characters.
                    int k = lineStart + 1;
                    while (k < end && Measure(t.Substring(lineStart, k - lineStart + 1), b.Font) <= width) k++;
                    fit = k;
                }
                b.Lines.Add(t.Substring(lineStart, fit - lineStart));
                b.Starts.Add(lineStart);
                lineStart = fit;
                if (lineStart < end && t[lineStart] == ' ') lineStart++;
            }
        }

        void RelayoutAll()
        {
            layoutWidth = TextWidth;
            foreach (var b in blocks)
            {
                b.Lines.Clear();
                b.Starts.Clear();
                Wrap(b, 0);
            }
        }

        void Changed()
        {
            if (updateDepth > 0) { pending = true; return; }
            pending = false;
            if (layoutWidth != TextWidth) RelayoutAll();
            int y = pad.Top;
            foreach (var b in blocks) { b.Height = b.Lines.Count * b.Font.Height + b.SpaceAfter; b.Top = y; y += b.Height; }
            contentHeight = y + pad.Bottom;
            if (follow) targetY = MaxScroll;
            targetY = Math.Min(targetY, MaxScroll);
            if (scrollY > MaxScroll) scrollY = MaxScroll;
            SyncBar();
            if (Math.Abs(targetY - scrollY) > 0.5) anim.Start();
            Invalidate();
        }

        void SyncBar()
        {
            int max = MaxScroll;
            if (max <= 0)
            {
                bar.Enabled = false;
                return;
            }
            bar.Enabled = true;
            bar.Minimum = 0;
            bar.Maximum = max + ViewHeight - 1;
            bar.LargeChange = ViewHeight;
            bar.SmallChange = Math.Max(1, Font.Height);
            bar.Value = Math.Max(0, Math.Min(max, (int)Math.Round(scrollY)));
        }

        // Eases towards the target position, so new lines slide into view.
        void AnimTick(object sender, EventArgs e)
        {
            double diff = targetY - scrollY;
            if (Math.Abs(diff) <= 0.5) { scrollY = targetY; anim.Stop(); }
            else
            {
                double step = diff * 0.25;
                if (Math.Abs(step) < 1) step = Math.Sign(diff);
                scrollY += step;
            }
            SyncBar();
            Invalidate();
        }

        // ---- Input ----

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int lineH = blocks.Count > 0 ? blocks[0].Font.Height : 20;
            targetY = Math.Max(0, Math.Min(MaxScroll, targetY - e.Delta / 120.0 * 3 * lineH));
            follow = targetY >= MaxScroll - 2;
            anim.Start();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Right || blocks.Count == 0) return;
            double docY = e.Y + scrollY;
            menuBlock = -1;
            for (int i = 0; i < blocks.Count; i++)
                if (docY >= blocks[i].Top && docY < blocks[i].Top + blocks[i].Height) { menuBlock = i; break; }
            menu.Items[0].Enabled = menuBlock >= 0;
            menu.Show(this, e.Location);
        }

        void CopyMessage()
        {
            if (menuBlock < 0 || menuBlock >= blocks.Count) return;
            int i = menuBlock;
            if (blocks[i].IsHeader && i + 1 < blocks.Count) i++;
            SetClipboard(blocks[i].Text);
        }

        void CopyAll()
        {
            var sb = new StringBuilder();
            foreach (var b in blocks)
            {
                sb.Append(b.Text);
                sb.Append(b.IsHeader ? "\r\n" : "\r\n\r\n");
            }
            SetClipboard(sb.ToString().TrimEnd());
        }

        static void SetClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text.Replace("\r\n", "\n").Replace("\n", "\r\n")); } catch { }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (IsHandleCreated) Changed();
        }

        // ---- Drawing ----

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int offset = (int)Math.Round(scrollY);
            int bottom = ClientSize.Height;
            foreach (var b in blocks)
            {
                int top = b.Top - offset;
                if (top > bottom) break;
                if (top + b.Height < 0) continue;
                int lh = b.Font.Height;
                for (int i = 0; i < b.Lines.Count; i++)
                {
                    int y = top + i * lh;
                    if (y + lh < 0) continue;
                    if (y > bottom) break;
                    TextRenderer.DrawText(g, b.Lines[i], b.Font, new Point(pad.Left, y), b.Color, BackColor, Flags);
                }
            }
        }
    }

    // Pulls the plain text out of documents the user adds to the chat.
    static class DocumentReader
    {
        public static readonly string[] Supported = { ".txt", ".md", ".csv", ".log", ".rtf", ".docx", ".pdf" };
        public const string FileFilter = "Documents (*.txt; *.docx; *.pdf; *.rtf; *.md; *.csv)|*.txt;*.docx;*.pdf;*.rtf;*.md;*.csv;*.log|All files (*.*)|*.*";

        public static bool IsSupported(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return Array.IndexOf(Supported, ext) >= 0;
        }

        // Returns the text, or throws an exception whose message can be shown to the user.
        // RTF needs a RichTextBox, so call this on the UI thread for .rtf files.
        public static string Read(string path, string pdftotext)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string text;
            if (ext == ".doc")
                throw new Exception("This is an old-style Word document. Open it in Word and use \"Save As\" to save it as a .docx file, then add that instead.");
            else if (ext == ".docx") text = ReadDocx(path);
            else if (ext == ".pdf") text = ReadPdf(path, pdftotext);
            else if (ext == ".rtf")
            {
                using (var box = new RichTextBox())
                {
                    box.Rtf = File.ReadAllText(path, Encoding.Default);
                    text = box.Text;
                }
            }
            else if (IsSupported(path)) text = ReadPlainText(path);
            else throw new Exception("This kind of file can't be read. Documents ending in .txt, .docx, .pdf or .rtf work.");
            return Tidy(text);
        }

        static string ReadPlainText(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
            catch (DecoderFallbackException) { return Encoding.Default.GetString(bytes); } // older Windows text files
        }

        // A .docx is a zip file; the text is in word/document.xml.
        static string ReadDocx(string path)
        {
            const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            using (var file = File.OpenRead(path))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Read))
            {
                var entry = zip.GetEntry("word/document.xml");
                if (entry == null) throw new Exception("This Word document seems to be damaged.");
                var xml = new XmlDocument();
                using (var s = entry.Open()) xml.Load(s);
                var ns = new XmlNamespaceManager(xml.NameTable);
                ns.AddNamespace("w", W);
                var sb = new StringBuilder();
                foreach (XmlNode p in xml.SelectNodes("//w:body//w:p", ns))
                {
                    foreach (XmlNode n in p.SelectNodes(".//w:t | .//w:tab | .//w:br | .//w:cr", ns))
                    {
                        if (n.LocalName == "t") sb.Append(n.InnerText);
                        else if (n.LocalName == "tab") sb.Append('\t');
                        else sb.Append('\n');
                    }
                    sb.Append('\n');
                }
                return sb.ToString();
            }
        }

        static string ReadPdf(string path, string pdftotext)
        {
            if (string.IsNullOrEmpty(pdftotext) || !File.Exists(pdftotext))
                throw new Exception("Reading PDFs isn't set up on this computer. Please run the installer again.");
            string outFile = Path.Combine(Path.GetTempPath(), "offline-assistant-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                var psi = new ProcessStartInfo(pdftotext, "-enc UTF-8 \"" + path + "\" \"" + outFile + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = Path.GetDirectoryName(pdftotext);
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } throw new Exception("This PDF took too long to read."); }
                    if (p.ExitCode == 3)
                        throw new Exception("This PDF is locked, so its text can't be copied out.");
                    if (p.ExitCode != 0)
                        throw new Exception("This PDF couldn't be opened. It may be damaged or password-protected.");
                }
                if (!File.Exists(outFile)) throw new Exception("This PDF couldn't be read.");
                string text = File.ReadAllText(outFile, Encoding.UTF8);
                if (text.Replace("\f", "").Trim().Length < 20)
                    throw new Exception("This PDF doesn't contain any text that can be read. It may be scanned pictures of pages.");
                return text;
            }
            finally
            {
                try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
            }
        }

        // Normalises line endings and spacing so the model doesn't waste time reading blank space.
        static string Tidy(string text)
        {
            text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\f', '\n');
            text = Regex.Replace(text, "[ \\t]+\\n", "\n");
            text = Regex.Replace(text, "\\n{3,}", "\n\n");
            return text.Trim();
        }

        public static int CountWords(string text)
        {
            return Regex.Matches(text, "\\S+").Count;
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

        ChatView transcript;
        TextBox input;
        Button sendButton;
        Button newChatButton;
        Label status;
        Button attachButton;
        Panel docBar;
        Label docLabel;
        Panel bottomPanel;
        string attachedName, attachedText;   // a document waiting to be sent with the next message
        int attachedWords;
        string pdftotextPath;
        ModelChoice modelChoice;
        string modelsDir;
        ProgressBar downloadBar;

        // Saved chat memory: the engine's working memory for a chat, written to disk when you leave
        // the chat and loaded back when you return, so nothing has to be re-read.
        string memoryDir;
        bool slotSaving;                     // this engine version can save and load chat memory
        bool memoryChanged;                  // the current chat has new replies since its memory was saved
        volatile bool memoryBusy;            // saving or loading memory; sending waits for it
        string chatSystemPrompt;             // the instructions this chat started with (kept so saved memory still matches)
        int openedSavedStart;                // where the reopened chat's saved memory starts
        readonly object memLock = new object();
        readonly Queue<Action> memJobs = new Queue<Action>();
        Thread memWorker;

        // Reading progress
        volatile int readPercent = -1;       // reported by the engine, if it does
        DateTime readStarted;
        int readTokensExpected;
        string readLabel = "";
        double readTps;                      // how fast this computer reads text (tokens/s), measured as it goes
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
            // Everything is worked out from the hardware; an optional config.ini can override it.
            var cfg = new Config(Path.Combine(appDir, "config.ini"));
            modelChoice = ModelChoice.ForThisComputer();
            modelsDir = Path.Combine(appDir, "models");
            enginePath = cfg.Get("engine", FindFile(Path.Combine(appDir, "engine"), "llama-server.exe"));
            modelPath = cfg.Get("model", "");
            if (!File.Exists(modelPath)) modelPath = modelChoice.FindLocal(modelsDir) ?? Path.Combine(modelsDir, modelChoice.File);
            ctx = cfg.GetInt("context", modelChoice.Context);
            int cores = Machine.PhysicalCores();
            threads = cfg.GetInt("threads", cores);              // replies: one thread per core (measured fastest)
            threadsBatch = cfg.GetInt("threads_batch", Math.Max(cores, Environment.ProcessorCount)); // reading: all threads
            maxReply = cfg.GetInt("max_reply", 768);
            pdftotextPath = cfg.Get("pdftotext", FindFile(Path.Combine(appDir, "tools"), "pdftotext.exe"));
            readTps = cfg.GetInt("read_tps", modelChoice.ReadTps);
            kvCache = cfg.Get("kv_cache", "q8_0");
            logPath = Path.Combine(appDir, "logs\\engine.log");
            chatsDir = Path.Combine(appDir, "chats");
            memoryDir = Path.Combine(appDir, "memory");
            try { Directory.CreateDirectory(memoryDir); } catch { }

            BuildUi();
            LoadChatList();
            Shown += (s, e) => StartEngine();
            FormClosing += (s, e) =>
            {
                AbandonReply();
                SaveCurrentChat();
                Hide();                      // close at once; finish saving the chat's memory out of sight
                SaveChatMemory(true);
                engine.Stop();
            };
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
            transcriptPanel.Padding = new Padding(0);

            transcript = new ChatView();
            transcript.Dock = DockStyle.Fill;
            transcript.Font = bodyFont;

            renderTimer = new System.Windows.Forms.Timer();
            renderTimer.Interval = 100;
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

            downloadBar = new ProgressBar();
            downloadBar.Dock = DockStyle.Right;
            downloadBar.Width = 160;
            downloadBar.Visible = false;
            top.Controls.Add(status);
            top.Controls.Add(downloadBar);
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
            bottomPanel = bottom;
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 112;
            bottom.Padding = new Padding(14, 10, 14, 6);

            var hint = new Label();
            hint.Dock = DockStyle.Bottom;
            hint.Height = 20;
            hint.ForeColor = noteColor;
            hint.Font = new Font("Segoe UI", 8.5f);
            hint.Text = "Press Enter to send, Shift+Enter for a new line. You can also drag a document here. Everything stays on this computer.";

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

            attachButton = new Button();
            attachButton.Text = "Add document";
            attachButton.Dock = DockStyle.Left;
            attachButton.Width = 116;
            attachButton.FlatStyle = FlatStyle.System;
            attachButton.Click += (s, e) => ChooseDocument();

            var gapLeft = new Panel();
            gapLeft.Dock = DockStyle.Left;
            gapLeft.Width = 10;

            // Shows the document waiting to be sent, with a way to remove it.
            docBar = new Panel();
            docBar.Dock = DockStyle.Top;
            docBar.Height = 28;
            docBar.Visible = false;
            docBar.Padding = new Padding(0, 0, 0, 6);
            docLabel = new Label();
            docLabel.Dock = DockStyle.Fill;
            docLabel.TextAlign = ContentAlignment.MiddleLeft;
            docLabel.BackColor = Color.FromArgb(224, 236, 255);
            docLabel.ForeColor = textColor;
            docLabel.AutoEllipsis = true;
            docLabel.Padding = new Padding(6, 0, 0, 0);
            var removeDoc = new LinkLabel();
            removeDoc.Text = "Remove";
            removeDoc.Dock = DockStyle.Right;
            removeDoc.Width = 64;
            removeDoc.TextAlign = ContentAlignment.MiddleCenter;
            removeDoc.BackColor = docLabel.BackColor;
            removeDoc.LinkClicked += (s, e) => ClearAttachment();
            docBar.Controls.Add(docLabel);
            docBar.Controls.Add(removeDoc);

            bottom.Controls.Add(input);
            bottom.Controls.Add(gapLeft);
            bottom.Controls.Add(attachButton);
            bottom.Controls.Add(gap);
            bottom.Controls.Add(sendButton);
            bottom.Controls.Add(docBar);
            bottom.Controls.Add(hint);

            // Documents can also be dragged onto the window.
            foreach (Control c in new Control[] { this, transcript, input })
            {
                c.AllowDrop = true;
                c.DragEnter += OnDragEnter;
                c.DragDrop += OnDragDrop;
            }

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

        static string FindFile(string dir, string name)
        {
            try
            {
                string direct = Path.Combine(dir, name);
                if (File.Exists(direct)) return direct;
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, name, SearchOption.AllDirectories)) return f;
            }
            catch { }
            return Path.Combine(dir, name);
        }

        void Ui(Action a)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
        }

        void Append(string text, Font font, Color color)
        {
            transcript.AddBlock(text, font, color);
        }

        void AppendNote(string text)
        {
            Append(text + "\n\n", noteFont, noteColor);
        }

        void ShowAssistantPlaceholder()
        {
            assistantStart = transcript.AddBlock("...", bodyFont, noteColor);
            shownReply = "";
            placeholderShown = true;
        }

        // Shows the reply so far in the assistant's block.
        void ReplaceAssistantText(string text)
        {
            if (assistantStart < 0) return;
            if (!placeholderShown && text == shownReply) return;
            transcript.SetBlock(assistantStart, text, bodyFont, textColor);
            placeholderShown = false;
            shownReply = text;
        }

        // Draws waiting words at most 10 times a second, so bursts of tokens don't cause jitter.
        void RenderTick(object sender, EventArgs e)
        {
            if (busy && !writing && readTokensExpected >= 150)
            {
                int pct = readPercent;
                if (pct < 0) // estimate from this computer's measured reading speed
                {
                    double secs = (DateTime.Now - readStarted).TotalSeconds;
                    pct = (int)Math.Min(95, secs * Math.Max(1.0, readTps) * 100 / readTokensExpected);
                }
                SetStatus(readLabel + " " + pct + "%");
            }
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
            s = Regex.Replace(s, @"(?<![\w*])\*(?=\S)([^*\n]+?)(?<=\S)\*(?![\w*])", "$1"); // *italic*
            s = Regex.Replace(s, @"(?m)^#{1,6}\s*", "");
            s = Regex.Replace(s, @"(?m)^(\s*)[\*\-]\s+", "$1• ");
            return s.TrimStart();
        }

        // ---------------- Engine ----------------

        // ---------------- First run: download the model ----------------

        void DownloadModel()
        {
            string label = modelChoice.Label + ", about " + modelChoice.SizeGB.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            try
            {
                Directory.CreateDirectory(modelsDir);
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(modelsDir)));
                if (drive.AvailableFreeSpace < (long)((modelChoice.SizeGB + 1) * 1024 * 1024 * 1024))
                {
                    SetStatus("Not enough disk space");
                    AppendNote("The assistant needs about " + (modelChoice.SizeGB + 1).ToString("0", CultureInfo.InvariantCulture) +
                               " GB of free disk space. Please free some up, then open the app again.");
                    return;
                }
            }
            catch { }

            if (transcript.IsEmpty)
                AppendNote("Welcome! Before the first chat, the assistant needs to be downloaded to this computer (" + label + "). " +
                           "This only happens once. It can take a while on a slow connection, and if it's interrupted it carries on where it left off.");
            SetStatus("Downloading the assistant...");
            downloadBar.Value = 0;
            downloadBar.Visible = true;
            var t = new Thread(() =>
            {
                string error = null;
                try { DownloadFile(modelChoice, modelPath); }
                catch (Exception ex) { error = ex.Message; }
                Ui(() =>
                {
                    downloadBar.Visible = false;
                    if (error == null) { StartEngine(); return; }
                    SetStatus("Download paused");
                    var answer = MessageBox.Show(
                        "The download stopped (" + error + ").\n\nCheck the internet connection, then click Retry. " +
                        "It will carry on where it left off.", AppName, MessageBoxButtons.RetryCancel, MessageBoxIcon.Information);
                    if (answer == DialogResult.Retry) DownloadModel();
                    else AppendNote("The download is paused. Open the app again when you're connected to the internet and it will carry on.");
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // Downloads with resume: the file is fetched into a .part file and renamed when complete.
        void DownloadFile(ModelChoice m, string dest)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            string part = dest + ".part";
            string url = "https://huggingface.co/" + m.Repo + "/resolve/main/" + m.File;
            for (int attempt = 0; ; attempt++)
            {
                long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "OfflineAssistant";
                req.Timeout = 30000;
                req.ReadWriteTimeout = 60000;
                if (have > 0) req.AddRange(have);
                HttpWebResponse resp;
                try { resp = (HttpWebResponse)req.GetResponse(); }
                catch (WebException ex)
                {
                    var r = ex.Response as HttpWebResponse;
                    if (r != null && r.StatusCode == HttpStatusCode.NotFound && attempt == 0)
                    {
                        // The file was renamed upstream: look up the current name.
                        string current = LookUpModelFile(m.Repo);
                        if (current == null) throw new Exception("the assistant's file couldn't be found online");
                        url = "https://huggingface.co/" + m.Repo + "/resolve/main/" + current;
                        continue;
                    }
                    if (r != null && (int)r.StatusCode == 416) break; // already complete
                    throw new Exception(ex.Status == WebExceptionStatus.ProtocolError ? "the server said no" : "no connection");
                }
                using (resp)
                {
                    bool resumed = resp.StatusCode == HttpStatusCode.PartialContent;
                    if (!resumed) have = 0;
                    long total = have + Math.Max(0, resp.ContentLength);
                    using (var input = resp.GetResponseStream())
                    using (var output = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write))
                    {
                        var buffer = new byte[1 << 20];
                        var clock = Stopwatch.StartNew();
                        long startHave = have;
                        double lastShown = -1;
                        int n;
                        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            output.Write(buffer, 0, n);
                            have += n;
                            if (clock.Elapsed.TotalSeconds - lastShown >= 0.5)
                            {
                                lastShown = clock.Elapsed.TotalSeconds;
                                double gb = have / 1073741824.0, totalGb = total / 1073741824.0;
                                double rate = (have - startHave) / Math.Max(1, clock.Elapsed.TotalSeconds);
                                double left = rate > 0 ? (total - have) / rate : 0;
                                string eta = left > 90 ? ", about " + (int)Math.Ceiling(left / 60) + " min left" : left > 0 ? ", about a minute left" : "";
                                int pct = total > 0 ? (int)(have * 100 / total) : 0;
                                string text = "Downloading the assistant: " + gb.ToString("0.0", CultureInfo.InvariantCulture) + " of " +
                                              totalGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB" + eta;
                                Ui(() => { SetStatus(text); downloadBar.Value = Math.Max(0, Math.Min(100, pct)); });
                            }
                        }
                    }
                    if (total > 0 && have < total) throw new Exception("the connection dropped");
                }
                break;
            }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(part, dest);
        }

        static string LookUpModelFile(string repo)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://huggingface.co/api/models/" + repo);
                req.UserAgent = "OfflineAssistant";
                req.Timeout = 30000;
                using (var resp = req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    var info = new JavaScriptSerializer().DeserializeObject(reader.ReadToEnd()) as Dictionary<string, object>;
                    var siblings = info != null && info.ContainsKey("siblings") ? info["siblings"] as object[] : null;
                    if (siblings == null) return null;
                    foreach (var o in siblings)
                    {
                        var d = o as Dictionary<string, object>;
                        string name = d != null && d.ContainsKey("rfilename") ? d["rfilename"] as string : null;
                        if (name != null && name.EndsWith("Q4_K_M.gguf", StringComparison.OrdinalIgnoreCase) && !name.Contains("mmproj"))
                            return name;
                    }
                }
            }
            catch { }
            return null;
        }

        void StartEngine()
        {
            coldStart = true;
            ready = false;
            sendButton.Enabled = false;
            if (!File.Exists(enginePath))
            {
                SetStatus("Not installed properly");
                AppendNote("Some files are missing. Please run the installer again.");
                return;
            }
            if (!File.Exists(modelPath)) { DownloadModel(); return; }
            SetStatus("Getting ready... (this takes up to a minute)");
            if (transcript.IsEmpty) AppendNote("Getting ready. This can take up to a minute after the computer starts.");

            var t = new Thread(() =>
            {
                string common = "-m \"" + modelPath + "\" -c " + ctx + " -t " + threads;
                string tuned = common + " -tb " + threadsBatch + " -np 1 --no-webui";
                // Compact chat memory needs flash attention. Try the full tuned settings first;
                // if this engine version rejects any of them, step back to safer ones.
                // Trailing forward slash: a backslash before the closing quote would break the argument.
                string slotArg = " --slot-save-path \"" + memoryDir + "/\"";
                string kvArgs = " -ctk " + kvCache + " -ctv " + kvCache + " -fa on";
                // Try the full tuned settings first; if this engine version rejects any of them,
                // step back to safer ones. Saving chat memory only works in the first two.
                int tier = 0;
                if (kvCache != "f16" && TryStart(tuned + kvArgs + slotArg, false)) tier = 1;
                else if (TryStart(tuned + slotArg, kvCache != "f16")) tier = 2;
                else if (TryStart(tuned, true)) tier = 3;
                else if (TryStart(common, true)) tier = 4;
                bool ok = tier > 0;
                slotSaving = tier == 1 || tier == 2;
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
                // Read the built-in instructions now, while the user is typing, so the first reply starts sooner.
                PreRead(new List<object> { Msg("system", SystemPrompt), Msg("user", "Hello") }, false);
            }
            else if (currentChatId != null && coldStart) LoadChatMemory(currentChatId, openedSavedStart); // opened while starting up
            else PreReadCurrentChat();
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
            SaveChatMemory(false);
            currentChatId = null;
            chatSystemPrompt = null;
            memoryChanged = false;
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
                    info.Title = MakeTitle(history[0].ContainsKey("doc") ? history[0]["doc"] + ": " + ShownText(history[0]) : ShownText(history[0]));
                    chats.Add(info);
                }
                info.Updated = DateTime.Now;

                var doc = new Dictionary<string, object>();
                doc["id"] = info.Id;
                doc["title"] = info.Title;
                doc["updated"] = info.Updated.ToString("o", CultureInfo.InvariantCulture);
                doc["messages"] = history;
                doc["history_start"] = historyStart;
                if (chatSystemPrompt != null) doc["system"] = chatSystemPrompt;
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
            SaveChatMemory(false);

            var loaded = new List<Dictionary<string, object>>();
            string savedSystem = null;
            int savedStart = 0;
            try
            {
                var json = new JavaScriptSerializer();
                json.MaxJsonLength = int.MaxValue;
                var doc = json.DeserializeObject(File.ReadAllText(info.FilePath, Encoding.UTF8)) as Dictionary<string, object>;
                var msgs = doc != null && doc.ContainsKey("messages") ? doc["messages"] as object[] : null;
                if (doc != null && doc.ContainsKey("system")) savedSystem = doc["system"] as string;
                if (doc != null && doc.ContainsKey("history_start")) savedStart = ToInt(doc["history_start"]);
                if (msgs != null)
                    foreach (var m in msgs)
                    {
                        var d = m as Dictionary<string, object>;
                        if (d == null || !d.ContainsKey("role") || !d.ContainsKey("content")) continue;
                        var msg = Msg(d["role"] as string, d["content"] as string ?? "");
                        foreach (var key in new[] { "doc", "doc_words", "question" })
                            if (d.ContainsKey(key)) msg[key] = d[key];
                        loaded.Add(msg);
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
            chatSystemPrompt = savedSystem;
            memoryChanged = false;
            openedSavedStart = Math.Max(0, Math.Min(savedStart, Math.Max(0, loaded.Count - 1)));

            transcript.BeginUpdate(); // lay out the whole chat in one go
            transcript.Clear();
            foreach (var m in history)
            {
                if ((string)m["role"] == "user") { ShowUserMessage(m); continue; }
                Append("Assistant\n", headFont, botColor);
                Append(Clean((string)m["content"]) + "\n\n", bodyFont, textColor);
            }
            transcript.EndUpdate();
            transcript.ScrollToEndNow();
            // A question left unanswered (e.g. the app was closed mid-reply) stays visible,
            // but isn't sent again, so the next message starts cleanly.
            if (history.Count > 0 && (string)history[history.Count - 1]["role"] == "user")
                history.RemoveAt(history.Count - 1);

            UpdateMemory(EstimateTokens(BuildMessages()));
            SelectInList(currentChatId);
            if (ready) SetStatus("Ready");
            input.Focus();
            if (ready) LoadChatMemory(info.Id, openedSavedStart);
        }

        // ---------------- Saved chat memory and pre-reading ----------------

        // Memory jobs run one at a time, in order, on a background thread.
        void EnqueueMemoryJob(Action job)
        {
            lock (memLock) { memJobs.Enqueue(job); Monitor.Pulse(memLock); }
            if (memWorker == null)
            {
                memWorker = new Thread(() =>
                {
                    while (true)
                    {
                        Action next;
                        lock (memLock)
                        {
                            while (memJobs.Count == 0) Monitor.Wait(memLock);
                            next = memJobs.Dequeue();
                        }
                        try { next(); } catch { }
                    }
                });
                memWorker.IsBackground = true;
                memWorker.Start();
            }
        }

        Dictionary<string, object> PostJson(string path, string json, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(baseUrl + path);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Proxy = null;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                byte[] data = Encoding.UTF8.GetBytes(json);
                req.ContentLength = data.Length;
                using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (var resp = req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    var ser = new JavaScriptSerializer();
                    ser.MaxJsonLength = int.MaxValue;
                    return ser.DeserializeObject(reader.ReadToEnd()) as Dictionary<string, object>;
                }
            }
            catch { return null; }
        }

        string MemoryFile(string chatId) { return chatId + ".bin"; }

        // Writes the engine's memory of the current chat to disk (when leaving the chat or closing).
        void SaveChatMemory(bool waitForIt)
        {
            if (!slotSaving || !ready || currentChatId == null || !memoryChanged) return;
            memoryChanged = false;
            string file = MemoryFile(currentChatId);
            Action job = () =>
            {
                var r = PostJson("/slots/0?action=save", "{\"filename\":\"" + file + "\"}", 60000);
                if (r == null || !r.ContainsKey("n_saved")) { slotSaving = false; return; } // not supported: stop trying
                PruneMemoryFiles();
            };
            if (waitForIt) job(); else EnqueueMemoryJob(job);
        }

        // Loads a reopened chat's saved memory, so nothing needs re-reading. If there is none,
        // pre-reads the recent part of the chat in the background instead.
        void LoadChatMemory(string chatId, int savedStart)
        {
            string path = Path.Combine(memoryDir, MemoryFile(chatId));
            if (!slotSaving || !File.Exists(path))
            {
                PreReadCurrentChat();
                return;
            }
            memoryBusy = true;
            SetStatus("Loading this chat's memory...");
            string file = MemoryFile(chatId);
            EnqueueMemoryJob(() =>
            {
                var r = PostJson("/slots/0?action=restore", "{\"filename\":\"" + file + "\"}", 120000);
                bool ok = r != null && r.ContainsKey("n_restored") && ToInt(r["n_restored"]) > 0;
                try { File.SetLastWriteTime(path, DateTime.Now); } catch { }
                Ui(() =>
                {
                    memoryBusy = false;
                    if (currentChatId != chatId) return; // the user has moved on
                    if (ok)
                    {
                        coldStart = false;
                        historyStart = savedStart;
                        UpdateMemory(EstimateTokens(BuildMessages()));
                        if (!busy) SetStatus("Ready");
                    }
                    else
                    {
                        if (!busy) SetStatus("Ready");
                        PreReadCurrentChat();
                    }
                });
            });
        }

        // Reads the recent part of the current chat in the background while the user is typing.
        void PreReadCurrentChat()
        {
            if (!ready || history.Count == 0 || !coldStart) return;
            var messages = BuildMessages();
            coldStart = false;
            PreRead(messages, true);
        }

        void PreRead(List<object> messages, bool countsAsChatMemory)
        {
            var body = new Dictionary<string, object>();
            body["messages"] = messages;
            body["max_tokens"] = 1;
            body["cache_prompt"] = true;
            string json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(body);
            string chatId = currentChatId;
            EnqueueMemoryJob(() =>
            {
                var r = PostJson("/v1/chat/completions", json, 30 * 60 * 1000);
                if (r != null && countsAsChatMemory) Ui(() => { if (currentChatId == chatId) memoryChanged = true; });
            });
        }

        // Keeps saved memory from filling the disk: the 10 most recently used chats, 3 GB at most.
        void PruneMemoryFiles()
        {
            try
            {
                var files = new List<FileInfo>(new DirectoryInfo(memoryDir).GetFiles("*.bin"));
                files.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
                long total = 0;
                for (int i = 0; i < files.Count; i++)
                {
                    total += files[i].Length;
                    bool chatGone = !File.Exists(Path.Combine(chatsDir, Path.GetFileNameWithoutExtension(files[i].Name) + ".json"));
                    if (i >= 10 || total > 3L * 1024 * 1024 * 1024 || chatGone)
                        try { files[i].Delete(); } catch { }
                }
            }
            catch { }
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
            try { File.Delete(Path.Combine(memoryDir, MemoryFile(info.Id))); } catch { }
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

        // ---------------- Documents ----------------

        void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0) AttachDocument(files[0]);
        }

        void ChooseDocument()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Add a document to the chat";
                dlg.Filter = DocumentReader.FileFilter;
                if (dlg.ShowDialog(this) == DialogResult.OK) AttachDocument(dlg.FileName);
            }
        }

        // Rough time for this computer to read some text, using the speed measured on earlier replies.
        string ReadingTime(int chars)
        {
            double seconds = (chars / 3.2) / Math.Max(1.0, readTps);
            if (seconds < 90) return "under 2 minutes";
            return "about " + (int)Math.Ceiling(seconds / 60) + " minutes";
        }

        void AttachDocument(string path)
        {
            if (busy) return;
            string name = Path.GetFileName(path);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (!DocumentReader.IsSupported(path) && ext != ".doc")
            {
                MessageBox.Show("\"" + name + "\" can't be read. Documents ending in .txt, .docx, .pdf or .rtf work.",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SetStatus("Reading the document...");
            attachButton.Enabled = false;
            Action<string, string> done = (docText, docError) =>
            {
                attachButton.Enabled = true;
                SetStatus(ready ? "Ready" : status.Text);
                if (docError != null)
                {
                    MessageBox.Show(docError, AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                OfferDocument(name, docText);
            };
            if (ext == ".rtf")
            {
                // Needs the UI thread.
                string text = null, error = null;
                try { text = DocumentReader.Read(path, pdftotextPath); } catch (Exception ex) { error = ex.Message; }
                done(text, error);
                return;
            }
            var t = new Thread(() =>
            {
                string text = null, error = null;
                try { text = DocumentReader.Read(path, pdftotextPath); }
                catch (Exception ex) { error = ex.Message; }
                Ui(() => done(text, error));
            });
            t.IsBackground = true;
            t.Start();
        }

        void OfferDocument(string name, string text)
        {
            if (text.Length == 0)
            {
                MessageBox.Show("\"" + name + "\" seems to be empty.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int words = DocumentReader.CountWords(text);
            int maxChars = PromptBudgetChars() * 8 / 10;
            if (text.Length > maxChars)
            {
                int maxWords = (int)((long)words * maxChars / text.Length);
                MessageBox.Show(
                    "\"" + name + "\" is too long for this computer to read in one go (about " + words.ToString("N0") +
                    " words; the limit is about " + maxWords.ToString("N0") + ").\n\nTry a shorter document, or copy in just the part you need.",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            double seconds = (text.Length / 3.2) / Math.Max(1.0, readTps);
            if (seconds > 120)
            {
                var answer = MessageBox.Show(
                    "\"" + name + "\" is about " + words.ToString("N0") + " words. This computer will take " + ReadingTime(text.Length) +
                    " to read it before it can answer.\n\nAdd it anyway?",
                    AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
            }
            attachedName = name;
            attachedText = text;
            attachedWords = words;
            docLabel.Text = "Document: " + name + "  (about " + words.ToString("N0") + " words)";
            if (!docBar.Visible) { docBar.Visible = true; bottomPanel.Height += docBar.Height; }
            input.Focus();
        }

        void ClearAttachment()
        {
            attachedName = null;
            attachedText = null;
            attachedWords = 0;
            if (docBar.Visible) { docBar.Visible = false; bottomPanel.Height -= docBar.Height; }
        }

        // What a user message looks like in the chat history (documents aren't shown in full).
        static string DocLine(Dictionary<string, object> m)
        {
            if (!m.ContainsKey("doc")) return null;
            int words = m.ContainsKey("doc_words") ? ToInt(m["doc_words"]) : 0;
            return "Document: " + m["doc"] + (words > 0 ? "  (about " + words.ToString("N0") + " words)" : "");
        }

        static string ShownText(Dictionary<string, object> m)
        {
            return m.ContainsKey("question") ? (string)m["question"] : (string)m["content"];
        }

        void ShowUserMessage(Dictionary<string, object> m)
        {
            Append("You\n", headFont, userColor);
            string docLine = DocLine(m);
            if (docLine != null) Append(docLine + "\n", noteFont, noteColor);
            Append(ShownText(m).Replace("\r\n", "\n") + "\n\n", bodyFont, textColor);
        }

        void SendOrStop()
        {
            if (busy) { StopGenerating(); return; }
            if (!ready) return;
            if (memoryBusy) { SetStatus("One moment, loading this chat's memory..."); return; }
            if (chatSystemPrompt == null) chatSystemPrompt = SystemPrompt;

            string text = input.Text.Trim();
            bool hasDoc = attachedText != null;
            if (text.Length == 0 && !hasDoc) return;
            if (hasDoc && text.Length == 0) text = "Please summarise this document.";
            if (text.Length > Math.Min(PromptBudgetChars() * 8 / 10, MaxMessageChars))
            {
                MessageBox.Show(
                    "That's too long for this computer to read in one go.\n\nPlease paste a shorter piece (about 1,000 words or less).",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (hasDoc && attachedText.Length + text.Length > PromptBudgetChars() * 8 / 10)
            {
                MessageBox.Show("The document and your message together are too long. Please shorten your message.",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            input.Clear();
            Dictionary<string, object> userMsg;
            if (hasDoc)
            {
                // The model sees the document's full text; the chat shows just its name and the question.
                userMsg = Msg("user",
                    "I've shared a document called \"" + attachedName + "\". Its text is between the lines below.\n" +
                    "----------\n" + attachedText + "\n----------\n\n" + text);
                userMsg["doc"] = attachedName;
                userMsg["doc_words"] = attachedWords;
                userMsg["question"] = text;
                ClearAttachment();
            }
            else userMsg = Msg("user", text);
            history.Add(userMsg);
            dirty = true;

            ShowUserMessage(userMsg);
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
            int startBefore = historyStart;
            var messages = BuildMessages();
            var forgotten = new List<string>();
            for (int i = startBefore; i < historyStart; i++)
                if (history[i].ContainsKey("doc")) forgotten.Add("\"" + history[i]["doc"] + "\"");
            if (wasCold) // reopened chat: anything not re-read is forgotten
                for (int i = 0; i < startBefore && i < history.Count; i++)
                    if (history[i].ContainsKey("doc") && !forgotten.Contains("\"" + history[i]["doc"] + "\"")) forgotten.Add("\"" + history[i]["doc"] + "\"");
            coldStart = false;
            bool catchingUp = (wasCold && messages.Count > 2) || lastBuildTrimmed;
            if (forgotten.Count > 0 && assistantStart >= 1)
            {
                // Goes just above the "Assistant" label of the reply that's about to be written.
                transcript.InsertBlock(assistantStart - 1,
                    "The assistant no longer has " + string.Join(", ", forgotten.ToArray()) +
                    " in its memory. Add it again if you have more questions about it.\n\n", noteFont, noteColor);
                assistantStart++;
            }
            readLabel = hasDoc ? "Reading the document..." : catchingUp ? "Catching up on the conversation..." : "Reading your message...";
            SetStatus(readLabel);
            int usedBefore = usedTokens;
            liveBase = EstimateTokens(messages);
            // How much new text the engine has to read (the rest is already in its memory).
            readTokensExpected = (wasCold || lastBuildTrimmed) ? liveBase : Math.Max(0, liveBase - usedBefore);
            readStarted = DateTime.Now;
            readPercent = -1;
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
            list.Add(Msg("system", chatSystemPrompt ?? SystemPrompt));
            for (int i = historyStart; i < history.Count; i++)
                list.Add(Msg((string)history[i]["role"], (string)history[i]["content"]));
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
                body["return_progress"] = true; // newer engines report reading progress while streaming
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
                        int progress = ReadProgress(parsed);
                        if (progress >= 0) readPercent = progress;
                        double speed = ReadPromptSpeed(parsed);
                        if (speed > 0) readTps = speed;
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

        // Reading progress the engine reports while it reads the prompt, as a percentage (or -1).
        static int ReadProgress(object parsed)
        {
            var obj = parsed as Dictionary<string, object>;
            var p = obj != null && obj.ContainsKey("prompt_progress") ? obj["prompt_progress"] as Dictionary<string, object> : null;
            if (p == null || !p.ContainsKey("total") || !p.ContainsKey("processed")) return -1;
            int cache = p.ContainsKey("cache") ? ToInt(p["cache"]) : 0;
            int todo = ToInt(p["total"]) - cache;
            if (todo <= 0) return 100;
            return Math.Max(0, Math.Min(100, (ToInt(p["processed"]) - cache) * 100 / todo));
        }

        // How fast the engine read the new part of the prompt (tokens/s), if it said and the sample is big enough.
        static double ReadPromptSpeed(object parsed)
        {
            var obj = parsed as Dictionary<string, object>;
            var timings = obj != null && obj.ContainsKey("timings") ? obj["timings"] as Dictionary<string, object> : null;
            if (timings == null || !timings.ContainsKey("prompt_n") || !timings.ContainsKey("prompt_per_second")) return -1;
            if (ToInt(timings["prompt_n"]) < 64) return -1;
            try { return Convert.ToDouble(timings["prompt_per_second"], CultureInfo.InvariantCulture); }
            catch { return -1; }
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
                memoryChanged = true;
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
