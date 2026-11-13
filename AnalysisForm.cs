// Finestra "Analisi approfondita": i controlli con il loro esito, i dettagli del controllo selezionato,
// la conclusione con il punteggio e, se attivo, l'assistente AI: "Spiega" commenta i risultati e la casella
// "Chiedi" permette di fare domande sul PC (la conversazione continua). Con Claude e i servizi compatibili
// OpenAI l'assistente può chiedere da sé dei controlli in sola lettura (vedi SysTools.cs).
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace CorbolSys
{
    class AnalysisForm : Form
    {
        static AnalysisForm open;

        public static void ShowOnce(Icon icon)
        {
            // se l'assistente è stato acceso o spento la finestra va ricostruita
            if (open != null && !open.IsDisposed && open.ai != AiSettings.Enabled && open.worker == null && open.aiCts == null) open.Close();
            if (open == null || open.IsDisposed) open = new AnalysisForm(icon);
            open.Show();
            if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal;
            open.Activate();
        }

        public static AnalysisForm Current { get { return open != null && !open.IsDisposed ? open : null; } }
        public bool Busy { get { return worker != null || aiCts != null; } }
        public string AiText { get { return txtAi != null ? txtAi.Text : ""; } }
        public void AutoExplain() { if (ai && an != null && worker == null) RunAi(null); }

        readonly ListView list;
        readonly ImageList icons;
        readonly TextBox txtDetail;
        readonly Label lblTitle, lblText;
        readonly Button btnStart, btnCopy, btnSave;
        Analysis an;
        Thread worker;
        volatile bool cancel;

        // assistente AI
        readonly bool ai;
        readonly Button btnExplain, btnAsk;
        readonly TextBox txtAi, txtQuestion;
        CancellationTokenSource aiCts;
        readonly List<AiMessage> convo = new List<AiMessage>();   // conversazione (già anonimizzata)
        readonly StringBuilder transcript = new StringBuilder();  // quello che si vede nel riquadro (in chiaro)
        Anonymizer anon;
        string lastSent;

        static readonly Color OkColor = Color.FromArgb(30, 130, 60), WarnColor = Color.FromArgb(190, 110, 0), ErrColor = Color.FromArgb(190, 40, 40);

        AnalysisForm(Icon icon)
        {
            Text = L.T("Analisi approfondita - CorbolSys", "In-depth analysis - CorbolSys");
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", UiFont.Pt);
            BackColor = Color.White;
            if (icon != null) Icon = icon;
            ai = AiSettings.Enabled;

            float s = UiFont.Scale;
            int iconSize = (int)Math.Round(16 * s * ScreenDpi() / 96f);
            icons = new ImageList { ImageSize = new Size(iconSize, iconSize), ColorDepth = ColorDepth.Depth32Bit };
            foreach (CheckState st in System.Enum.GetValues(typeof(CheckState))) icons.Images.Add(st.ToString(), StateIcon(st, iconSize));

            // ---- parte sinistra: controlli, dettagli, conclusione ----
            var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 6, 6) };
            list = new ListView
            {
                View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, MultiSelect = false,
                ShowItemToolTips = true, SmallImageList = icons, Dock = DockStyle.Fill, HideSelection = false
            };
            list.Columns.Add(L.T("Controllo", "Check"));
            list.Columns.Add(L.T("Risultato", "Result"));
            list.SelectedIndexChanged += delegate { ShowDetail(); };
            list.Resize += delegate { FitColumns(); };
            txtDetail = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(248, 249, 251),
                Dock = DockStyle.Bottom, Height = 130, BorderStyle = BorderStyle.FixedSingle
            };
            var concl = new Panel { Dock = DockStyle.Bottom, Height = 104, Padding = new Padding(0, 8, 0, 0) };
            lblTitle = new Label { Dock = DockStyle.Top, Height = 26, Font = new Font("Segoe UI Semibold", UiFont.Pt + 1f) };
            lblText = new Label { Dock = DockStyle.Fill };
            concl.Controls.Add(lblText);
            concl.Controls.Add(lblTitle);
            left.Controls.Add(list);
            left.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 6 });
            left.Controls.Add(txtDetail);
            left.Controls.Add(concl);

            // ---- barra in basso ----
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(10, 6, 10, 6) };
            btnStart = new Button { Text = L.T("Ripeti", "Run again"), Size = new Size(110, 30) };
            btnCopy = new Button { Text = L.T("Copia report", "Copy report"), Size = new Size(120, 30), Enabled = false };
            btnSave = new Button { Text = L.T("Salva report...", "Save report..."), Size = new Size(130, 30), Enabled = false };
            var btnClose = new Button { Text = L.T("Chiudi", "Close"), Size = new Size(100, 30) };
            btnStart.Click += delegate { if (worker != null) cancel = true; else if (aiCts == null) Start(); };
            btnCopy.Click += delegate { try { Clipboard.SetText(Crlf(CopyText())); Flash(L.T("Report copiato negli appunti.", "Report copied to the clipboard.")); } catch { } };
            btnSave.Click += delegate { SaveReport(); };
            btnClose.Click += delegate { Close(); };
            CancelButton = btnClose;
            var btnSpace = new Button { Text = L.T("Spazio su disco...", "Disk space..."), Size = new Size(150, 30) };
            var btnClean = new Button { Text = L.T("Pulizia disco...", "Disk cleanup..."), Size = new Size(140, 30) };
            btnSpace.Click += delegate { DiskSpaceForm.ShowOnce(Icon, null); };
            btnClean.Click += delegate { CleanupForm.ShowOnce(Icon); };
            bar.Controls.AddRange(new Control[] { btnStart, btnCopy, btnSave, btnSpace, btnClean, btnClose });

            if (ai)
            {
                // ---- parte destra: assistente ----
                var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 12, 12, 6) };
                var head = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
                btnExplain = new Button { Text = L.T("Spiega con l'AI", "Explain with AI"), Size = new Size(140, 30), Enabled = false };
                var btnNew = new Button { Text = L.T("Nuova conversazione", "New conversation"), Size = new Size(160, 30) };
                var lnkSent = new Button { Text = L.T("Cosa viene inviato...", "What is sent..."), AutoSize = true, MinimumSize = new Size(0, 30) };
                lnkSent.Click += delegate { ShowSent(); };
                btnExplain.Click += delegate { RunAi(null); };
                btnNew.Click += delegate { if (aiCts == null) ResetConversation(true); };
                head.Controls.AddRange(new Control[] { btnExplain, btnNew, lnkSent });
                txtAi = new TextBox
                {
                    Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, Dock = DockStyle.Fill,
                    ForeColor = Color.DimGray, Font = new Font("Segoe UI", UiFont.Pt)
                };
                var ask = new Panel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(0, 8, 0, 0) };
                txtQuestion = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
                btnAsk = new Button { Text = L.T("Chiedi", "Ask"), Dock = DockStyle.Right, Width = 90, Enabled = false };
                btnAsk.Click += delegate { Ask(); };
                txtQuestion.KeyDown += (o, e) =>
                {
                    if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Ask(); }   // Maiusc+Invio va a capo
                };
                ask.Controls.Add(txtQuestion);
                ask.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 6 });
                ask.Controls.Add(btnAsk);
                right.Controls.Add(txtAi);
                right.Controls.Add(ask);
                right.Controls.Add(head);

                var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.None, SplitterWidth = 6 };
                split.Panel1.Controls.Add(left);
                split.Panel2.Controls.Add(right);
                Controls.Add(split);
                Controls.Add(bar);
                ClientSize = new Size(1040, 640);
                Load += delegate { split.SplitterDistance = split.Width * 52 / 100; };
            }
            else
            {
                Controls.Add(left);
                Controls.Add(bar);
                ClientSize = new Size(600, 640);
            }
            MinimumSize = new Size(560, 480);
            Scale(new SizeF(s, s));   // dimensioni pensate per 9 pt

            Load += delegate
            {
                // non più grande dello schermo (DPI alti, schermi piccoli)
                var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                Size = new Size(Math.Min(Width, wa.Width * 95 / 100), Math.Min(Height, wa.Height * 95 / 100));
                Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
            };
            Shown += delegate { FitColumns(); Start(); };
        }

        static float ScreenDpi()
        {
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX; } catch { return 96f; }
        }

        void FitColumns()
        {
            int w = list.ClientSize.Width;
            if (w <= 0) return;
            list.Columns[0].Width = (int)(w * 0.36);
            list.Columns[1].Width = w - list.Columns[0].Width - 2;
        }

        static Bitmap StateIcon(CheckState st, int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color c;
                switch (st)
                {
                    case CheckState.Ok: c = Color.FromArgb(46, 160, 80); break;
                    case CheckState.Warn: c = Color.FromArgb(230, 145, 0); break;
                    case CheckState.Error: c = Color.FromArgb(210, 55, 55); break;
                    case CheckState.Info: c = Color.FromArgb(120, 140, 170); break;
                    case CheckState.Running: c = Color.FromArgb(60, 130, 220); break;
                    default: c = Color.FromArgb(205, 205, 205); break;
                }
                float m = size * 0.15f, d = size - 2 * m;
                if (st == CheckState.Running || st == CheckState.Wait)
                    using (var pen = new Pen(c, Math.Max(1.5f, size / 9f))) g.DrawEllipse(pen, m, m, d, d);
                else
                    using (var br = new SolidBrush(c)) g.FillEllipse(br, m, m, d, d);
            }
            return bmp;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            cancel = true;
            if (aiCts != null) aiCts.Cancel();
            base.OnFormClosing(e);
        }

        void Ui(Action a)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
        }

        void Flash(string text)
        {
            lblTitle.Text = text;
            lblTitle.ForeColor = Color.DimGray;
            var t = new System.Windows.Forms.Timer { Interval = 2500 };
            t.Tick += delegate { t.Dispose(); if (an != null && worker == null) ShowConclusion(); };
            t.Start();
        }

        // ---------------- analisi ----------------

        void Start()
        {
            an = new Analysis();
            cancel = false;
            list.Items.Clear();
            foreach (var c in an.Checks)
            {
                var it = new ListViewItem(new[] { c.Title, "" }) { ImageKey = CheckState.Wait.ToString(), Tag = c };
                c.Tag = it;
                list.Items.Add(it);
            }
            txtDetail.Text = L.T("Seleziona un controllo per vedere i dettagli e cosa fare.", "Select a check to see the details and what to do.");
            lblTitle.Text = L.T("Analisi in corso: richiede da 10 a 40 secondi...", "Analysis running: it takes 10 to 40 seconds...");
            lblTitle.ForeColor = Color.DimGray;
            lblText.Text = SysMonitor.Instance.Minutes(1).Count == 0
                ? L.T("CorbolSys è appena partito: CPU e memoria si basano sull'ultimo minuto. Lasciandolo acceso, l'analisi considera anche l'andamento delle ultime ore.",
                      "CorbolSys just started: CPU and memory are based on the last minute. If you leave it running, the analysis also considers the trend of the last hours.")
                : "";
            btnStart.Text = L.T("Interrompi", "Stop");
            btnCopy.Enabled = btnSave.Enabled = false;
            if (ai)
            {
                btnExplain.Enabled = btnAsk.Enabled = false;
                ResetConversation(false);
            }
            var a = an;
            a.Changed = c => Ui(delegate { ShowCheck(c); });
            a.Cancelled = () => cancel;
            worker = new Thread(delegate ()
            {
                try { a.Run(); } catch { }
                Ui(Finished);
            }) { IsBackground = true, Name = "CorbolSys analysis" };
            worker.Start();
        }

        void ShowCheck(SysCheck c)
        {
            var it = c.Tag as ListViewItem;
            if (it == null) return;
            it.ImageKey = c.State.ToString();
            it.SubItems[1].Text = c.Detail;
            it.ToolTipText = c.Title + ": " + c.Detail;
            if (it.Selected) ShowDetail();
        }

        void ShowDetail()
        {
            if (list.SelectedItems.Count == 0) return;
            var c = list.SelectedItems[0].Tag as SysCheck;
            if (c == null) return;
            var sb = new StringBuilder();
            sb.AppendLine(c.Title + ": " + c.Detail);
            if (c.Advice.Length > 0 && c.State >= CheckState.Warn) sb.AppendLine().AppendLine("→ " + c.Advice);
            if (c.Extra.Count > 0) { sb.AppendLine(); foreach (var x in c.Extra) sb.AppendLine("• " + x); }
            txtDetail.Text = Crlf(sb.ToString().TrimEnd());
        }

        void Finished()
        {
            worker = null;
            btnStart.Text = L.T("Ripeti", "Run again");
            if (cancel)
            {
                lblTitle.Text = L.T("Interrotto.", "Stopped.");
                lblTitle.ForeColor = Color.DimGray;
                return;
            }
            ShowConclusion();
            btnCopy.Enabled = btnSave.Enabled = true;
            if (ai)
            {
                btnExplain.Enabled = btnAsk.Enabled = true;
                ShowAi(L.T("Spiega con l'AI commenta i risultati e dice cosa fare. Puoi anche fare una domanda qui sotto, per esempio \"perché il PC è lento?\" o \"cosa occupa spazio sul disco C?\".",
                           "Explain with AI comments on the results and says what to do. You can also ask a question below, for example \"why is my PC slow?\" or \"what is filling drive C?\"."), true);
            }
            // seleziona il primo problema, così i dettagli si vedono subito
            var first = an.Checks.FirstOrDefault(c => c.State == CheckState.Error) ?? an.Checks.FirstOrDefault(c => c.State == CheckState.Warn);
            if (first != null && first.Tag is ListViewItem) { ((ListViewItem)first.Tag).Selected = true; ((ListViewItem)first.Tag).EnsureVisible(); }
        }

        void ShowConclusion()
        {
            lblTitle.Text = an.ConclusionTitle;
            lblTitle.ForeColor = an.Overall == CheckState.Ok ? OkColor : an.Overall == CheckState.Error ? ErrColor : WarnColor;
            lblText.Text = an.ConclusionText;
        }

        string CopyText()
        {
            string report = an != null ? an.ReportText() : "";
            string chat = transcript.ToString().Trim();
            if (chat.Length == 0) return report;
            return report + "\n" + L.T("Assistente AI", "AI assistant") + ":\n" + string.Join("\n", chat.Split('\n').Where(l => !l.StartsWith("» "))) + "\n";
        }

        void SaveReport()
        {
            using (var d = new SaveFileDialog
            {
                Filter = L.T("File di testo", "Text files") + " (*.txt)|*.txt",
                FileName = "CorbolSys-" + Environment.MachineName + "-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt"
            })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(d.FileName, Crlf(CopyText()), new UTF8Encoding(true)); Flash(L.T("Report salvato.", "Report saved.")); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        static string Crlf(string s) { return Regex.Replace(s ?? "", "\r?\n", "\r\n"); }

        // ---------------- assistente AI ----------------

        void ShowAi(string text, bool hint = false)
        {
            if (txtAi == null) return;
            var color = hint ? Color.DimGray : SystemColors.WindowText;
            if (txtAi.ForeColor != color) txtAi.ForeColor = color;
            string s = Crlf(text), cur = txtAi.Text;
            if (s == cur) return;
            // durante lo streaming il testo cresce: si aggiunge solo la parte nuova, senza ridisegnare tutto (niente sfarfallio)
            if (cur.Length > 0 && s.Length > cur.Length && s.StartsWith(cur, StringComparison.Ordinal))
            {
                txtAi.AppendText(s.Substring(cur.Length));
                return;
            }
            txtAi.Text = s;
            txtAi.SelectionStart = txtAi.TextLength;
            txtAi.ScrollToCaret();
        }

        void ResetConversation(bool showHint)
        {
            convo.Clear();
            transcript.Length = 0;
            lastSent = null;
            anon = null;
            if (showHint && an != null && worker == null)
                ShowAi(L.T("Nuova conversazione: premi Spiega con l'AI o scrivi una domanda.", "New conversation: press Explain with AI or type a question."), true);
        }

        static string LangName()
        {
            try { return CultureInfo.GetCultureInfo(L.Code).EnglishName; } catch { return "English"; }
        }

        const string Common =
            "Words in square brackets in the data (for example [PC] or [USER]) replace private data: copy them exactly when you need them " +
            "and never invent new ones. Rely only on the data provided and on the results of your checks; never invent values. If the data is " +
            "not enough to be sure, say so and suggest what to check. Recommend built-in Windows tools and settings (Task Manager, Settings, " +
            "Windows Security, Device Manager, Disk Cleanup) rather than third-party 'cleaner' or 'booster' programs; never suggest registry " +
            "hacks or turning off security features, and warn clearly before any step that deletes data. Write plain text for a small window: " +
            "no Markdown headings, bold or tables; use short paragraphs and lists with \"- \". Never say that you checked something unless its " +
            "result is in the data. In the analysis, [OK] and [INFO] mean there is no problem: never present them as problems and never call a " +
            "value high or full unless its check is [WARNING] or [ERROR]. Startup entries marked as disabled are already disabled. Do not guess " +
            "what an unknown program is: say you don't know it and how to find out (e.g. its publisher in Task Manager).";

        const string ToolsHelp =
            "You can run read-only checks with the tools (at most 8 per answer) to investigate further: top_processes for what uses CPU, " +
            "memory or disk right now, process_details to identify an unknown process, event_log for errors around a given time, " +
            "resource_history to see when the PC was busy and which program caused it, largest_folders when a disk is full, " +
            "installed_programs to relate problems to recent installs, stopped_services and startup_programs. Use them when they help find " +
            "or confirm the cause; do not run checks when the data already answers. Then write only the final answer for the user and " +
            "mention what the checks showed.";

        static string SystemPrompt(bool tools)
        {
            return "You are the PC health assistant built into CorbolSys, a system monitor for Windows. The first message contains the results " +
                   "of an automatic analysis of the user's PC: the checks with their status, details, and the resource usage trend recorded " +
                   "by CorbolSys. Help a non-technical user understand the state of their computer and fix problems, most important first, " +
                   "naming the specific programs or components involved. " + Common + (tools ? " " + ToolsHelp : "") + " Answer in " + LangName() + ".";
        }

        const string ExplainRequest = "Explain what these results mean for me and what I should do, most important first. If everything is fine, say so briefly. Keep it under about 250 words.";

        // Gli strumenti passano solo dalle API (Claude, compatibili OpenAI). L'assistente locale (modello piccolo)
        // nelle prove diceva di aver controllato senza farlo; Claude Code parte senza strumenti.
        static bool UsesTools(IAiProvider prov) { return prov is AiHttpProvider; }

        Anonymizer BuildAnon()
        {
            var a = new Anonymizer();
            a.Add(Environment.MachineName, "PC");
            if (an != null) a.Add(an.Domain, "DOMAIN");
            try { a.Add(Environment.UserName, "USER"); } catch { }
            try { a.Add(Environment.UserDomainName, "DOMAIN"); } catch { }
            return a;
        }

        void Ask()
        {
            if (aiCts != null || btnAsk == null || !btnAsk.Enabled) return;
            string q = txtQuestion.Text.Trim();
            if (q.Length == 0) { txtQuestion.Focus(); return; }
            txtQuestion.Text = "";
            RunAi(q);
        }

        // question == null: "Spiega"
        void RunAi(string question)
        {
            if (aiCts != null) { aiCts.Cancel(); return; }
            if (an == null || worker != null) return;
            var prov = AiSettings.CurrentProvider();
            if (prov == null)
            {
                ShowAi(L.T("L'assistente non è configurato: apri Assistente AI... dal menu di CorbolSys.", "The assistant is not set up: open AI assistant... from the CorbolSys menu."));
                return;
            }
            bool tools = UsesTools(prov);
            if (convo.Count == 0)
            {
                anon = AiSettings.Anonymize ? BuildAnon() : null;
                string data = an.ReportText(prov == LocalAi.Instance);   // modello piccolo: report compatto
                if (anon != null) data = anon.Apply(data);
                convo.Add(new AiMessage("user", "Analysis of my PC:\n\n" + data + "\n\n" + (question == null ? ExplainRequest : "My question: " + (anon != null ? anon.Apply(question) : question))));
            }
            else convo.Add(new AiMessage("user", question == null ? ExplainRequest : (anon != null ? anon.Apply(question) : question)));

            var req = new AiRequest { System = SystemPrompt(tools) };
            req.Messages.AddRange(convo);
            if (tools) { req.Tools = SysTools.Definitions(); req.MaxToolRounds = SysTools.MaxCalls; }
            lastSent = SentText(prov, req);

            if (transcript.Length > 0) transcript.Append("\n\n");
            transcript.Append(question == null ? "» " + L.T("Spiegazione dei risultati", "Explanation of the results") : L.T("Tu", "You") + ": " + question).Append("\n\n");

            var cts = aiCts = new CancellationTokenSource();
            string idle = question == null ? btnExplain.Text : btnAsk.Text;
            var btn = question == null ? btnExplain : btnAsk;
            btn.Text = L.T("Interrompi", "Stop");
            (btn == btnExplain ? btnAsk : btnExplain).Enabled = false;
            btnStart.Enabled = false;
            string head = transcript.ToString();
            ShowAi(head + (prov == LocalAi.Instance && !LocalAi.Instance.Running
                ? L.T("Avvio dell'assistente locale: la prima volta può richiedere fino a un minuto...", "Starting the local assistant: the first time it can take up to a minute...")
                : L.F("{0} sta scrivendo...", "{0} is writing...", prov.Name)));

            var acc = new StringBuilder();
            var a = anon;
            int pending = 0;   // al massimo un aggiornamento in coda: i pezzi arrivati nel frattempo si mostrano insieme
            Action refresh = delegate
            {
                if (Interlocked.Exchange(ref pending, 1) == 1) return;
                Ui(delegate
                {
                    Interlocked.Exchange(ref pending, 0);
                    string shown;
                    lock (acc) shown = acc.ToString();
                    if (a != null) shown = a.Restore(shown);
                    if (aiCts == cts) ShowAi(head + shown);
                });
            };
            int calls = 0;
            var sentLog = new StringBuilder();
            req.RunTool = delegate (AiToolCall c)
            {
                if (a != null && c.Args != null)
                    foreach (var k in c.Args.Keys.ToList())
                        if (c.Args[k] is string) c.Args[k] = a.Restore((string)c.Args[k]);
                string label = SysTools.Describe(c);
                if (++calls > SysTools.MaxCalls) return "Error: the limit of " + SysTools.MaxCalls + " checks is reached. Answer now with what you have.";
                lock (acc) acc.Append((acc.Length > 0 && acc[acc.Length - 1] != '\n' ? "\n" : "") + "» " + label + " ...\n");
                refresh();
                string outp;
                try { outp = SysTools.Execute(c, cts.Token); } catch (Exception ex) { outp = "Error: " + ex.Message; }
                lock (acc) { acc.Length -= 5; acc.Append(" ✓\n\n"); }
                refresh();
                string sentOut = a != null ? a.Apply(outp) : outp;
                lock (sentLog) sentLog.AppendLine().AppendLine("--- " + L.F("Controllo: {0}", "Check: {0}", label) + " ---").Append(sentOut);
                return sentOut;
            };
            req.OnRestart = delegate
            {
                lock (acc) { acc.Length = 0; acc.Append(L.T("(l'assistente locale si è chiuso: riparte e ricomincia la risposta...)", "(the local assistant closed: restarting the reply...)") + "\n"); }
                calls = 0;
                refresh();
            };
            var t = new Thread(delegate ()
            {
                var res = prov.Run(req, delegate (string chunk) { lock (acc) acc.Append(chunk); refresh(); }, cts.Token);
                string final;
                lock (acc) final = acc.ToString();
                Ui(delegate
                {
                    lock (sentLog) if (sentLog.Length > 0) lastSent += "\n" + sentLog;
                    aiCts = null;
                    btn.Text = idle;
                    btnExplain.Enabled = btnAsk.Enabled = btnStart.Enabled = true;
                    // la risposta (senza le righe dei controlli) entra nella conversazione per le domande successive
                    string answer = string.Join("\n", final.Split('\n').Where(l => !l.StartsWith("» "))).Trim();
                    string shown = a != null ? a.Restore(final.Trim()) : final.Trim();
                    if (res.Cancelled) shown += (shown.Length > 0 ? "\n\n" : "") + L.T("(interrotto)", "(stopped)");
                    else if (!res.Ok) shown = res.Error + (shown.Length > 0 ? "\n\n" + shown : "");
                    else if (res.Truncated) shown += "\n\n" + L.T("(risposta troncata)", "(reply cut short)");
                    if (res.Ok && answer.Length > 0) convo.Add(new AiMessage("assistant", answer));
                    else convo.RemoveAt(convo.Count - 1);   // domanda senza risposta: si può ripetere
                    if (convo.Count == 0) anon = null;
                    transcript.Append(shown);
                    ShowAi(transcript.ToString());
                    txtQuestion.Focus();
                });
            }) { IsBackground = true, Name = "CorbolSys AI" };
            t.Start();
        }

        static string SentText(IAiProvider prov, AiRequest req)
        {
            var sb = new StringBuilder();
            sb.AppendLine(L.F("Servizio: {0}", "Service: {0}", prov.Name) + (prov.SendsTextOutside ? "" : " " + L.T("(sul PC, i dati non escono)", "(on this PC, data does not leave it)")));
            sb.AppendLine(AiSettings.Anonymize ? L.T("Anonimizzazione: attiva", "Anonymization: on") : L.T("Anonimizzazione: spenta", "Anonymization: off"));
            sb.AppendLine(req.Tools != null ? L.F("Controlli aggiuntivi permessi all'assistente: {0} (sola lettura)", "Extra checks allowed to the assistant: {0} (read-only)", string.Join(", ", req.Tools.Select(x => x.Name)))
                                            : L.T("Controlli aggiuntivi: nessuno", "Extra checks: none"));
            sb.AppendLine();
            sb.AppendLine("--- " + L.T("Istruzioni", "Instructions") + " ---");
            sb.AppendLine(req.System);
            foreach (var m in req.Messages)
            {
                sb.AppendLine();
                sb.AppendLine("--- " + (m.Role == "user" ? L.T("Messaggio", "Message") : L.T("Risposta precedente", "Previous reply")) + " ---");
                sb.AppendLine(m.Text);
            }
            return sb.ToString();
        }

        void ShowSent()
        {
            string text = lastSent;
            if (text == null)
            {
                var prov = AiSettings.CurrentProvider();
                if (prov == null) { ShowAi(L.T("L'assistente non è configurato: apri Assistente AI... dal menu di CorbolSys.", "The assistant is not set up: open AI assistant... from the CorbolSys menu.")); return; }
                if (an == null || worker != null) return;
                var a = AiSettings.Anonymize ? BuildAnon() : null;
                string data = an.ReportText(prov == LocalAi.Instance);
                if (a != null) data = a.Apply(data);
                var req = new AiRequest { System = SystemPrompt(UsesTools(prov)) };
                req.Messages.Add(new AiMessage("user", "Analysis of my PC:\n\n" + data + "\n\n" + ExplainRequest));
                if (UsesTools(prov)) req.Tools = SysTools.Definitions();
                text = SentText(prov, req);
            }
            using (var f = new Form
            {
                Text = L.T("Cosa viene inviato - CorbolSys", "What is sent - CorbolSys"), StartPosition = FormStartPosition.CenterParent,
                Font = Font, Size = new Size((int)(760 * UiFont.Scale * ScreenDpi() / 96f), (int)(600 * UiFont.Scale * ScreenDpi() / 96f)), MinimizeBox = false, ShowInTaskbar = false, Icon = Icon
            })
            {
                var tb = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Fill, Text = Crlf(text), BackColor = Color.White, Font = new Font("Segoe UI", UiFont.Pt + 1f) };
                f.Controls.Add(tb);
                tb.SelectionStart = 0;
                f.ShowDialog(this);
            }
        }
    }
}
