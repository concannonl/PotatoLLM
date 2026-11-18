// Offline Assistant - a minimal native chat window for a local llama.cpp server.
// Written for C# 5 so it compiles with the csc.exe built into every Windows (.NET Framework 4.x).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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
        const string SystemPrompt =
            "You are a helpful assistant running privately on the user's own computer, with no internet access. " +
            "Answer clearly and concisely in plain text. Do not use markdown, tables or headings.";

        RichTextBox transcript;
        TextBox input;
        Button sendButton;
        Button newChatButton;
        Label status;
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
        bool dirty;                // current chat has changes not yet saved
        int ctx, threads, threadsBatch, maxReply;
        volatile string baseUrl = "";

        bool ready, busy;
        volatile bool stopRequested;
        HttpWebRequest currentRequest;
        int assistantStart = -1;
        bool writing;

        readonly List<Dictionary<string, object>> history = new List<Dictionary<string, object>>();
        int historyStart = 0; // older messages before this index are no longer sent

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
            top.Controls.Add(status);

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

        void Append(string text, Font font, Color color)
        {
            transcript.ReadOnly = false;
            transcript.SelectionStart = transcript.TextLength;
            transcript.SelectionLength = 0;
            transcript.SelectionFont = font;
            transcript.SelectionColor = color;
            transcript.SelectedText = text;
            transcript.ReadOnly = true;
            ScrollToEnd();
        }

        void AppendNote(string text)
        {
            Append(text + "\n\n", noteFont, noteColor);
        }

        void ScrollToEnd()
        {
            transcript.SelectionStart = transcript.TextLength;
            transcript.SelectionLength = 0;
            transcript.ScrollToCaret();
        }

        void ReplaceAssistantText(string text)
        {
            if (assistantStart < 0) return;
            transcript.ReadOnly = false;
            transcript.SelectionStart = assistantStart;
            transcript.SelectionLength = transcript.TextLength - assistantStart;
            transcript.SelectionFont = bodyFont;
            transcript.SelectionColor = textColor;
            transcript.SelectedText = text;
            transcript.ReadOnly = true;
            ScrollToEnd();
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
                // Tuned settings first; if this engine version rejects any of them, fall back to basics.
                bool ok = TryStart(common + " -tb " + threadsBatch + " -np 1 --no-webui", false)
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
                AppendNote("Ready. Ask me anything. Everything stays on this computer and nothing is sent to the internet.");
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
            dirty = false;

            transcript.Clear();
            foreach (var m in history)
            {
                bool isUser = (string)m["role"] == "user";
                Append(isUser ? "You\n" : "Assistant\n", headFont, isUser ? userColor : botColor);
                string text = (string)m["content"];
                Append((isUser ? text.Replace("\r\n", "\n") : Clean(text)) + "\n\n", bodyFont, textColor);
            }
            // A question left unanswered (e.g. the app was closed mid-reply) stays visible,
            // but isn't sent again, so the next message starts cleanly.
            if (history.Count > 0 && (string)history[history.Count - 1]["role"] == "user")
                history.RemoveAt(history.Count - 1);

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
            if (text.Length > PromptBudgetChars() * 8 / 10)
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
            assistantStart = transcript.TextLength;
            Append("...", bodyFont, noteColor);

            busy = true;
            writing = false;
            stopRequested = false;
            partialReply = "";
            sendButton.Text = "Stop";
            SetStatus("Reading your message...");
            SaveCurrentChat(); // the chat appears in the list as soon as it starts

            int myId = ++genId;
            var messages = BuildMessages();
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

            if (total > budget)
            {
                int target = budget * 6 / 10;
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
                        string piece = ExtractDelta(json.DeserializeObject(payload));
                        if (string.IsNullOrEmpty(piece)) continue;
                        reply.Append(piece);
                        string snapshot = reply.ToString();
                        Ui(() => ShowPartial(myId, snapshot));
                    }
                }
            }
            catch (Exception ex)
            {
                if (!stopRequested) error = ex.Message;
            }
            string final = reply.ToString();
            Ui(() => FinishReply(myId, final, error));
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

        void ShowPartial(int myId, string text)
        {
            if (!busy || myId != genId) return;
            partialReply = text;
            if (!writing) { writing = true; SetStatus("Writing..."); }
            ReplaceAssistantText(Clean(text));
        }

        void FinishReply(int myId, string final, string error)
        {
            if (myId != genId) return; // belongs to a chat the user has left
            bool wasStopped = stopRequested;
            dirty = true;
            busy = false;
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
