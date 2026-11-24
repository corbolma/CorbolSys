// Finestra "Ping ricevuti": chi ha fatto ping a questo PC (vedi PingWatch.cs). Si apre da Porte aperte.
// Senza registro del firewall mostra solo quando sono arrivati (mentre CorbolSys era aperta); con il registro
// acceso mostra indirizzo, esito (risposto o bloccato) e quante volte. Con una cattura di pktmon fermata (PingCapture.cs)
// mostra quella, che vede i ping sulla scheda di rete, prima del firewall: indirizzo, risposto o senza risposta.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CorbolSys
{
    class PingForm : Form
    {
        static PingForm open;
        public static PingForm Current { get { return open != null && !open.IsDisposed ? open : null; } }
        public bool Busy { get { return loading; } }

        public static void ShowOnce(Form owner)
        {
            if (open == null || open.IsDisposed) open = new PingForm(owner != null ? owner.Icon : null);
            else open.Reload();
            open.Show();
            if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal;
            open.Activate();
        }

        readonly Label lblStatus, lblLog, lblEmpty;
        readonly ListView list;
        readonly Button btnLog, btnRefresh, btnCopy, btnCapture, btnDiscard;
        volatile bool loading;
        PingWatch.LogState logState;
        List<PingSource> sources = new List<PingSource>();
        List<PingEvent> counted = new List<PingEvent>();
        bool logReadable;
        PingCapture.State capState;
        DateTime capStart, capStop;
        List<PingSource> capSources = new List<PingSource>();
        bool capReadable;
        bool ShowingCapture { get { return capState == PingCapture.State.Done && capReadable; } }

        PingForm(Icon icon)
        {
            Text = L.T("Ping ricevuti - CorbolSys", "Pings received - CorbolSys");
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", UiFont.Pt);
            BackColor = Color.White;
            if (icon != null) Icon = icon;

            var top = new Panel { Dock = DockStyle.Top, Height = 128, Padding = new Padding(12, 8, 12, 4) };
            lblStatus = new Label { Dock = DockStyle.Top, Height = 44, AutoEllipsis = true };
            lblLog = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.FromArgb(80, 80, 80) };
            top.Controls.Add(lblLog);
            top.Controls.Add(lblStatus);

            list = new ListView
            {
                View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
                Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            list.Columns.Add(L.T("Da", "From"));
            list.Columns.Add(L.T("Dove", "Where"));
            list.Columns.Add(L.T("Esito", "Result"));
            list.Columns.Add(L.T("Ping", "Pings"));
            list.Columns.Add(L.T("Primo", "First"));
            list.Columns.Add(L.T("Ultimo", "Last"));
            list.Resize += delegate { FitColumns(); };
            // messaggio quando l'elenco è vuoto, sotto le intestazioni delle colonne
            lblEmpty = new Label { AutoSize = false, Height = 60, Dock = DockStyle.Bottom, TextAlign = ContentAlignment.TopCenter, ForeColor = Color.FromArgb(110, 110, 110), Visible = false };
            list.Controls.Add(lblEmpty);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 84, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(10, 4, 10, 4) };
            btnLog = new Button { Size = new Size(260, 30), Enabled = false };
            btnCapture = new Button { Size = new Size(260, 30), Enabled = false, Visible = PingCapture.Available };
            btnDiscard = new Button { Text = L.T("Elimina la cattura", "Delete the capture"), Size = new Size(150, 30), Visible = false };
            btnCapture.Click += delegate { ToggleCapture(); };
            btnDiscard.Click += delegate { DiscardCapture(); };
            btnRefresh = new Button { Text = L.T("Aggiorna", "Refresh"), Size = new Size(100, 30) };
            btnCopy = new Button { Text = L.T("Copia elenco", "Copy list"), Size = new Size(120, 30), Enabled = false };
            var btnClose = new Button { Text = L.T("Chiudi", "Close"), Size = new Size(100, 30) };
            btnLog.Click += delegate { ToggleLog(); };
            btnRefresh.Click += delegate { Reload(); };
            btnCopy.Click += delegate { try { Clipboard.SetText(ListText()); } catch { } };
            btnClose.Click += delegate { Close(); };
            CancelButton = btnClose;
            bar.Controls.AddRange(new Control[] { btnLog, btnCapture, btnDiscard, btnRefresh, btnCopy, btnClose, Help.Button(this, Help.Ports, Point.Empty) });
            bar.SetFlowBreak(btnCapture, true);

            Controls.Add(list);
            Controls.Add(top);
            Controls.Add(bar);
            ClientSize = new Size(760, 500);
            MinimumSize = new Size(560, 340);
            Scale(new SizeF(UiFont.Scale, UiFont.Scale));
            Shown += delegate { FitColumns(); Reload(); };
        }

        void FitColumns()
        {
            int w = list.Width - SystemInformation.VerticalScrollBarWidth - 2 * SystemInformation.Border3DSize.Width - 2;
            if (w <= 0) return;
            int[] pct = { 26, 14, 14, 10, 18 };
            int used = 0;
            for (int i = 0; i < pct.Length; i++) { list.Columns[i].Width = w * pct[i] / 100; used += list.Columns[i].Width; }
            list.Columns[5].Width = w - used - 4;
        }

        public void Reload()
        {
            if (loading) return;
            loading = true;
            btnRefresh.Enabled = btnCopy.Enabled = btnLog.Enabled = btnCapture.Enabled = btnDiscard.Enabled = false;
            lblStatus.Text = L.T("Lettura in corso...", "Reading...");
            var t = new Thread(delegate ()
            {
                string status = "";
                var state = PingWatch.LogState.Off;
                bool readable = false;
                var src = new List<PingSource>();
                var cnt = new List<PingEvent>();
                var cst = PingCapture.State.None;
                DateTime cFrom = DateTime.MinValue, cTo = DateTime.MinValue;
                bool cRead = false;
                var csrc = new List<PingSource>();
                try
                {
                    cst = PingCapture.Current(out cFrom, out cTo);
                    if (cst == PingCapture.State.Done) csrc = PingWatch.Group(PingCapture.Results(out cRead));
                }
                catch { }
                try
                {
                    status = PingWatch.Summary();
                    state = PingWatch.Logging();
                    if (state == PingWatch.LogState.Ours) src = PingWatch.Group(PingWatch.FromLog(out readable));
                    cnt = PingWatch.SeenWhileOpen();
                }
                catch { }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        lblStatus.Text = status;
                        logState = state; logReadable = readable; sources = src; counted = cnt;
                        capState = cst; capStart = cFrom; capStop = cTo; capReadable = cRead; capSources = csrc;
                        loading = false;
                        btnRefresh.Enabled = btnCopy.Enabled = btnLog.Enabled = btnCapture.Enabled = btnDiscard.Enabled = true;
                        Fill();
                    }));
                }
                catch { loading = false; }
            });
            t.IsBackground = true;
            t.Start();
        }

        void Fill()
        {
            btnLog.Text = logState == PingWatch.LogState.Ours ? L.T("Smetti di registrare chi fa ping", "Stop recording who pings")
                                                              : L.T("Registra chi fa ping...", "Record who pings...");
            switch (logState)
            {
                case PingWatch.LogState.Ours:
                    lblLog.Text = logReadable
                        ? L.F("Il registro del firewall di Windows è acceso: qui sotto chi ha fatto ping (anche quelli bloccati), dal più recente. Il registro tiene fino a {0} MB, poi ricomincia.",
                              "The Windows firewall log is on: below, who pinged this PC (blocked pings too), most recent first. The log keeps up to {0} MB, then starts over.", 16 * 2)
                        : L.T("Il registro del firewall è acceso ma non c'è ancora o non si legge: riprova tra qualche minuto.",
                              "The firewall log is on but not there yet or not readable: try again in a few minutes.");
                    break;
                case PingWatch.LogState.Elsewhere:
                    lblLog.Text = L.T("Il registro del firewall è già acceso da qualcun altro, in una cartella che CorbolSys non legge. Con \"Registra chi fa ping...\" lo sposti nella cartella di CorbolSys.",
                                      "The firewall log was already turned on by someone else, in a folder CorbolSys can't read. \"Record who pings...\" moves it to the CorbolSys folder.");
                    break;
                default:
                    lblLog.Text = L.T("Chi ha fatto ping lo sa solo il registro del firewall di Windows, che ora è spento: qui sotto vedi solo quando sono arrivati i ping, mentre CorbolSys era aperta. \"Registra chi fa ping...\" lo accende (serve la conferma di amministratore).",
                                      "Only the Windows firewall log knows who pinged, and it's off now: below you only see when pings arrived while CorbolSys was open. \"Record who pings...\" turns it on (administrator confirmation needed).");
                    break;
            }

            bool running = capState == PingCapture.State.Running || capState == PingCapture.State.Interrupted;
            btnCapture.Text = running ? L.T("Ferma la cattura e leggila", "Stop the capture and read it")
                            : capState == PingCapture.State.Done ? L.T("Nuova cattura con pktmon...", "New capture with pktmon...")
                            : L.T("Cattura i ping con pktmon...", "Capture pings with pktmon...");
            btnDiscard.Visible = capState == PingCapture.State.Done;
            if (capState == PingCapture.State.Running)
                lblLog.Text = L.F("Cattura con pktmon in corso dalle {0}: usa il PC normalmente, anche a CorbolSys chiusa, poi \"Ferma la cattura e leggila\" per vedere chi ha fatto ping. Intanto: ",
                                  "Capture with pktmon running since {0}: use the PC as usual, even with CorbolSys closed, then \"Stop the capture and read it\" to see who pinged. Meanwhile: ",
                                  capStart.ToString("t")) + lblLog.Text;
            else if (capState == PingCapture.State.Interrupted)
                lblLog.Text = L.F("La cattura con pktmon avviata alle {0} si è fermata col riavvio del PC: \"Ferma la cattura e leggila\" mostra i ping arrivati fino ad allora.",
                                  "The pktmon capture started at {0} stopped when the PC restarted: \"Stop the capture and read it\" shows the pings received until then.",
                                  capStart.ToString("g"));
            else if (capState == PingCapture.State.Done)
                lblLog.Text = capReadable
                    ? L.F("Cattura con pktmon dalle {0} alle {1}: qui sotto chi ha fatto ping al PC in quel periodo, dal più recente. pktmon vede i ping sulla scheda di rete, prima del firewall: \"senza risposta\" sono quelli fermati dal firewall.",
                          "Capture with pktmon from {0} to {1}: below, who pinged the PC in that period, most recent first. pktmon sees pings on the network adapter, before the firewall: \"no answer\" means the firewall stopped them.",
                          capStart.ToString("g"), capStop.ToString("t"))
                    : L.T("La cattura con pktmon è stata fermata ma il file dei risultati non si legge. Prova una nuova cattura.",
                          "The pktmon capture was stopped but its results file can't be read. Try a new capture.");

            list.BeginUpdate();
            list.Items.Clear();
            if (ShowingCapture)
            {
                foreach (var s in capSources)
                    list.Items.Add(new ListViewItem(new[]
                    {
                        s.From, s.Where, s.Allowed ? L.T("risposto", "answered") : L.T("senza risposta", "no answer"),
                        s.Count.ToString(), s.First.ToString("g"), s.Last.ToString("g")
                    }) { ForeColor = s.Allowed ? list.ForeColor : Color.FromArgb(120, 120, 120) });
                lblEmpty.Text = L.T("Nessun ping ricevuto durante la cattura", "No pings received during the capture");
            }
            else if (logState == PingWatch.LogState.Ours && logReadable)
            {
                foreach (var s in sources)
                    list.Items.Add(new ListViewItem(new[]
                    {
                        s.From, s.Where, s.Allowed ? L.T("risposto", "answered") : L.T("bloccato", "blocked"),
                        s.Count.ToString(), s.First.ToString("g"), s.Last.ToString("g")
                    }) { ForeColor = s.Allowed ? list.ForeColor : Color.FromArgb(120, 120, 120) });
                lblEmpty.Text = L.T("Nessun ping nel registro", "No pings in the log");
            }
            else
            {
                foreach (var e in counted.OrderByDescending(x => x.Time))
                    list.Items.Add(new ListViewItem(new[] { L.T("(non noto)", "(unknown)"), "", L.T("risposto", "answered"), e.Count.ToString(), e.Time.ToString("g"), e.Time.ToString("g") }));
                lblEmpty.Text = L.T("Nessun ping da quando CorbolSys è aperta", "No pings since CorbolSys was opened");
            }
            list.EndUpdate();
            lblEmpty.Visible = list.Items.Count == 0;
            if (lblEmpty.Visible) lblEmpty.Height = Math.Max(40, list.ClientSize.Height - 40);
            FitColumns();
        }

        void ToggleLog()
        {
            bool on = logState != PingWatch.LogState.Ours;
            if (on && MessageBox.Show(this,
                    L.F("Per sapere chi fa ping, CorbolSys accende il registro del firewall di Windows: Windows scriverà le connessioni lasciate entrare e quelle bloccate in {0} (al massimo {1} MB, poi ricomincia). Lo legge solo il tuo utente e gli amministratori.\n\nServe la conferma di amministratore. Lo spegni quando vuoi da questa finestra.",
                        "To know who pings, CorbolSys turns on the Windows firewall log: Windows will write the connections it lets in and the ones it blocks to {0} (at most {1} MB, then it starts over). Only your user and administrators can read it.\n\nAdministrator confirmation is needed. Turn it off any time from this window.",
                        PingWatch.LogDir, 16 * 2),
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            btnLog.Enabled = btnRefresh.Enabled = false;
            var t = new Thread(delegate ()
            {
                bool ok = PingWatch.SetLoggingElevated(on);
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (!ok) MessageBox.Show(this, L.T("Non è stato possibile cambiare il registro del firewall (conferma annullata o permessi negati).",
                                                           "The firewall log could not be changed (confirmation cancelled or permission denied)."),
                                                 Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        Reload();
                    }));
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        void ToggleCapture()
        {
            bool start = capState != PingCapture.State.Running && capState != PingCapture.State.Interrupted;
            if (start && MessageBox.Show(this,
                    L.F("CorbolSys avvia pktmon, il Monitoraggio pacchetti di Windows, solo per i ping (ICMP): registra i ping che arrivano alla scheda di rete, con l'indirizzo di chi li manda, anche quelli che il firewall blocca. Il file va in {0} (al massimo {1} MB) e lo leggono solo il tuo utente e gli amministratori.\n\nLa cattura continua anche a CorbolSys chiusa, finché non la fermi da questa finestra o riavvii il PC. Per vedere se i ping arrivano durante un'analisi, rifai l'analisi mentre la cattura è in corso.\n\nServe la conferma di amministratore, ora e quando la fermi. Eventuali filtri di pktmon messi da altri vengono tolti.",
                        "CorbolSys starts pktmon, the Windows Packet Monitor, for pings only (ICMP): it records the pings reaching the network adapter, with the sender's address, including those the firewall blocks. The file goes to {0} (at most {1} MB) and only your user and administrators can read it.\n\nThe capture keeps going even with CorbolSys closed, until you stop it from this window or restart the PC. To see whether pings arrive during an analysis, run the analysis again while the capture is running.\n\nAdministrator confirmation is needed, now and when you stop it. Any pktmon filters set by others are removed.",
                        PingCapture.Dir, 64),
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            btnCapture.Enabled = btnDiscard.Enabled = btnRefresh.Enabled = false;
            lblStatus.Text = start ? L.T("Avvio della cattura...", "Starting the capture...") : L.T("Lettura della cattura...", "Reading the capture...");
            var t = new Thread(delegate ()
            {
                int code = PingCapture.RunElevated(start ? "start" : "stop");
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (code == 2)
                            MessageBox.Show(this, L.T("pktmon sta già registrando per qualcun altro (una cattura avviata a mano o da un programma). Fermala con \"pktmon stop\" in un terminale da amministratore, poi riprova.",
                                                      "pktmon is already recording for someone else (a capture started by hand or by a program). Stop it with \"pktmon stop\" in an administrator terminal, then try again."),
                                            Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        else if (code == -1)
                            MessageBox.Show(this, L.T("Conferma di amministratore annullata: la cattura non è cambiata.", "Administrator confirmation cancelled: the capture didn't change."),
                                            Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        else if (code != 0)
                            MessageBox.Show(this, start ? L.T("Non è stato possibile avviare pktmon.", "pktmon could not be started.")
                                                        : L.T("La cattura è stata fermata, ma non è stato possibile leggerne i risultati.", "The capture was stopped, but its results could not be read."),
                                            Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        Reload();
                    }));
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        void DiscardCapture()
        {
            if (MessageBox.Show(this, L.T("Eliminare i risultati della cattura con pktmon?", "Delete the results of the pktmon capture?"),
                                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            if (!PingCapture.Discard())
                MessageBox.Show(this, L.F("Non è stato possibile eliminare la cartella {0}.", "The folder {0} could not be deleted.", PingCapture.Dir),
                                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Reload();
        }

        string ListText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(L.F("Ping ricevuti (CorbolSys {0}) - {1}", "Pings received (CorbolSys {0}) - {1}", AppVersion.Text, DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
            sb.AppendLine(lblStatus.Text);
            sb.AppendLine();
            foreach (ListViewItem it in list.Items)
                sb.AppendLine(string.Join(" - ", it.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text).Where(s => s.Length > 0)));
            return sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
        }
    }
}
