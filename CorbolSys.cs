// CorbolSys - widget desktop + icona nella tray: CPU, RAM, GPU, dischi, rete, temperatura, batteria e processi
// più pesanti, con avvisi quando qualcosa resta al limite e un'analisi approfondita con assistente AI.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using WinTimer = System.Windows.Forms.Timer;

[assembly: System.Reflection.AssemblyTitle("CorbolSys - monitor del sistema")]

namespace CorbolSys
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SetCurrentProcessExplicitAppUserModelID(string id);

        // Deve coincidere con AppUserModelId in Setup.cs (collegamento del menu Start)
        public const string AppUserModelId = "AppSys.Widget"; // come prima del cambio di nome: le icone già fissate alla barra restano della stessa app

        //   (nessuno) / /front -> mostra il widget     /analysis -> apre l'analisi     /exit -> chiude l'app
        //   /report <file>     -> analisi completa salvata su file, senza interfaccia (per l'IT o per gli script)
        //   /snapshot <file> [analysis] -> immagine PNG del widget (o della finestra di analisi), poi esce
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length >= 2 && args[0].Equals("/report", StringComparison.OrdinalIgnoreCase))
            {
                L.LoadFromSettings();
                SysMonitor.HistoryEnabled = false;
                SysMonitor.Instance.Start();
                Thread.Sleep(6000);   // qualche campione di CPU, dischi e processi
                var a = new Analysis();
                a.Run();
                File.WriteAllText(args[1], System.Text.RegularExpressions.Regex.Replace(a.ReportText(), "\r?\n", "\r\n"), new UTF8Encoding(true));
                return;
            }
            if (args.Length >= 2 && args[0].Equals("/snapshot", StringComparison.OrdinalIgnoreCase))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                SysMonitor.HistoryEnabled = false;
                WidgetForm.SnapshotPath = args[1];
                WidgetForm.SnapshotAi = args.Length > 2 && args[2].Equals("ai", StringComparison.OrdinalIgnoreCase);
                if (args.Length > 2 && (args[2].Equals("space", StringComparison.OrdinalIgnoreCase) || args[2].Equals("cleanup", StringComparison.OrdinalIgnoreCase)))
                {
                    WidgetForm.SnapshotWindow = args[2].ToLowerInvariant();
                    WidgetForm.SnapshotArg = args.Length > 3 ? args[3] : null;
                }
                WidgetForm.SnapshotAnalysis = WidgetForm.SnapshotAi || (args.Length > 2 && args[2].Equals("analysis", StringComparison.OrdinalIgnoreCase));
                Application.Run(new WidgetForm("snapshot"));
                return;
            }
            OldApp.MoveLeftovers(); // dati rimasti nelle cartelle col nome vecchio (AppSys)
            try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch { }
            string cmd = args.Length > 0 ? args[0].TrimStart('/', '-').ToLowerInvariant() : "front";
            if (cmd != "exit" && cmd != "analysis" && cmd != "topmost") cmd = "front";
            bool createdNew;
            using (var mutex = new Mutex(true, "CorbolSys_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    AllowSetForegroundWindow(-1);
                    try { EventWaitHandle.OpenExisting(WidgetForm.EventPrefix + cmd).Set(); } catch { }
                    return;
                }
                if (cmd == "exit") return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new WidgetForm(cmd));
            }
        }
    }

    class Palette
    {
        public Color Back, Border, Label, Value, Accent, Ram, Gpu, Good, Warn, Bad, Track;
    }

    // Dimensione del testo di menu e finestre (il predefinito di Windows è 9 pt)
    static class UiFont
    {
        public const float Pt = 11f;   // come CorbolIp
        public static readonly float Scale = Pt / 9f;   // le coordinate delle finestre sono pensate per 9 pt
    }

    class AboutForm : Form
    {
        static AboutForm open;

        public static void ShowOnce(Icon icon)
        {
            if (open == null || open.IsDisposed) open = new AboutForm(icon);
            open.Show();
            open.Activate();
        }

        AboutForm(Icon icon)
        {
            Text = L.T("Informazioni su CorbolSys", "About CorbolSys");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", UiFont.Pt);
            BackColor = Color.White;
            if (icon != null) Icon = icon;
            var pic = new PictureBox { Location = new Point(20, 20), Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom };
            try
            {
                // Le immagini in corbolsys.ico sono PNG (Icon.ToBitmap non le decodifica): si legge quella da 64 px, come in CorbolIp
                using (var st = WidgetForm.IconStream())
                using (var ms = new MemoryStream())
                {
                    st.CopyTo(ms);
                    byte[] d = ms.ToArray();
                    int n = BitConverter.ToUInt16(d, 4);
                    for (int i = 0; i < n; i++)
                    {
                        int e = 6 + 16 * i;
                        if (d[e] != 64) continue;
                        int size = BitConverter.ToInt32(d, e + 8), off = BitConverter.ToInt32(d, e + 12);
                        using (var img = Image.FromStream(new MemoryStream(d, off, size))) pic.Image = new Bitmap(img);
                        break;
                    }
                }
            }
            catch { }
            var title = new Label { Text = "CorbolSys " + AppVersion.Text, AutoSize = true, Location = new Point(82, 18), Font = new Font("Segoe UI Semibold", 14f * UiFont.Scale) };
            var desc = new Label
            {
                Text = L.T("Monitor del sistema sul desktop, con analisi approfondita e assistente AI", "System monitor on your desktop, with in-depth analysis and AI assistant"),
                AutoSize = true, MaximumSize = new Size(260, 0), Location = new Point(84, 50), ForeColor = Color.DimGray
            };
            var author = new Label { Text = "© 2026 " + AppVersion.Author, AutoSize = true, Location = new Point(84, 90) };
            // "Licenza MIT · GitHub" come in CorbolIp: un solo LinkLabel con due collegamenti. Sottolineatura sempre presente
            // (AlwaysUnderline): con HoverUnderline il carattere cambia al passaggio del mouse e il testo si ridisegna male
            var blue = Color.FromArgb(37, 99, 235);
            string lic = L.T("Licenza MIT", "MIT License");
            var links = new LinkLabel
            {
                Text = lic + "  ·  GitHub", AutoSize = true, Location = new Point(84, 112),
                LinkColor = blue, ActiveLinkColor = blue, LinkBehavior = LinkBehavior.AlwaysUnderline
            };
            links.Links.Clear();
            links.Links.Add(0, lic.Length, AppVersion.RepoUrl + "/blob/main/LICENSE");
            links.Links.Add(lic.Length + 5, 6, AppVersion.RepoUrl);
            links.LinkClicked += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo((string)e.Link.LinkData) { UseShellExecute = true }); } catch { }
            };
            var ok = new Button { Text = "OK", Size = new Size(90, 30) };
            ok.Click += delegate { Close(); };
            AcceptButton = CancelButton = ok;
            Controls.AddRange(new Control[] { pic, title, desc, author, links, ok });
            ClientSize = new Size(360, 200);
            Scale(new SizeF(UiFont.Scale, UiFont.Scale));
            Load += delegate
            {
                author.Top = desc.Bottom + 14;
                links.Top = author.Bottom + 4;
                ok.Location = new Point(ClientSize.Width - 16 - ok.Width, links.Bottom + 16);
                ClientSize = new Size(ClientSize.Width, ok.Bottom + 14);
            };
        }
    }

    class WidgetForm : Form
    {
        [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int idx, IntPtr val);
        [DllImport("user32.dll")] static extern IntPtr SetWindowLong(IntPtr hWnd, int idx, IntPtr val);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        const int GWL_HWNDPARENT = -8, WS_EX_TOOLWINDOW = 0x80, WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string AppName = "CorbolSys";
        public const string EventPrefix = "CorbolSys_Cmd_";
        public static string SnapshotPath;          // /snapshot: salva un'immagine ed esce (niente tray, niente impostazioni salvate)
        public static bool SnapshotAnalysis;
        readonly List<EventWaitHandle> cmdEvents = new List<EventWaitHandle>();

        readonly NotifyIcon tray;
        readonly Icon appIcon;
        readonly WinTimer paintTimer, alertTimer;
        readonly ToolStripMenuItem miTopMost, miStartup, miShow, miCompact;
        readonly ToolStripMenuItem[] miTheme, miOpacity, miLang;
        readonly List<Action> relabel = new List<Action>();
        static readonly int[] OpacityValues = { 100, 90, 80, 70, 60, 50 };
        readonly Font fTitle = new Font("Segoe UI Semibold", 13f);
        readonly Font fLabel = new Font("Segoe UI", 8.5f);
        readonly Font fSmall = new Font("Segoe UI", 7.5f);
        readonly Font fValue = new Font("Consolas", 10f);
        readonly string settingsPath;

        // impostazioni
        bool topMostMode, compact, tempFront;
        bool showGpu = true, showDisks = true, showNet = true, showProcs = true, showTemp = true, trayCpu = true, alerts = true;
        string theme = "auto";
        int opacity = 90;

        // aggiornamenti: controllo automatico disattivato di default (niente traffico non richiesto)
        bool autoUpdate;
        string updVersion, updUrl, updSha;   // ultima release più recente di questa (null = nessuna)
        int checkingUpd;
        DateTime lastUpdCheck = DateTime.MinValue;
        bool balloonIsUpdate;
        readonly ToolStripMenuItem miUpdateAvail;
        readonly WinTimer updTimer;

        public WidgetForm(string startCmd)
        {
            settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CorbolSys", "settings.txt");
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            Width = 330;
            Height = 300;
            L.Set(ReadSetting("lang") ?? "auto");

            // ----- menu -----
            var menu = new ContextMenuStrip { Font = new Font("Segoe UI", UiFont.Pt) };
            menu.Items.Add(new ToolStripMenuItem("CorbolSys " + AppVersion.Text) { Enabled = false });
            // compare solo quando c'è una versione più recente
            miUpdateAvail = new ToolStripMenuItem("", null, delegate { OfferUpdate(); }) { Visible = false };
            relabel.Add(delegate { if (updVersion != null) miUpdateAvail.Text = L.F("Aggiorna a CorbolSys {0}...", "Update to CorbolSys {0}...", updVersion); });
            menu.Items.Add(miUpdateAvail);
            menu.Items.Add(new ToolStripSeparator());
            var miAnalysis = Mi("Analisi approfondita...", "In-depth analysis...", delegate { AnalysisForm.ShowOnce(appIcon); });
            // niente grassetto, come in CorbolIp: il menu calcola la larghezza con il carattere normale e su schermi
            // ridimensionati (125%) una voce in grassetto può risultare tagliata
            menu.Items.Add(miAnalysis);
            menu.Items.Add(Mi("Spazio su disco...", "Disk space...", delegate { DiskSpaceForm.ShowOnce(appIcon, null); }));
            menu.Items.Add(Mi("Pulizia disco...", "Disk cleanup...", delegate { CleanupForm.ShowOnce(appIcon); }));
            menu.Items.Add(Mi("Assistente AI...", "AI assistant...", delegate { AiSettingsForm.ShowOnce(appIcon, null); }));
            menu.Items.Add(Mi("Gestione attività", "Task Manager", delegate { Launch("taskmgr.exe", ""); }));
            menu.Items.Add(Mi("Copia stato attuale", "Copy current status", delegate { try { Clipboard.SetText(StatusText()); tray.ShowBalloonTip(1500, "CorbolSys", L.T("Stato copiato negli appunti", "Status copied to clipboard"), ToolTipIcon.Info); } catch { } }));
            menu.Items.Add(new ToolStripSeparator());
            miShow = Mi("Mostra widget", "Show widget", delegate { ToggleWidget(); });
            miShow.Checked = true;
            menu.Items.Add(miShow);

            var miDetails = Mi("Dettagli da mostrare", "Details to show", null);
            Toggle(miDetails, "Scheda video (GPU)", "Graphics card (GPU)", () => showGpu, v => showGpu = v);
            Toggle(miDetails, "Dischi", "Disks", () => showDisks, v => showDisks = v);
            Toggle(miDetails, "Rete", "Network", () => showNet, v => showNet = v);
            Toggle(miDetails, "Temperatura e batteria", "Temperature and battery", () => showTemp, v => showTemp = v);
            Toggle(miDetails, "Processi più pesanti", "Heaviest processes", () => showProcs, v => showProcs = v);
            menu.Items.Add(miDetails);

            var miMon = Mi("Monitoraggio", "Monitoring", null);
            Toggle(miMon, "Avvisi quando qualcosa resta al limite", "Alerts when something stays at the limit", () => alerts, v => alerts = v);
            Toggle(miMon, "Uso della CPU nell'icona della tray", "CPU usage in the tray icon", () => trayCpu, v => { trayCpu = v; UpdateTray(); });
            Toggle(miMon, "Registra la cronologia (7 giorni, solo sul PC)", "Record history (7 days, on this PC only)", () => SysMonitor.HistoryEnabled, v => SysMonitor.HistoryEnabled = v);
            miMon.DropDownItems.Add(Mi("Apri la cronologia", "Open history", delegate
            {
                if (File.Exists(SysMonitor.HistoryPath)) Launch("notepad.exe", "\"" + SysMonitor.HistoryPath + "\"");
                else MessageBox.Show(L.T("Nessun dato registrato finora.", "No data recorded yet."), "CorbolSys", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }));
            menu.Items.Add(miMon);

            var miLook = Mi("Aspetto", "Appearance", null);
            // testi scritti per esteso in Mi: così lang-check.ps1 li trova
            miTheme = new[]
            {
                Mi("Tema automatico", "Automatic theme", null), Mi("Tema scuro", "Dark theme", null), Mi("Tema chiaro", "Light theme", null)
            };
            string[] themeKeys = { "auto", "dark", "light" };
            for (int i = 0; i < 3; i++)
            {
                string key = themeKeys[i];
                miTheme[i].Click += delegate { theme = key; ApplyLook(); SaveSettings(); };
                miTheme[i].Tag = key;
                miLook.DropDownItems.Add(miTheme[i]);
            }
            miLook.DropDownItems.Add(new ToolStripSeparator());
            var miOp = Mi("Opacità", "Opacity", null);
            miOpacity = new ToolStripMenuItem[OpacityValues.Length];
            for (int i = 0; i < OpacityValues.Length; i++)
            {
                int v = OpacityValues[i];
                miOpacity[i] = new ToolStripMenuItem(v + "%", null, delegate { opacity = v; ApplyLook(); SaveSettings(); }) { Tag = v };
                miOp.DropDownItems.Add(miOpacity[i]);
            }
            miLook.DropDownItems.Add(miOp);
            miCompact = Mi("Modalità compatta", "Compact mode", delegate { compact = !compact; ApplyLook(); SaveSettings(); });
            miLook.DropDownItems.Add(miCompact);
            menu.Items.Add(miLook);

            var miLangMenu = new ToolStripMenuItem("Lingua / Language");
            var langKeys = new List<string> { "auto" };
            langKeys.AddRange(L.All().Select(x => x.Code));
            miLang = new ToolStripMenuItem[langKeys.Count];
            for (int i = 0; i < langKeys.Count; i++)
            {
                string key = langKeys[i];
                miLang[i] = i == 0 ? Mi("Automatica (come Windows)", "Automatic (same as Windows)", null) : new ToolStripMenuItem(L.All()[i - 1].Name);
                miLang[i].Click += delegate { SetLanguage(key); };
                miLang[i].Tag = key;
                miLangMenu.DropDownItems.Add(miLang[i]);
                if (i == 0) miLangMenu.DropDownItems.Add(new ToolStripSeparator());
            }
            menu.Items.Add(miLangMenu);
            miTopMost = Mi("Sempre in primo piano", "Always on top", delegate { SetTopMost(!topMostMode); SaveSettings(); });
            menu.Items.Add(miTopMost);
            miStartup = Mi("Avvia con Windows", "Start with Windows", delegate { ToggleStartup(); });
            miStartup.Checked = IsStartupEnabled();
            menu.Items.Add(miStartup);
            var miUpd = Mi("Aggiornamenti", "Updates", null);
            miUpd.DropDownItems.Add(Mi("Controlla ora", "Check now", delegate { CheckUpdates(true); }));
            Toggle(miUpd, "Controlla automaticamente (una volta al giorno)", "Check automatically (once a day)", () => autoUpdate, v =>
            {
                autoUpdate = v;
                if (v) CheckUpdates(false);
            });
            menu.Items.Add(miUpd);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Mi("Informazioni su CorbolSys", "About CorbolSys", delegate { AboutForm.ShowOnce(appIcon); }));
            menu.Items.Add(Mi("Esci", "Exit", delegate { Close(); }));
            menu.Opening += delegate { foreach (var m in miLang) m.Checked = (string)m.Tag == L.Setting; };
            ContextMenuStrip = menu;

            // ----- tray -----
            appIcon = LoadAppIcon();
            Icon = appIcon;
            tray = new NotifyIcon { Icon = appIcon, ContextMenuStrip = menu, Visible = true, Text = "CorbolSys" };
            tray.DoubleClick += delegate { ToggleWidget(); };
            tray.BalloonTipClicked += delegate
            {
                if (balloonIsUpdate) { balloonIsUpdate = false; OfferUpdate(); }
                else AnalysisForm.ShowOnce(appIcon);
            };
            tray.BalloonTipClosed += delegate { balloonIsUpdate = false; };

            // ----- mouse: trascina per spostare, clic su un disco apre lo spazio su disco, doppio clic l'analisi -----
            MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (e.Clicks == 2) { pressing = false; if (VolumeAt(e.Location) == null) AnalysisForm.ShowOnce(appIcon); return; }
                pressing = true;
                downPt = e.Location;
            };
            MouseMove += (s, e) =>
            {
                if (pressing && (Math.Abs(e.X - downPt.X) > 4 || Math.Abs(e.Y - downPt.Y) > 4))
                {
                    pressing = false;
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                    SaveSettings();
                    return;
                }
                Cursor = VolumeAt(e.Location) != null ? Cursors.Hand : Cursors.Default;
            };
            MouseUp += (s, e) =>
            {
                if (!pressing) return;
                pressing = false;
                string v = VolumeAt(e.Location);
                if (v != null) DiskSpaceForm.ShowOnce(appIcon, v);
            };

            LoadSettings();
            Settings.SaveHandler = SaveSettings;   // l'assistente AI salva le sue impostazioni nello stesso file
            ApplyLook();
            SystemEvents.UserPreferenceChanged += OnUserPrefChanged;

            if (SnapshotPath != null) { tray.Visible = false; StartSnapshot(); }
            else ListenCommand("front", BringToFront);
            if (SnapshotPath == null)
            {
                ListenCommand("analysis", delegate { AnalysisForm.ShowOnce(appIcon); });
                ListenCommand("topmost", delegate { if (!Visible) ToggleWidget(); SetTopMost(!topMostMode); SaveSettings(); });
                ListenCommand("exit", Close);
                Load += delegate { UpdateJumpList(); };
            }
            if (startCmd == "analysis") Shown += delegate { AnalysisForm.ShowOnce(appIcon); };
            if (startCmd == "topmost") Load += delegate { SetTopMost(true); SaveSettings(); };

            SysMonitor.Instance.Start();
            paintTimer = new WinTimer { Interval = 1000 };
            paintTimer.Tick += delegate { if (Visible) { ResizeToContent(); Invalidate(); } UpdateTray(); };
            paintTimer.Start();
            alertTimer = new WinTimer { Interval = 5000 };
            alertTimer.Tick += delegate { CheckAlerts(); };
            alertTimer.Start();

            // Aggiornamenti: primo controllo un minuto dopo l'avvio, poi ogni 24 ore (solo se attivato)
            updTimer = new WinTimer { Interval = 60000 };
            updTimer.Tick += delegate
            {
                updTimer.Interval = 600000;
                if (autoUpdate && SnapshotPath == null && DateTime.Now - lastUpdCheck > TimeSpan.FromHours(24)) CheckUpdates(false);
            };
            updTimer.Start();
        }

        ToolStripMenuItem Mi(string it, string en, EventHandler click)
        {
            var m = new ToolStripMenuItem(L.T(it, en), null, click);
            relabel.Add(delegate { m.Text = L.T(it, en); });
            return m;
        }

        ToolStripMenuItem Toggle(ToolStripMenuItem parent, string it, string en, Func<bool> get, Action<bool> set)
        {
            var m = Mi(it, en, null);
            m.Click += delegate { set(!get()); m.Checked = get(); SaveSettings(); ResizeToContent(); Invalidate(); };
            parent.DropDownItems.Add(m);
            parent.DropDownOpening += delegate { m.Checked = get(); };
            return m;
        }

        static void Launch(string exe, string args)
        {
            try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }); } catch { }
        }

        void SetLanguage(string key)
        {
            L.Set(key);
            foreach (var a in relabel) a();
            SaveSettings();
            if (SnapshotPath == null) UpdateJumpList();
            ResizeToContent();
            Invalidate();
        }

        // ---------- Jump List (tasto destro sull'icona fissata nella barra delle applicazioni) ----------
        // Compare sull'icona fissata dal menu Start: il collegamento creato dall'installer ha lo stesso
        // AppUserModelID dell'app (Program.AppUserModelId). Le voci esistono solo mentre CorbolSys è aperta.
        void UpdateJumpList()
        {
            try
            {
                string exe = Application.ExecutablePath;
                var jl = new System.Windows.Shell.JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
                jl.JumpItems.Add(new System.Windows.Shell.JumpTask
                {
                    Title = L.T("Analisi approfondita", "In-depth analysis"),
                    Description = L.T("Controlla la salute del PC", "Checks the health of the PC"),
                    ApplicationPath = exe, Arguments = "/analysis", IconResourcePath = exe
                });
                jl.JumpItems.Add(new System.Windows.Shell.JumpTask
                {
                    Title = L.T("Mostra in primo piano", "Bring to front"),
                    Description = L.T("Porta il widget sopra le altre finestre", "Show the widget above other windows"),
                    ApplicationPath = exe, Arguments = "/front", IconResourcePath = exe
                });
                jl.JumpItems.Add(new System.Windows.Shell.JumpTask
                {
                    Title = L.T("Sempre in primo piano (sì/no)", "Always on top (on/off)"),
                    Description = L.T("Attiva o disattiva Sempre in primo piano", "Turn Always on top on or off"),
                    ApplicationPath = exe, Arguments = "/topmost", IconResourcePath = exe
                });
                jl.JumpItems.Add(new System.Windows.Shell.JumpTask
                {
                    Title = L.T("Esci da CorbolSys", "Exit CorbolSys"),
                    Description = L.T("Chiude l'app se è aperta", "Closes the app if it is running"),
                    ApplicationPath = exe, Arguments = "/exit", IconResourcePath = exe
                });
                jl.Apply();
            }
            catch { }
        }

        // All'uscita si svuota: le voci servono solo mentre CorbolSys è aperta
        static void ClearJumpList()
        {
            try { new System.Windows.Shell.JumpList { ShowFrequentCategory = false, ShowRecentCategory = false }.Apply(); }
            catch { }
        }

        string ReadSetting(string key)
        {
            try
            {
                if (File.Exists(settingsPath))
                    foreach (var ln in File.ReadAllLines(settingsPath))
                        if (ln.StartsWith(key + "=")) return ln.Substring(key.Length + 1).Trim();
            }
            catch { }
            return null;
        }

        void ListenCommand(string name, Action action)
        {
            var ev = new EventWaitHandle(false, EventResetMode.AutoReset, EventPrefix + name);
            cmdEvents.Add(ev);
            ThreadPool.RegisterWaitForSingleObject(ev, delegate { try { BeginInvoke(action); } catch { } }, null, -1, false);
        }

        new void BringToFront()
        {
            if (!Visible) ToggleWidget();
            if (topMostMode) { Activate(); return; }
            tempFront = true;
            TopMost = true;
            Activate();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (tempFront) { tempFront = false; if (!topMostMode) TopMost = false; }
        }

        void OnUserPrefChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (theme == "auto") try { BeginInvoke(new Action(ApplyLook)); } catch { }
        }

        // ===================== Finestra / z-order (come CorbolIp: incollata al desktop) =====================
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_TOOLWINDOW; return cp; }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
        const int WM_WINDOWPOSCHANGING = 0x46;
        const uint SWP_NOZORDER = 0x4;
        static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_WINDOWPOSCHANGING && !topMostMode && !tempFront)
            {
                var wp = (WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(WINDOWPOS));
                wp.hwndInsertAfter = HWND_BOTTOM;
                wp.flags &= ~SWP_NOZORDER;
                Marshal.StructureToPtr(wp, m.LParam, false);
            }
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!topMostMode) PinToDesktop();
        }

        void PinToDesktop() { SetOwner(FindWindow("Progman", null)); }

        void SetOwner(IntPtr owner)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr(Handle, GWL_HWNDPARENT, owner);
            else SetWindowLong(Handle, GWL_HWNDPARENT, owner);
        }

        void SetTopMost(bool on)
        {
            topMostMode = on;
            miTopMost.Checked = on;
            if (!IsHandleCreated) return;
            if (on) { SetOwner(IntPtr.Zero); TopMost = true; }
            else { TopMost = false; PinToDesktop(); }
        }

        // ===================== Avvisi =====================
        DateTime cpuHighSince = DateTime.MaxValue, ramHighSince = DateTime.MaxValue, tempHighSince = DateTime.MaxValue;
        readonly Dictionary<string, DateTime> lastAlert = new Dictionary<string, DateTime>();
        DateTime lastSpaceCheck = DateTime.MinValue;

        void CheckAlerts()
        {
            if (!alerts) return;
            var s = SysMonitor.Instance.Current;
            var hist = SysMonitor.Instance.History();
            if (hist.Count < 20) return;
            var now = DateTime.Now;
            double cpu30 = hist.Skip(Math.Max(0, hist.Count - 30)).Average(h => h.Cpu);
            cpuHighSince = cpu30 >= 90 ? (cpuHighSince == DateTime.MaxValue ? now : cpuHighSince) : DateTime.MaxValue;
            ramHighSince = s.RamPct >= 92 ? (ramHighSince == DateTime.MaxValue ? now : ramHighSince) : DateTime.MaxValue;
            tempHighSince = s.TempC >= 90 ? (tempHighSince == DateTime.MaxValue ? now : tempHighSince) : DateTime.MaxValue;
            var topCpu = s.Procs.OrderByDescending(p => p.Cpu).FirstOrDefault();
            var topMem = s.Procs.OrderByDescending(p => p.Private).FirstOrDefault();

            if (now - cpuHighSince > TimeSpan.FromMinutes(2))
                Alert("cpu", L.T("Processore al massimo", "Processor maxed out"),
                      L.F("La CPU è sopra il 90% da più di 2 minuti{0}.", "CPU has been above 90% for over 2 minutes{0}.", topCpu != null ? " (" + topCpu.Name + " " + topCpu.Cpu.ToString("0") + "%)" : ""));
            if (now - ramHighSince > TimeSpan.FromMinutes(1))
                Alert("ram", L.T("Memoria quasi esaurita", "Memory almost full"),
                      L.F("RAM al {0}{1}: chiudi i programmi che non usi.", "RAM at {0}{1}: close programs you don't use.", Fmt.Pct(s.RamPct), topMem != null ? " (" + topMem.Name + " " + Fmt.Bytes(topMem.Private) + ")" : ""));
            if (now - tempHighSince > TimeSpan.FromSeconds(30))
                Alert("temp", L.T("PC molto caldo", "PC very hot"), L.F("Temperatura {0:0} °C.", "Temperature {0:0} °C.", s.TempC));
            if (s.Battery >= 0 && s.Battery <= 10 && !s.OnAc)
                Alert("battery", L.T("Batteria quasi scarica", "Battery almost empty"), L.F("Batteria al {0}%: collega l'alimentatore.", "Battery at {0}%: plug in the charger.", s.Battery));
            if (now - lastSpaceCheck > TimeSpan.FromMinutes(5))
            {
                lastSpaceCheck = now;
                foreach (var v in s.Volumes)
                    if (v.Total > 0 && (100.0 * v.Free / v.Total < 5 || v.Free < (2L << 30)))
                        Alert("space" + v.Name, L.F("Disco {0} quasi pieno", "Drive {0} almost full", v.Name), L.F("Restano {0} liberi.", "{0} left.", Fmt.Bytes(v.Free)), 6 * 60);
            }
        }

        void Alert(string kind, string title, string text, int cooldownMin = 30)
        {
            DateTime last;
            if (lastAlert.TryGetValue(kind, out last) && DateTime.Now - last < TimeSpan.FromMinutes(cooldownMin)) return;
            lastAlert[kind] = DateTime.Now;
            balloonIsUpdate = false;
            tray.ShowBalloonTip(8000, title, text + "\n" + L.T("Clicca per l'analisi approfondita.", "Click for the in-depth analysis."), ToolTipIcon.Warning);
        }

        // ===================== Tray =====================
        Icon cpuIcon;

        void UpdateTray()
        {
            var s = SysMonitor.Instance.Current;
            string vol = s.Volumes.Count > 0 ? " · " + s.Volumes[0].Name + " " + Fmt.Bytes(s.Volumes[0].Free) : "";
            string tip = "CorbolSys · CPU " + Fmt.Pct(s.Cpu) + " · RAM " + Fmt.Pct(s.RamPct) + vol;
            try { tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip; } catch { }
            if (!trayCpu) { if (tray.Icon != appIcon) { tray.Icon = appIcon; DisposeCpuIcon(); } return; }
            var old = cpuIcon;
            cpuIcon = DrawCpuIcon(SysMonitor.Instance.History(), Pal());
            tray.Icon = cpuIcon;
            if (old != null) { DestroyIcon(old.Handle); old.Dispose(); }
        }

        void DisposeCpuIcon()
        {
            if (cpuIcon == null) return;
            DestroyIcon(cpuIcon.Handle);
            cpuIcon.Dispose();
            cpuIcon = null;
        }

        // Icona della tray: barre della CPU degli ultimi secondi, colore secondo il carico
        static Icon DrawCpuIcon(List<SysSnapshot> hist, Palette p)
        {
            int size = SystemInformation.SmallIconSize.Width;
            using (var bmp = new Bitmap(size, size))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(Color.FromArgb(235, 22, 26, 34))) g.FillRectangle(b, 0, 0, size, size);
                int bars = Math.Max(4, size / 3), bw = Math.Max(2, size / bars);
                var last = hist.Skip(Math.Max(0, hist.Count - bars)).ToList();
                for (int i = 0; i < last.Count; i++)
                {
                    double v = last[i].Cpu;
                    int h = Math.Max(1, (int)Math.Round(v / 100 * (size - 2)));
                    Color c = v >= 90 ? Color.FromArgb(240, 90, 90) : v >= 70 ? Color.FromArgb(245, 190, 60) : Color.FromArgb(80, 210, 140);
                    using (var b = new SolidBrush(c)) g.FillRectangle(b, size - (last.Count - i) * bw, size - 1 - h, bw - (bw > 2 ? 1 : 0), h);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        public static Stream IconStream()
        {
            return typeof(WidgetForm).Assembly.GetManifestResourceStream("CorbolSys.corbolsys.ico");
        }

        static Icon LoadAppIcon()
        {
            try { using (var st = IconStream()) if (st != null) return new Icon(st, SystemInformation.SmallIconSize); } catch { }
            return SystemIcons.Application;
        }

        // ===================== Disegno =====================
        const int Pad = 14, RowH = 18, GraphH = 34;   // come CorbolIp

        Palette Pal()
        {
            bool light = theme == "light" || (theme == "auto" && SystemIsLight());
            if (light)
                return new Palette
                {
                    Back = Color.FromArgb(248, 249, 251), Border = Color.FromArgb(205, 210, 220), Label = Color.FromArgb(105, 112, 125),
                    Value = Color.FromArgb(25, 28, 35), Accent = Color.FromArgb(30, 110, 220), Ram = Color.FromArgb(140, 70, 210),
                    Gpu = Color.FromArgb(220, 120, 20), Good = Color.FromArgb(25, 150, 70), Warn = Color.FromArgb(190, 130, 0),
                    Bad = Color.FromArgb(200, 50, 50), Track = Color.FromArgb(225, 229, 236)
                };
            return new Palette
            {
                Back = Color.FromArgb(24, 26, 32), Border = Color.FromArgb(60, 66, 80), Label = Color.FromArgb(140, 150, 165),
                Value = Color.FromArgb(235, 238, 242), Accent = Color.FromArgb(90, 170, 255), Ram = Color.FromArgb(190, 140, 255),
                Gpu = Color.FromArgb(255, 170, 70), Good = Color.FromArgb(80, 210, 120), Warn = Color.FromArgb(240, 190, 60),
                Bad = Color.FromArgb(240, 100, 100), Track = Color.FromArgb(48, 53, 64)
            };
        }

        static bool SystemIsLight()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k != null ? k.GetValue("AppsUseLightTheme") : null;
                    return v is int && (int)v == 1;
                }
            }
            catch { return false; }
        }

        void ApplyLook()
        {
            BackColor = Pal().Back;
            Opacity = opacity / 100.0;
            foreach (var m in miTheme) m.Checked = (string)m.Tag == theme;
            foreach (var m in miOpacity) m.Checked = (int)m.Tag == opacity;
            miCompact.Checked = compact;
            ResizeToContent();
            Invalidate();
        }

        void ResizeToContent()
        {
            int w = compact ? 270 : 330;   // come CorbolIp
            int h = Render(null);
            if (Width != w || Height != h) Size = new Size(w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Render(e.Graphics);
        }

        int TextW(string s, Font f) { return TextRenderer.MeasureText(s, f, Size.Empty, TextFormatFlags.NoPadding).Width; }

        int DrawT(Graphics g, string s, Font f, int x, int y, Color c)
        {
            if (g != null) TextRenderer.DrawText(g, s, f, new Point(x, y), c, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            return TextW(s, f);
        }

        void DrawRight(Graphics g, string s, Font f, int right, int y, Color c)
        {
            if (g != null) DrawT(g, s, f, right - TextW(s, f), y, c);
        }

        static Color Level(Palette p, double v, Color normal)
        {
            return v >= 92 ? p.Bad : v >= 80 ? p.Warn : normal;
        }

        // Riga "ETICHETTA [barra] valore"
        void BarRow(Graphics g, Palette p, ref int y, string label, double pct, Color c, string value, string sub = null)
        {
            if (g != null)
            {
                int valW = compact ? 44 : 50;
                int x0 = Pad + LabelW, right = Width - Pad;
                DrawT(g, label, fLabel, Pad, y + 2, p.Label);
                int barW = right - x0 - valW - 6;
                if (sub != null && !compact) barW -= TextW(sub, fSmall) + 6;
                var r = new Rectangle(x0, y + 6, Math.Max(10, barW), 7);
                using (var b = new SolidBrush(p.Track)) g.FillRectangle(b, r);
                int fw = (int)Math.Round(r.Width * Math.Max(0, Math.Min(100, pct)) / 100);
                if (fw > 0) using (var b = new SolidBrush(Level(p, pct, c))) g.FillRectangle(b, r.X, r.Y, fw, r.Height);
                if (sub != null && !compact) DrawT(g, sub, fSmall, r.Right + 6, y + 3, p.Label);
                DrawRight(g, value, fValue, right, y + 1, Level(p, pct, p.Value));
            }
            y += RowH;
        }

        // Righe dei dischi nel widget (per il clic che apre lo spazio su disco)
        readonly List<KeyValuePair<Rectangle, string>> volumeHits = new List<KeyValuePair<Rectangle, string>>();
        bool pressing;
        Point downPt;

        string VolumeAt(Point pt)
        {
            foreach (var h in volumeHits) if (h.Key.Contains(pt)) return h.Value;
            return null;
        }

        int LabelW { get { return Math.Max(40, TextW(L.T("DISCO", "DISK"), fLabel) + 10); } }

        int Render(Graphics g)
        {
            var p = Pal();
            var s = SysMonitor.Instance.Current;
            int W = Width, y = Pad;
            volumeHits.Clear();
            if (g != null)
            {
                using (var pen = new Pen(p.Border)) g.DrawRectangle(pen, 0, 0, W - 1, Height - 1);
                using (var b = new SolidBrush(p.Accent)) g.FillRectangle(b, 0, 0, 3, Height);
            }

            // Titolo: nome del PC e da quanto è acceso
            DrawT(g, Environment.MachineName, fTitle, Pad, y, p.Value);
            DrawRight(g, "⏱ " + Fmt.Duration(s.Uptime), fSmall, W - Pad, y + 7, p.Label);
            y += 26;
            if (!compact && !string.IsNullOrEmpty(SysMonitor.CpuName))
            {
                if (g != null) TextRenderer.DrawText(g, ShortCpu(SysMonitor.CpuName) + " · " + Fmt.Bytes(s.RamTotal) + " RAM", fLabel, new Rectangle(Pad, y, W - 2 * Pad, 16), p.Label, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                y += 16;
            }
            y += 4;

            BarRow(g, p, ref y, "CPU", s.Cpu, p.Accent, Fmt.Pct(s.Cpu));
            BarRow(g, p, ref y, "RAM", s.RamPct, p.Ram, Fmt.Pct(s.RamPct), Fmt.Bytes(s.RamTotal - s.RamAvail) + " / " + Fmt.Bytes(s.RamTotal));
            if (showGpu && s.Gpu >= 0) BarRow(g, p, ref y, "GPU", s.Gpu, p.Gpu, Fmt.Pct(s.Gpu));

            if (!compact)
            {
                y += 3;
                if (g != null) DrawGraph(g, p, new Rectangle(Pad, y, W - 2 * Pad, GraphH));
                y += GraphH + 4;
            }

            if (showDisks)
            {
                if (g != null && s.DiskBusy >= 0)
                {
                    DrawT(g, L.T("DISCO", "DISK"), fLabel, Pad, y + 2, p.Label);
                    int x = Pad + LabelW;
                    x += DrawT(g, "R " + Fmt.Rate(s.DiskRead), fValue, x, y + 1, p.Accent) + 10;
                    DrawT(g, "W " + Fmt.Rate(s.DiskWrite), fValue, x, y + 1, p.Gpu);
                    DrawRight(g, Fmt.Pct(s.DiskBusy), fValue, W - Pad, y + 1, Level(p, s.DiskBusy, p.Value));
                }
                if (s.DiskBusy >= 0) y += RowH;
                foreach (var v in s.Volumes)
                {
                    volumeHits.Add(new KeyValuePair<Rectangle, string>(new Rectangle(0, y, W, RowH), v.Name));
                    BarRow(g, p, ref y, v.Name, v.UsedPct, p.Accent, Fmt.Pct(v.UsedPct), L.F("{0} liberi", "{0} free", Fmt.Bytes(v.Free)));
                }
            }

            if (showNet)
            {
                if (g != null)
                {
                    DrawT(g, L.T("RETE", "NET"), fLabel, Pad, y + 2, p.Label);
                    int x = Pad + LabelW;
                    x += DrawT(g, "↓ " + Fmt.Rate(s.NetRx), fValue, x, y + 1, p.Accent) + 12;
                    DrawT(g, "↑ " + Fmt.Rate(s.NetTx), fValue, x, y + 1, p.Good);
                }
                y += RowH;
            }

            if (showTemp && (s.TempC > 0 || s.Battery >= 0))
            {
                if (g != null)
                {
                    int x = Pad;
                    if (s.TempC > 0)
                    {
                        x += DrawT(g, "TEMP ", fLabel, x, y + 2, p.Label);
                        x += DrawT(g, s.TempC.ToString("0") + " °C", fValue, x, y + 1, s.TempC >= 90 ? p.Bad : s.TempC >= 80 ? p.Warn : p.Value) + 16;
                    }
                    if (s.Battery >= 0)
                    {
                        x += DrawT(g, L.T("BATT ", "BATT "), fLabel, x, y + 2, p.Label);
                        DrawT(g, s.Battery + "%" + (s.Charging ? " ⚡" : s.OnAc ? " 🔌" : ""), fValue, x, y + 1, s.Battery <= 15 && !s.OnAc ? p.Bad : p.Value);
                    }
                }
                y += RowH;
            }

            if (showProcs && !compact)
            {
                y += 6;
                if (g != null)
                {
                    DrawT(g, L.T("PROCESSI", "PROCESSES"), fLabel, Pad, y, p.Accent);   // intestazione di sezione blu, come le schede in CorbolIp
                    DrawRight(g, "CPU      RAM", fSmall, W - Pad, y + 1, p.Label);
                }
                y += 17;
                foreach (var pr in s.Procs.OrderByDescending(x => x.Cpu).ThenByDescending(x => x.Private).Take(4))
                {
                    if (g != null)
                    {
                        string mem = Fmt.Bytes(pr.Private);
                        int memW = 64, cpuW = 42;
                        TextRenderer.DrawText(g, pr.Name + (pr.Count > 1 ? " ×" + pr.Count : ""), fLabel, new Rectangle(Pad, y + 1, W - 2 * Pad - memW - cpuW - 4, 16), p.Value, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        DrawRight(g, pr.Cpu.ToString("0.0") + "%", fValue, W - Pad - memW, y, Level(p, pr.Cpu, p.Value));
                        DrawRight(g, mem, fValue, W - Pad, y, p.Value);
                    }
                    y += 17;
                }
            }
            return y + Pad - 4;
        }

        static string ShortCpu(string n)
        {
            n = n.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "").Replace(" CPU", "").Replace("Processor", "");
            int at = n.IndexOf(" @", StringComparison.Ordinal);
            if (at > 0) n = n.Substring(0, at);
            return System.Text.RegularExpressions.Regex.Replace(n, @"\s+", " ").Trim();
        }

        void DrawGraph(Graphics g, Palette p, Rectangle r)
        {
            using (var b = new SolidBrush(Color.FromArgb(18, p.Label))) g.FillRectangle(b, r);
            var hist = SysMonitor.Instance.History();
            int n = hist.Count;
            if (n < 2) { DrawT(g, L.T("raccolta dati...", "collecting data..."), fSmall, r.X + 4, r.Y + 3, p.Label); return; }
            float step = (float)r.Width / (SysMonitor.HistLen - 1);
            Func<Func<SysSnapshot, double>, PointF[]> pts = f =>
            {
                var a = new PointF[n];
                for (int i = 0; i < n; i++)
                    a[i] = new PointF(r.Right - (n - 1 - i) * step, (float)(r.Bottom - 1 - Math.Max(0, Math.Min(100, f(hist[i]))) / 100 * (r.Height - 3)));
                return a;
            };
            var cpu = pts(h => h.Cpu);
            var poly = new List<PointF>(cpu) { new PointF(cpu[n - 1].X, r.Bottom), new PointF(cpu[0].X, r.Bottom) };
            using (var b = new SolidBrush(Color.FromArgb(55, p.Accent))) g.FillPolygon(b, poly.ToArray());
            using (var pen = new Pen(p.Accent, 1.5f)) g.DrawLines(pen, cpu);
            using (var pen = new Pen(p.Ram, 1.2f)) g.DrawLines(pen, pts(h => h.RamPct));
            if (showGpu && hist[n - 1].Gpu >= 0) using (var pen = new Pen(p.Gpu, 1.1f) { DashStyle = DashStyle.Dot }) g.DrawLines(pen, pts(h => Math.Max(0, h.Gpu)));
            int x = r.X + 4;
            x += DrawT(g, "CPU", fSmall, x, r.Y + 2, p.Accent) + 6;
            x += DrawT(g, "RAM", fSmall, x, r.Y + 2, p.Ram) + 6;
            if (showGpu && hist[n - 1].Gpu >= 0) DrawT(g, "GPU", fSmall, x, r.Y + 2, p.Gpu);
            DrawRight(g, "60 s", fSmall, r.Right - 4, r.Y + 2, p.Label);
        }

        string StatusText()
        {
            var s = SysMonitor.Instance.Current;
            var sb = new StringBuilder();
            sb.AppendLine(Environment.MachineName + " - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine(SysMonitor.CpuName);
            sb.AppendLine("CPU " + Fmt.Pct(s.Cpu) + " · RAM " + Fmt.Pct(s.RamPct) + " (" + Fmt.Bytes(s.RamTotal - s.RamAvail) + " / " + Fmt.Bytes(s.RamTotal) + ")" + (s.Gpu >= 0 ? " · GPU " + Fmt.Pct(s.Gpu) : ""));
            foreach (var v in s.Volumes) sb.AppendLine(v.Name + " " + L.F("{0} liberi di {1}", "{0} free of {1}", Fmt.Bytes(v.Free), Fmt.Bytes(v.Total)));
            if (s.TempC > 0) sb.AppendLine("Temp " + s.TempC.ToString("0") + " °C");
            if (s.Battery >= 0) sb.AppendLine(L.T("Batteria", "Battery") + " " + s.Battery + "%");
            sb.AppendLine(L.T("Acceso da", "Up for") + " " + Fmt.Duration(s.Uptime));
            return sb.ToString();
        }

        // ===================== Aggiornamenti =====================
        // Legge l'ultima release da GitHub; propone il download solo dell'installer pubblicato nel repository,
        // verificato con lo SHA-256 scritto nelle note della release ("SHA-256: ...", vedi build-installer.bat).
        void CheckUpdates(bool manual)
        {
            if (Interlocked.Exchange(ref checkingUpd, 1) == 1) return;
            lastUpdCheck = DateTime.Now;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string tag = null, url = null, sha = null;
                try
                {
                    try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                    var req = (HttpWebRequest)WebRequest.Create(AppVersion.RepoUrl.Replace("https://github.com/", "https://api.github.com/repos/") + "/releases/latest");
                    req.Timeout = 10000;
                    req.UserAgent = "CorbolSys/" + AppVersion.Text;
                    req.Accept = "application/vnd.github+json";
                    string json;
                    using (var resp = req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream())) json = sr.ReadToEnd();
                    var m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9]+(\\.[0-9]+){1,3})\"");
                    if (m.Success) tag = m.Groups[1].Value;
                    m = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"(" + Regex.Escape(AppVersion.RepoUrl + "/releases/download/") + "[^\"]+\\.exe)\"");
                    if (m.Success) url = m.Groups[1].Value;
                    m = Regex.Match(json, "SHA-256:\\s*`?([0-9a-fA-F]{64})");
                    if (m.Success) sha = m.Groups[1].Value.ToLowerInvariant();
                }
                catch { }
                checkingUpd = 0;
                try { BeginInvoke(new Action(delegate { OnUpdateResult(tag, url, sha, manual); })); } catch { }
            });
        }

        void OnUpdateResult(string tag, string url, string sha, bool manual)
        {
            Version latest;
            if (tag == null || !Version.TryParse(tag, out latest))
            {
                if (manual) Msg(L.T("Impossibile controllare gli aggiornamenti. Verifica la connessione a Internet e riprova.",
                                    "Could not check for updates. Check your Internet connection and try again."), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (latest > new Version(AppVersion.Text))
            {
                bool isNew = updVersion != tag;
                updVersion = tag; updUrl = url; updSha = sha;
                miUpdateAvail.Text = L.F("Aggiorna a CorbolSys {0}...", "Update to CorbolSys {0}...", tag);
                miUpdateAvail.Visible = true;
                if (manual) OfferUpdate();
                else if (isNew)
                {
                    tray.ShowBalloonTip(10000, L.F("CorbolSys {0} disponibile", "CorbolSys {0} available", tag),
                        L.T("Clicca qui per aggiornare.", "Click here to update."), ToolTipIcon.Info);
                    balloonIsUpdate = true;
                }
            }
            else if (manual)
                Msg(L.F("Hai già l'ultima versione di CorbolSys ({0}).", "You already have the latest version of CorbolSys ({0}).", AppVersion.Text),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void OfferUpdate()
        {
            if (updVersion == null) return;
            string page = AppVersion.RepoUrl + "/releases/tag/v" + updVersion;
            if (updUrl == null) { Launch(page, ""); return; }
            var r = Msg(L.F("È disponibile CorbolSys {0} (installata: {1}).\n\nScaricare e installare ora? L'installer chiuderà CorbolSys durante l'aggiornamento; impostazioni e cronologia restano.\n\nNovità: {2}",
                            "CorbolSys {0} is available (installed: {1}).\n\nDownload and install it now? The installer will close CorbolSys during the update; your settings and history are kept.\n\nWhat's new: {2}",
                            updVersion, AppVersion.Text, page), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes) DownloadUpdate(updUrl, updSha, updVersion, page);
        }

        void DownloadUpdate(string url, string sha, string ver, string page)
        {
            balloonIsUpdate = false;
            tray.ShowBalloonTip(3000, "CorbolSys", L.F("Download di CorbolSys {0} in corso...", "Downloading CorbolSys {0}...", ver), ToolTipIcon.Info);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string file = Path.Combine(Path.GetTempPath(), "CorbolSys-Setup-" + ver + ".exe");
                string err = null;
                try
                {
                    try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                    using (var wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.UserAgent] = "CorbolSys/" + AppVersion.Text;
                        wc.DownloadFile(url, file);
                    }
                    if (sha != null)
                    {
                        // Confronta con lo SHA-256 scritto nelle note della release
                        string got;
                        using (var st = File.OpenRead(file))
                        using (var h = SHA256.Create())
                            got = BitConverter.ToString(h.ComputeHash(st)).Replace("-", "").ToLowerInvariant();
                        if (got != sha)
                        {
                            File.Delete(file);
                            err = L.T("il file scaricato non corrisponde a quello pubblicato (SHA-256 diverso).", "the downloaded file does not match the published one (SHA-256 mismatch).");
                        }
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (err == null)
                        {
                            try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); return; }
                            catch (Exception ex) { err = ex.Message; }
                        }
                        if (Msg(L.F("Aggiornamento non riuscito: {0}\n\nAprire la pagina di download?", "Update failed: {0}\n\nOpen the download page?", err),
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                            Launch(page, "");
                    }));
                }
                catch { }
            });
        }

        // MessageBox visibile sopra le altre finestre (il widget sta sotto, sul desktop)
        static DialogResult Msg(string text, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            using (var owner = new Form { TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None,
                                          StartPosition = FormStartPosition.CenterScreen, Size = new Size(1, 1), Opacity = 0 })
            {
                owner.Show();
                owner.Activate();
                return MessageBox.Show(owner, text, "CorbolSys", buttons, icon);
            }
        }

        // ===================== Impostazioni =====================
        void ToggleWidget()
        {
            if (Visible) Hide(); else { Show(); if (!topMostMode) PinToDesktop(); }
            miShow.Checked = Visible;
            SaveSettings();
        }

        bool IsStartupEnabled()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(AppName) != null; } catch { return false; }
        }

        void ToggleStartup()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (IsStartupEnabled()) k.DeleteValue(AppName, false);
                    else k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                }
            }
            catch { }
            miStartup.Checked = IsStartupEnabled();
        }

        void LoadSettings()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 20, wa.Top + 20);
            bool visible = true;
            try
            {
                if (File.Exists(settingsPath))
                {
                    var d = new Dictionary<string, string>();
                    foreach (var ln in File.ReadAllLines(settingsPath))
                    {
                        int eq = ln.IndexOf('=');
                        if (eq > 0) d[ln.Substring(0, eq).Trim()] = ln.Substring(eq + 1).Trim();
                    }
                    string v;
                    int x, y;
                    if (d.TryGetValue("x", out v) && int.TryParse(v, out x) && d.TryGetValue("y", out v) && int.TryParse(v, out y))
                    {
                        var pt = new Point(x, y);
                        if (SystemInformation.VirtualScreen.Contains(pt)) Location = pt;
                    }
                    Func<string, bool, bool> b = (k, def) => d.TryGetValue(k, out v) ? v == "1" : def;
                    topMostMode = b("topmost", false);
                    visible = b("visible", true);
                    compact = b("compact", false);
                    showGpu = b("show.gpu", true);
                    showDisks = b("show.disks", true);
                    showNet = b("show.net", true);
                    showTemp = b("show.temp", true);
                    showProcs = b("show.procs", true);
                    trayCpu = b("tray.cpu", true);
                    alerts = b("alerts", true);
                    autoUpdate = b("updates", false);
                    if (SnapshotPath == null) SysMonitor.HistoryEnabled = b("history", true);
                    if (d.TryGetValue("theme", out v) && (v == "auto" || v == "dark" || v == "light")) theme = v;
                    int op;
                    if (d.TryGetValue("opacity", out v) && int.TryParse(v, out op) && op >= 30 && op <= 100) opacity = op;
                    foreach (var kv in d) if (kv.Key.StartsWith("ai.")) Settings.Values[kv.Key] = kv.Value;
                }
            }
            catch { }
            if (!Settings.Values.Keys.Any(k => k.StartsWith("ai.")) && SnapshotPath == null) ImportCorbolIpAi();
            miTopMost.Checked = topMostMode;
            TopMost = topMostMode;
            if (!visible)
            {
                miShow.Checked = false;
                Load += delegate { BeginInvoke(new Action(Hide)); };
            }
        }

        public static string SnapshotWindow, SnapshotArg;   // /snapshot <file> space [cartella] | cleanup
        public static bool SnapshotAi;   // /snapshot <file> ai: analisi + "Spiega" (con l'assistente configurato)
        bool aiStarted;

        void StartSnapshot()
        {
            var t = new WinTimer { Interval = 7000 };
            t.Tick += delegate
            {
                if (SnapshotWindow == "space")
                {
                    var f = DiskSpaceForm.Current;
                    if (f == null) { DiskSpaceForm.ShowOnce(appIcon, SnapshotArg); t.Interval = 1000; return; }
                    if (f.Busy) return;
                    Save(f);
                }
                else if (SnapshotWindow == "cleanup")
                {
                    var f = CleanupForm.Current;
                    if (f == null) { CleanupForm.ShowOnce(appIcon); t.Interval = 1000; return; }
                    if (f.Busy) return;
                    f.SelectFirst();
                    Save(f);
                }
                else if (SnapshotAnalysis)
                {
                    var f = AnalysisForm.Current;
                    if (f == null) { AnalysisForm.ShowOnce(appIcon); t.Interval = 1000; return; }
                    if (f.Busy) return;
                    if (SnapshotAi && !aiStarted) { aiStarted = true; f.AutoExplain(); return; }
                    Save(f);
                    if (SnapshotAi) try { File.WriteAllText(Path.ChangeExtension(SnapshotPath, ".txt"), f.AiText, new UTF8Encoding(true)); } catch { }
                }
                else Save(this);
                t.Stop();
                Close();
            };
            t.Start();
        }

        static void Save(Control c)
        {
            try
            {
                using (var bmp = new Bitmap(c.Width, c.Height))
                {
                    c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height));
                    bmp.Save(SnapshotPath, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch { }
        }

        // Primo avvio: se CorbolIp ha già l'assistente configurato, se ne riprendono le impostazioni.
        // Le chiavi API si decifrano con l'entropia di CorbolIp e si cifrano di nuovo per CorbolSys (sempre DPAPI, stesso utente).
        static void ImportCorbolIpAi()
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CorbolIp", "settings.txt");
                // CorbolIp non ancora aggiornato: impostazioni col nome vecchio
                if (!File.Exists(path)) path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppIP", "settings.txt");
                if (!File.Exists(path)) return;
                var corbolIpEntropy = Encoding.UTF8.GetBytes("AppIP.ai.v1");
                foreach (var ln in File.ReadAllLines(path))
                {
                    int eq = ln.IndexOf('=');
                    if (eq <= 0 || !ln.StartsWith("ai.")) continue;
                    string k = ln.Substring(0, eq).Trim(), v = ln.Substring(eq + 1).Trim();
                    if (k == "ai.local.crashes") continue;
                    if (k.EndsWith(".key")) v = Secret.Protect(Secret.Unprotect(v, corbolIpEntropy));
                    Settings.Values[k] = v;
                }
            }
            catch { }
        }

        void SaveSettings()
        {
            if (SnapshotPath != null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                var sb = new StringBuilder();
                Action<string, object> w = (k, v) => sb.Append(k).Append('=').Append(v is bool ? ((bool)v ? "1" : "0") : Convert.ToString(v)).Append('\n');
                w("x", Left); w("y", Top);
                w("topmost", topMostMode); w("visible", Visible); w("compact", compact);
                w("theme", theme); w("opacity", opacity); w("lang", L.Setting);
                w("show.gpu", showGpu); w("show.disks", showDisks); w("show.net", showNet); w("show.temp", showTemp); w("show.procs", showProcs);
                w("tray.cpu", trayCpu); w("alerts", alerts); w("updates", autoUpdate); w("history", SysMonitor.HistoryEnabled);
                foreach (var kv in Settings.Values.OrderBy(k => k.Key)) w(kv.Key, kv.Value);
                File.WriteAllText(settingsPath, sb.ToString());
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettings();
            SystemEvents.UserPreferenceChanged -= OnUserPrefChanged;
            if (SnapshotPath == null) ClearJumpList();
            SysMonitor.Instance.Stop();
            LocalAi.Instance.Stop();
            tray.Visible = false;
            tray.Dispose();
            DisposeCpuIcon();
            base.OnFormClosing(e);
        }
    }
}
