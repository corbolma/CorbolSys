// Finestra "Porte aperte": l'elenco completo delle porte in ascolto (vedi Ports.cs), con programma, da dove sono
// raggiungibili, cosa fa il firewall e la valutazione con il motivo. Si apre dal menu o con un doppio clic sul
// controllo "Porte aperte" dell'analisi approfondita. Nessun traffico di rete.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CorbolSys
{
    class PortsForm : Form
    {
        static PortsForm open;
        public static PortsForm Current { get { return open != null && !open.IsDisposed ? open : null; } }
        public bool Busy { get { return loading; } }

        public static void ShowOnce(Icon icon)
        {
            if (open == null || open.IsDisposed) open = new PortsForm(icon);
            else open.Reload();
            open.Show();
            if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal;
            open.Activate();
        }

        readonly ListView list;
        readonly Label lblSummary;
        readonly CheckBox chkLocal;
        readonly TextBox txtDetail;
        readonly Button btnRefresh, btnCopy, btnFolder;
        List<OpenPort> ports = new List<OpenPort>();
        string pingLine = "";
        volatile bool loading;

        PortsForm(Icon icon)
        {
            Text = L.T("Porte aperte - CorbolSys", "Open ports - CorbolSys");
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", UiFont.Pt);
            BackColor = Color.White;
            if (icon != null) Icon = icon;

            float dpi = 96f;
            try { using (var g = CreateGraphics()) dpi = g.DpiX; } catch { }
            int iconSize = (int)Math.Round(16 * UiFont.Scale * dpi / 96f);
            var icons = new ImageList { ImageSize = new Size(iconSize, iconSize), ColorDepth = ColorDepth.Depth32Bit };
            foreach (CheckState st in Enum.GetValues(typeof(CheckState))) icons.Images.Add(st.ToString(), AnalysisForm.StateIcon(st, iconSize));

            var top = new Panel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(12, 8, 12, 0) };
            lblSummary = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, Text = L.T("Lettura delle porte in corso...", "Reading the ports...") };
            chkLocal = new CheckBox { Dock = DockStyle.Bottom, Height = 26, Text = L.T("Mostra anche le porte raggiungibili solo da questo PC", "Also show ports reachable only from this PC") };
            chkLocal.CheckedChanged += delegate { Fill(); };
            top.Controls.Add(lblSummary);
            top.Controls.Add(chkLocal);

            list = new ListView
            {
                View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, ShowItemToolTips = true,
                SmallImageList = icons, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            list.Columns.Add(L.T("Porta", "Port"));
            list.Columns.Add(L.T("Programma", "Program"));
            list.Columns.Add(L.T("Raggiungibile", "Reachable"));
            list.Columns.Add("Firewall");
            list.Columns.Add(L.T("Valutazione", "Assessment"));
            list.SelectedIndexChanged += delegate { ShowDetail(); };
            list.Resize += delegate { FitColumns(); };

            txtDetail = new TextBox { Dock = DockStyle.Bottom, Height = 104, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(10, 6, 10, 6) };
            btnRefresh = new Button { Text = L.T("Aggiorna", "Refresh"), Size = new Size(100, 30) };
            btnCopy = new Button { Text = L.T("Copia elenco", "Copy list"), Size = new Size(120, 30), Enabled = false };
            btnFolder = new Button { Text = L.T("Apri cartella del programma", "Open program folder"), Size = new Size(210, 30), Enabled = false };
            var btnPing = new Button { Text = L.T("Ping ricevuti...", "Pings received..."), Size = new Size(150, 30) };
            btnPing.Click += delegate { PingForm.ShowOnce(this); };
            var btnClose = new Button { Text = L.T("Chiudi", "Close"), Size = new Size(100, 30) };
            btnRefresh.Click += delegate { Reload(); };
            btnCopy.Click += delegate { try { Clipboard.SetText(ListText()); lblSummary.Text = L.T("Elenco copiato negli appunti.", "List copied to the clipboard."); } catch { } };
            btnFolder.Click += delegate
            {
                var p = Selected();
                if (p != null && p.Path.Length > 0) try { Process.Start("explorer.exe", "/select,\"" + p.Path + "\""); } catch { }
            };
            btnClose.Click += delegate { Close(); };
            CancelButton = btnClose;
            bar.Controls.AddRange(new Control[] { btnRefresh, btnCopy, btnFolder, btnPing, btnClose, Help.Button(this, Help.Ports, Point.Empty) });

            Controls.Add(list);
            Controls.Add(top);
            Controls.Add(txtDetail);
            Controls.Add(bar);
            ClientSize = new Size(900, 560);
            MinimumSize = new Size(640, 400);
            Scale(new SizeF(UiFont.Scale, UiFont.Scale));
            Shown += delegate { FitColumns(); Reload(); };
        }

        void FitColumns()
        {
            // spazio sempre lasciato alla barra verticale: compare dopo il riempimento e farebbe nascere quella orizzontale
            int w = list.Width - SystemInformation.VerticalScrollBarWidth - 2 * SystemInformation.Border3DSize.Width - 2;
            if (w <= 0) return;
            int[] pct = { 12, 30, 20, 14 };
            int used = 0;
            for (int i = 0; i < pct.Length; i++) { list.Columns[i].Width = w * pct[i] / 100; used += list.Columns[i].Width; }
            list.Columns[4].Width = w - used - 4;
        }

        public void Reload()
        {
            if (loading) return;
            loading = true;
            btnRefresh.Enabled = btnCopy.Enabled = false;
            lblSummary.Text = L.T("Lettura delle porte in corso...", "Reading the ports...");
            var t = new Thread(delegate ()
            {
                List<OpenPort> res;
                string ping = "";
                try { res = Ports.List(); } catch { res = new List<OpenPort>(); }
                try { ping = PingWatch.Summary(); } catch { }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        ports = res;
                        pingLine = ping;
                        loading = false;
                        btnRefresh.Enabled = btnCopy.Enabled = true;
                        Fill();
                    }));
                }
                catch { loading = false; }
            });
            t.IsBackground = true;
            t.Start();
        }

        static CheckState StateOf(PortRisk r)
        {
            switch (r)
            {
                case PortRisk.Suspicious: return CheckState.Error;
                case PortRisk.Check: return CheckState.Warn;
                case PortRisk.Info: return CheckState.Info;
                default: return CheckState.Ok;
            }
        }

        void Fill()
        {
            if (loading) return;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var p in ports.Where(x => chkLocal.Checked || x.Reach != PortReach.ThisPc))
            {
                var it = new ListViewItem(new[] { p.Proto + " " + p.Port, p.ProcessLabel, p.ReachText, p.FirewallText, p.RiskText + (p.Why.Length > 0 ? ": " + p.Why : "") })
                {
                    ImageKey = StateOf(p.Risk).ToString(), Tag = p, ToolTipText = p.Line()
                };
                list.Items.Add(it);
            }
            list.EndUpdate();
            FitColumns();   // ora c'è (o non c'è) la barra verticale: le colonne si riadattano senza barra orizzontale
            lblSummary.Text = Summary(ports) + (pingLine.Length > 0 ? "\n" + pingLine : "");
            txtDetail.Text = list.Items.Count > 0 ? L.T("Seleziona una porta per vedere i dettagli.", "Select a port to see the details.") : "";
            btnFolder.Enabled = false;
        }

        public static string Summary(List<OpenPort> ports)
        {
            var net = ports.Where(p => p.Reach != PortReach.ThisPc).ToList();
            int sus = net.Count(p => p.Risk == PortRisk.Suspicious), chk = net.Count(p => p.Risk == PortRisk.Check);
            var parts = new List<string>
            {
                L.F("{0} porte raggiungibili dalla rete", "{0} ports reachable from the network", net.Count),
                L.F("{0} solo da questo PC", "{0} from this PC only", ports.Count - net.Count)
            };
            if (sus > 0) parts.Add(L.F("{0} sospette", "{0} suspicious", sus));
            if (chk > 0) parts.Add(L.F("{0} da guardare", "{0} to check", chk));
            string fw = !Ports.FirewallKnown ? L.T("stato del firewall non noto", "firewall state unknown")
                      : Ports.FirewallOn ? L.T("firewall di Windows acceso", "Windows firewall on") : L.T("firewall di Windows SPENTO", "Windows firewall OFF");
            return string.Join(" · ", parts) + "\n" + fw;
        }

        OpenPort Selected()
        {
            return list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as OpenPort : null;
        }

        void ShowDetail()
        {
            var p = Selected();
            btnFolder.Enabled = p != null && p.Path.Length > 0;
            if (p == null) return;
            var sb = new StringBuilder();
            sb.AppendLine(p.Proto + " " + p.Port + " · " + p.ProcessLabel + " · PID " + p.Pid);
            sb.AppendLine(p.RiskText + ": " + p.Why);
            if (p.Path.Length > 0) sb.AppendLine(p.Path);
            sb.AppendLine(p.SignText + " · " + L.T("ascolta su", "listens on") + " " + p.Address + " (" + p.ReachText + ")" + (p.Reach != PortReach.ThisPc ? " · firewall: " + p.FirewallText : ""));
            txtDetail.Text = sb.ToString().TrimEnd().Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
        }

        string ListText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(L.F("Porte aperte (CorbolSys {0}) - {1}", "Open ports (CorbolSys {0}) - {1}", AppVersion.Text, DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
            sb.AppendLine(Summary(ports));
            if (pingLine.Length > 0) sb.AppendLine(pingLine);
            sb.AppendLine();
            foreach (var p in ports.Where(x => chkLocal.Checked || x.Reach != PortReach.ThisPc)) sb.AppendLine(p.Line());
            return sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
        }
    }
}
