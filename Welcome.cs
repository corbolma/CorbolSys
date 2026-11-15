// Presentazione per chi usa CorbolSys per la prima volta: 4 schede (il widget, il menu, l'analisi, lo spazio su disco).
// Compare una volta sola al primo avvio (setting welcome=1) e si riapre dal menu (Benvenuto...).
// Le illustrazioni sono disegnate qui con valori di esempio (mai i dati veri del PC) e i testi nella lingua dell'app.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CorbolSys
{
    class WelcomeForm : Form
    {
        static WelcomeForm open;
        public static WelcomeForm Current { get { return open != null && !open.IsDisposed ? open : null; } }

        public static void ShowOnce(Icon icon)
        {
            if (open == null || open.IsDisposed) open = new WelcomeForm(icon);
            open.Show();
            open.WindowState = FormWindowState.Normal;
            open.Activate();
        }

        public const int Pages = 4;
        readonly Panel art;
        readonly Label lblTitle, lblText;
        readonly Button btnBack, btnNext;
        readonly LinkLabel lnkSkip, lnkGuide;
        int page;

        static readonly Color ArtBack = Color.FromArgb(243, 245, 248), Accent = Color.FromArgb(40, 110, 200);
        static readonly Color Good = Color.FromArgb(46, 160, 80), Bad = Color.FromArgb(210, 55, 55), Warn = Color.FromArgb(230, 145, 0);
        static readonly Color Ram = Color.FromArgb(140, 80, 200), Gpu = Color.FromArgb(230, 120, 30);

        WelcomeForm(Icon icon)
        {
            Text = L.T("Benvenuto in CorbolSys", "Welcome to CorbolSys");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true; // il widget sta sotto le finestre: questa deve vedersi subito
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", UiFont.Pt);
            BackColor = Color.White;
            if (icon != null) Icon = icon;

            art = new DoubleBufferedPanel { Location = new Point(0, 0), Size = new Size(560, 230), BackColor = ArtBack };
            art.Paint += (s, e) => DrawArt(e.Graphics, art.ClientSize);
            lblTitle = new Label { AutoSize = true, Location = new Point(24, 246), Font = new Font("Segoe UI Semibold", UiFont.Pt + 3f) };
            // spazio per 6-7 righe: lo stesso testo occupa più o meno righe secondo schermo e impostazioni di Windows
            lblText = new Label { AutoSize = false, Location = new Point(24, 282), Size = new Size(512, 148), Font = new Font("Segoe UI", UiFont.Pt + 1.5f) };
            // dimensioni fisse e larghe: con AutoSize e la scalatura il testo dei collegamenti veniva tagliato
            lnkGuide = new LinkLabel { Text = L.T("Apri la guida completa", "Open the full guide"), AutoSize = false, Location = new Point(24, 438), Size = new Size(260, 26),
                                       TextAlign = ContentAlignment.MiddleLeft, LinkBehavior = LinkBehavior.AlwaysUnderline, Visible = false };
            lnkGuide.LinkClicked += delegate { Help.Open(Help.Guide); };
            lnkSkip = new LinkLabel { Text = L.T("Salta", "Skip"), AutoSize = false, Location = new Point(24, 485), Size = new Size(200, 26),
                                      TextAlign = ContentAlignment.MiddleLeft, LinkBehavior = LinkBehavior.AlwaysUnderline };
            lnkSkip.LinkClicked += delegate { Close(); };
            btnBack = new Button { Text = L.T("Indietro", "Back"), Location = new Point(332, 482), Size = new Size(100, 32) };
            btnNext = new Button { Location = new Point(440, 482), Size = new Size(100, 32) };
            btnBack.Click += delegate { if (page > 0) { page--; ShowPage(); } };
            btnNext.Click += delegate { if (page < Pages - 1) { page++; ShowPage(); } else Close(); };
            AcceptButton = btnNext;
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) Close();
                else if (e.KeyCode == Keys.Right && page < Pages - 1) { page++; ShowPage(); }
                else if (e.KeyCode == Keys.Left && page > 0) { page--; ShowPage(); }
            };
            Controls.AddRange(new Control[] { art, lblTitle, lblText, lnkGuide, lnkSkip, btnBack, btnNext });
            ClientSize = new Size(560, 528);
            Scale(new SizeF(UiFont.Scale, UiFont.Scale)); // coordinate pensate per 9 pt
            ShowPage();
            Shown += delegate { btnNext.Focus(); };
        }

        // Per /snapshot: mostra una scheda
        public void GoTo(int p)
        {
            page = Math.Max(0, Math.Min(Pages - 1, p));
            ShowPage();
        }

        void ShowPage()
        {
            btnBack.Enabled = page > 0;
            btnNext.Text = page < Pages - 1 ? L.T("Avanti", "Next") : L.T("Inizia", "Start");
            lnkSkip.Visible = page < Pages - 1;
            lnkGuide.Visible = page == Pages - 1;
            switch (page)
            {
                case 0:
                    lblTitle.Text = L.T("Il widget", "The widget");
                    lblText.Text = L.T("CorbolSys mostra sul desktop quanto lavorano processore, memoria, scheda video, dischi e rete, la temperatura, " +
                                       "la batteria e i programmi più pesanti. Resta sotto le finestre e lo vedi con Win+D; l'icona vicino all'orologio " +
                                       "mostra l'uso del processore.",
                                       "CorbolSys shows on the desktop how hard the processor, memory, graphics card, disks and network are working, " +
                                       "the temperature, the battery and the heaviest programs. It stays below the windows and you see it with Win+D; the " +
                                       "icon next to the clock shows processor usage.");
                    break;
                case 1:
                    lblTitle.Text = L.T("Tutto dal tasto destro", "Everything from the right click");
                    lblText.Text = L.T("Tasto destro sul widget o sull'icona vicino all'orologio: lì ci sono tutte le funzioni, dall'analisi del PC " +
                                       "a cosa mostrare nel widget. Doppio clic sull'icona mostra o nasconde il widget.",
                                       "Right-click the widget or the icon next to the clock: all the features are there, from the PC analysis " +
                                       "to what the widget shows. Double-click the icon to show or hide the widget.");
                    break;
                case 2:
                    lblTitle.Text = L.T("Il PC è lento?", "Is the PC slow?");
                    lblText.Text = L.T("Apri Analisi approfondita: controlla processore, memoria, dischi, errori, temperatura, batteria, programmi " +
                                       "all'avvio e aggiornamenti, dà un voto da 0 a 100 e spiega cosa sistemare. Copia report prepara il testo per " +
                                       "l'assistenza; con l'assistente AI (facoltativo) puoi anche fare domande.",
                                       "Open In-depth analysis: it checks processor, memory, disks, errors, temperature, battery, startup programs " +
                                       "and updates, gives a score from 0 to 100 and explains what to fix. Copy report prepares the text for IT " +
                                       "support; with the AI assistant (optional) you can also ask questions.");
                    break;
                default:
                    lblTitle.Text = L.T("Disco pieno?", "Disk full?");
                    lblText.Text = L.T("Spazio su disco mostra quali cartelle occupano più spazio; Pulizia disco toglie in sicurezza file " +
                                       "temporanei e cache. CorbolSys ti avvisa quando qualcosa resta al limite, ad esempio un disco quasi pieno " +
                                       "o il processore sempre al massimo.",
                                       "Disk space shows which folders take the most space; Disk cleanup safely removes temporary files and " +
                                       "caches. CorbolSys notifies you when something stays at the limit, for example a nearly full disk or the " +
                                       "processor always at maximum.");
                    break;
            }
            art.Invalidate();
        }

        // ---------- illustrazioni (valori di esempio) ----------
        void DrawArt(Graphics g, Size sz)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float u = sz.Height / 230f;   // unità: l'altezza del riquadro è 230 a 9 pt
            switch (page)
            {
                case 0: DrawWidget(g, sz, u); break;
                case 1: DrawMenu(g, sz, u); break;
                case 2: DrawAnalysis(g, sz, u); break;
                default: DrawSpace(g, sz, u); break;
            }
            float d = 7 * u, gap = 8 * u, w = Pages * d + (Pages - 1) * gap, x = (sz.Width - w) / 2, y = sz.Height - 14 * u;
            for (int i = 0; i < Pages; i++)
                using (var br = new SolidBrush(i == page ? Accent : Color.FromArgb(200, 205, 212)))
                    g.FillEllipse(br, x + i * (d + gap), y, d, d);
        }

        void Card(Graphics g, RectangleF r, float u, Color back)
        {
            using (var sh = new SolidBrush(Color.FromArgb(35, 0, 0, 0))) g.FillRectangle(sh, r.X + 3 * u, r.Y + 3 * u, r.Width, r.Height);
            using (var br = new SolidBrush(back)) g.FillRectangle(br, r);
            using (var pen = new Pen(Color.FromArgb(200, 205, 212))) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        }

        // Il widget in tema scuro, come appare sul desktop
        void DrawWidget(Graphics g, Size sz, float u)
        {
            var r = new RectangleF((sz.Width - 250 * u) / 2, 8 * u, 250 * u, 196 * u);
            Card(g, r, u, Color.FromArgb(28, 31, 38));
            using (var accentBar = new SolidBrush(Accent)) g.FillRectangle(accentBar, r.X, r.Y, 3 * u, r.Height);
            Color label = Color.FromArgb(150, 158, 170), value = Color.FromArgb(230, 233, 238), track = Color.FromArgb(55, 60, 70);
            using (var fTitle = new Font("Segoe UI Semibold", 11f * u))
            using (var fSmall = new Font(Font.FontFamily, 6.5f * u))
            using (var f = new Font("Consolas", 7.5f * u))
            using (var lb = new SolidBrush(label))
            using (var vb = new SolidBrush(value))
            {
                float x = r.X + 10 * u, y = r.Y + 6 * u, w = r.Width - 20 * u;
                g.DrawString(L.T("DESKTOP-ESEMPIO", "DESKTOP-EXAMPLE"), fTitle, vb, x, y);
                y += fTitle.GetHeight(g);
                g.DrawString("Intel Core i5 · 16 GB RAM", fSmall, lb, x, y);
                y += fSmall.GetHeight(g) + 4 * u;
                float lh = f.GetHeight(g) + 2 * u;
                Action<string, Color, float, string> bar = (name, c, pct, txt) =>
                {
                    g.DrawString(name, f, lb, x, y);
                    float bx = x + 34 * u, bw = w - 34 * u - 40 * u, by = y + lh / 2 - 2.5f * u;
                    using (var tb = new SolidBrush(track)) g.FillRectangle(tb, bx, by, bw, 5 * u);
                    using (var cb = new SolidBrush(c)) g.FillRectangle(cb, bx, by, bw * pct, 5 * u);
                    var ts = g.MeasureString(txt, f);
                    g.DrawString(txt, f, vb, x + w - ts.Width, y);
                    y += lh;
                };
                bar("CPU", Accent, 0.23f, "23%");
                bar("RAM", Ram, 0.58f, "58%");
                bar("GPU", Gpu, 0.06f, "6%");
                y += 3 * u;
                bar("C:", Accent, 0.64f, "64%");
                g.DrawString(L.T("RETE", "NET"), f, lb, x, y);
                using (var nb = new SolidBrush(Color.FromArgb(90, 170, 240))) g.DrawString("↓ 1,2 MB/s", f, nb, x + 34 * u, y);
                using (var ub = new SolidBrush(Color.FromArgb(80, 190, 110))) g.DrawString("↑ 85 KB/s", f, ub, x + 110 * u, y);
                y += lh;
                g.DrawString("TEMP", f, lb, x, y);
                g.DrawString("52 °C", f, vb, x + 34 * u, y);
                g.DrawString("BATT", f, lb, x + 110 * u, y);
                g.DrawString("87%", f, vb, x + 144 * u, y);
                y += lh + 3 * u;
                using (var ab = new SolidBrush(Color.FromArgb(90, 160, 240))) g.DrawString(L.T("PROCESSI", "PROCESSES"), fSmall, ab, x, y);
                y += fSmall.GetHeight(g) + 1 * u;
                foreach (var p in new[] { new[] { "Teams", "6,1%" }, new[] { "chrome", "3,4%" }, new[] { "explorer", "0,8%" } })
                {
                    g.DrawString(p[0], f, vb, x, y);
                    var ts = g.MeasureString(p[1], f);
                    g.DrawString(p[1], f, vb, x + w - ts.Width, y);
                    y += lh - 1 * u;
                }
            }
        }

        void DrawMenu(Graphics g, Size sz, float u)
        {
            string hot = L.T("Analisi approfondita...", "In-depth analysis...");
            var items = new List<string>
            {
                "CorbolSys " + AppVersion.Text, "-", hot, L.T("Spazio su disco...", "Disk space..."), L.T("Pulizia disco...", "Disk cleanup..."),
                L.T("Assistente AI...", "AI assistant..."), L.T("Gestione attività", "Task Manager"), "-",
                L.T("Mostra widget", "Show widget"), L.T("Monitoraggio", "Monitoring") + "  ▸"
            };
            using (var f = new Font(Font.FontFamily, Font.Size * 0.9f))
            {
                float lh = f.GetHeight(g) * 1.12f, sepH = 7 * u;
                float h = 10 * u, w = 0;
                foreach (var it in items) { h += it == "-" ? sepH : lh; if (it != "-") w = Math.Max(w, g.MeasureString(it, f).Width); }
                w += 40 * u;
                var r = new RectangleF(sz.Width / 2 - w / 2 + 50 * u, 10 * u, w, h);
                Card(g, r, u, Color.White);
                float y = r.Y + 5 * u;
                foreach (var it in items)
                {
                    if (it == "-")
                    {
                        using (var pen = new Pen(Color.FromArgb(225, 228, 232))) g.DrawLine(pen, r.X + 8 * u, y + sepH / 2, r.Right - 8 * u, y + sepH / 2);
                        y += sepH;
                        continue;
                    }
                    if (it == hot) using (var hb = new SolidBrush(Color.FromArgb(220, 232, 248))) g.FillRectangle(hb, r.X + 3 * u, y, r.Width - 6 * u, lh);
                    using (var tb = new SolidBrush(it.StartsWith("CorbolSys") ? Color.Gray : Color.FromArgb(30, 30, 30)))
                        g.DrawString(it, f, tb, r.X + 24 * u, y + (lh - f.GetHeight(g)) / 2);
                    y += lh;
                }
                // mouse con il tasto destro evidenziato, a sinistra del menu
                float mx = r.X - 95 * u, my = r.Y + 30 * u, mw = 54 * u, mh = 80 * u;
                using (var path = Rounded(new RectangleF(mx, my, mw, mh), 26 * u))
                {
                    g.FillPath(Brushes.White, path);
                    using (var pen = new Pen(Color.FromArgb(120, 125, 135), 1.6f * u)) g.DrawPath(pen, path);
                }
                using (var right = new GraphicsPath())
                {
                    right.AddArc(mx, my, mw, 52 * u, 270, 90);
                    right.AddLine(mx + mw, my + 30 * u, mx + mw / 2, my + 30 * u);
                    right.AddLine(mx + mw / 2, my + 30 * u, mx + mw / 2, my);
                    right.CloseFigure();
                    using (var br = new SolidBrush(Accent)) g.FillPath(br, right);
                }
                using (var pen = new Pen(Color.FromArgb(120, 125, 135), 1.2f * u))
                {
                    g.DrawLine(pen, mx, my + 30 * u, mx + mw, my + 30 * u);
                    g.DrawLine(pen, mx + mw / 2, my, mx + mw / 2, my + 30 * u);
                }
                using (var pen = new Pen(Accent, 2f * u) { DashStyle = DashStyle.Dot, EndCap = LineCap.ArrowAnchor })
                    g.DrawLine(pen, mx + mw + 6 * u, my + 14 * u, r.X - 8 * u, my + 14 * u);
            }
        }

        static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        void DrawAnalysis(Graphics g, Size sz, float u)
        {
            var rows = new[]
            {
                new KeyValuePair<string, Color>(L.T("Processore", "Processor") + "  ·  " + L.T("uso normale", "normal use"), Good),
                new KeyValuePair<string, Color>(L.T("Memoria", "Memory") + "  ·  " + L.T("quasi piena (88%)", "almost full (88%)"), Warn),
                new KeyValuePair<string, Color>(L.T("Spazio su disco", "Disk space") + "  ·  " + L.T("C: solo 6 GB liberi", "C: only 6 GB free"), Bad),
                new KeyValuePair<string, Color>(L.T("Avvio automatico", "Startup programs") + "  ·  " + L.T("14 programmi", "14 programs"), Warn),
                new KeyValuePair<string, Color>(L.T("Antivirus", "Antivirus") + "  ·  " + L.T("attivo e aggiornato", "on and up to date"), Good),
            };
            using (var f = new Font(Font.FontFamily, Font.Size * 0.9f))
            using (var fb = new Font("Segoe UI Semibold", Font.Size * 0.95f))
            {
                float lh = f.GetHeight(g) * 1.35f;
                float w = 0;
                foreach (var r0 in rows) w = Math.Max(w, g.MeasureString(r0.Key, f).Width);
                string concl = L.F("Il PC funziona, con alcuni punti da sistemare ({0}/100)", "The PC works, with a few things to fix ({0}/100)", 68);
                w = Math.Max(w + 50 * u, g.MeasureString(concl, fb).Width + 30 * u);
                float h = rows.Length * lh + 1.6f * lh + 14 * u;
                var r = new RectangleF((sz.Width - w) / 2, 10 * u, w, h);
                Card(g, r, u, Color.White);
                float y = r.Y + 8 * u, dot = 10 * u;
                foreach (var row in rows)
                {
                    using (var br = new SolidBrush(row.Value)) g.FillEllipse(br, r.X + 14 * u, y + (lh - dot) / 2, dot, dot);
                    g.DrawString(row.Key, f, Brushes.Black, r.X + 32 * u, y + (lh - f.GetHeight(g)) / 2);
                    y += lh;
                }
                y += 8 * u;
                using (var br = new SolidBrush(Color.FromArgb(190, 110, 0))) g.DrawString(concl, fb, br, r.X + 14 * u, y);
            }
        }

        void DrawSpace(Graphics g, Size sz, float u)
        {
            var folders = new[]
            {
                new KeyValuePair<string, float>(L.T("Download", "Downloads"), 0.38f), new KeyValuePair<string, float>(L.T("Video", "Videos"), 0.22f),
                new KeyValuePair<string, float>("AppData", 0.15f), new KeyValuePair<string, float>(L.T("Documenti", "Documents"), 0.09f)
            };
            string[] sizes = { "38 GB", "22 GB", "15 GB", "9 GB" };
            var r = new RectangleF(36 * u, 14 * u, 300 * u, 122 * u);
            Card(g, r, u, Color.White);
            using (var f = new Font(Font.FontFamily, Font.Size * 0.88f))
            using (var fb = new Font("Segoe UI Semibold", Font.Size * 0.9f))
            {
                float x = r.X + 12 * u, y = r.Y + 8 * u, w = r.Width - 24 * u;
                g.DrawString("C:  ·  " + L.T("6 GB liberi su 256 GB", "6 GB free of 256 GB"), fb, Brushes.Black, x, y);
                y += fb.GetHeight(g) + 3 * u;
                using (var tb = new SolidBrush(Color.FromArgb(225, 228, 232))) g.FillRectangle(tb, x, y, w, 7 * u);
                using (var cb = new SolidBrush(Bad)) g.FillRectangle(cb, x, y, w * 0.97f, 7 * u);
                y += 16 * u;
                float lh = f.GetHeight(g) * 1.25f;
                for (int i = 0; i < folders.Length; i++)
                {
                    g.DrawString(folders[i].Key, f, Brushes.Black, x, y);
                    float bx = x + 90 * u, bw = w - 90 * u - 50 * u;
                    using (var cb = new SolidBrush(Color.FromArgb(120, 160, 220))) g.FillRectangle(cb, bx, y + lh / 2 - 4 * u, bw * folders[i].Value / 0.38f, 8 * u);
                    var ts = g.MeasureString(sizes[i], f);
                    g.DrawString(sizes[i], f, Brushes.DimGray, x + w - ts.Width, y);
                    y += lh;
                }

                // notifica come quelle di Windows, a destra
                string title = L.F("Disco {0} quasi pieno", "Drive {0} almost full", "C:");
                string body = L.F("Restano {0} liberi.", "{0} left.", "6 GB");
                float tw = Math.Max(g.MeasureString(title, fb).Width, g.MeasureString(body, f).Width) + 24 * u;
                float th = fb.GetHeight(g) + f.GetHeight(g) + 16 * u;
                var t = new RectangleF(sz.Width - tw - 24 * u, r.Bottom - th + 30 * u, tw, th);
                using (var sh = new SolidBrush(Color.FromArgb(50, 0, 0, 0))) g.FillRectangle(sh, t.X + 3 * u, t.Y + 3 * u, tw, th);
                using (var br = new SolidBrush(Color.FromArgb(40, 44, 52))) g.FillRectangle(br, t);
                using (var br = new SolidBrush(Warn)) g.FillRectangle(br, t.X, t.Y, 4 * u, th);
                g.DrawString(title, fb, Brushes.White, t.X + 12 * u, t.Y + 7 * u);
                using (var br = new SolidBrush(Color.FromArgb(210, 214, 220))) g.DrawString(body, f, br, t.X + 12 * u, t.Y + 7 * u + fb.GetHeight(g));
            }
        }

        class DoubleBufferedPanel : Panel
        {
            public DoubleBufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; }
        }
    }
}
