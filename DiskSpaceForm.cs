// Finestra "Spazio su disco": cartelle e file di una cartella ordinati per dimensione, con una barra
// proporzionale. Doppio clic (o Invio) su una cartella per entrarci, "Su" per tornare indietro.
// Il calcolo gira in background e ricorda la dimensione di ogni sottocartella già vista: entrare in una
// cartella calcolata è immediato. Non segue i collegamenti (junction, link simbolici) per non contare due
// volte, e non conta i file di OneDrive che sono solo nel cloud. Sola lettura: non elimina nulla.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace AppSys
{
    class DiskSpaceForm : Form
    {
        static DiskSpaceForm open;
        public static DiskSpaceForm Current { get { return open != null && !open.IsDisposed ? open : null; } }
        public bool Busy { get { return current == null || entries.Any(e => e.IsDir && !e.Done) || dirty; } }

        // drive: "C:" o "C:\"; null = disco di sistema (o quello già aperto)
        public static void ShowOnce(Icon icon, string drive)
        {
            if (open == null || open.IsDisposed) open = new DiskSpaceForm(icon, drive);
            else if (drive != null) open.Navigate(Root(drive));
            open.Show();
            if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal;
            open.Activate();
        }

        static string Root(string drive)
        {
            drive = drive.TrimEnd('\\');
            return drive + "\\";
        }

        class Entry
        {
            public string Name, Path;
            public bool IsDir, Done, Others;   // Others: riga che riassume i file più piccoli
            public long Size;
            public int Count;                  // per "Others": quanti file
            public DateTime Modified;
        }

        // Dimensioni già calcolate (cartella -> byte) e cartelle non accessibili, per tutta la sessione
        static readonly Dictionary<string, long> cache = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        static int denied;

        const int MaxFiles = 25;   // file mostrati uno per uno, gli altri in una riga riassuntiva
        const FileAttributes CloudOnly = (FileAttributes)0x00400000 | FileAttributes.Offline;   // RECALL_ON_DATA_ACCESS

        readonly ComboBox cbDrive;
        readonly Button btnUp, btnRefresh, btnExplorer;
        readonly Label lblPath, lblStatus;
        readonly ListView list;
        readonly ContextMenuStrip ctx;
        string current;
        List<Entry> entries = new List<Entry>();
        CancellationTokenSource cts;
        long scannedFiles;
        readonly System.Windows.Forms.Timer uiTimer;
        bool dirty, loadError;
        readonly Color barColor = Color.FromArgb(37, 99, 235), barTrack = Color.FromArgb(228, 232, 240);

        DiskSpaceForm(Icon icon, string drive)
        {
            Text = L.T("Spazio su disco - AppSys", "Disk space - AppSys");
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", UiFont.Pt);
            BackColor = Color.White;
            if (icon != null) Icon = icon;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(10, 8, 10, 4), WrapContents = false };
            cbDrive = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250, Margin = new Padding(3, 3, 8, 3) };
            btnUp = new Button { Text = "↑ " + L.T("Su", "Up"), Size = new Size(80, 30) };
            btnRefresh = new Button { Text = L.T("Aggiorna", "Refresh"), Size = new Size(100, 30) };
            btnExplorer = new Button { Text = L.T("Apri in Esplora file", "Open in File Explorer"), Size = new Size(170, 30) };
            top.Controls.AddRange(new Control[] { cbDrive, btnUp, btnRefresh, btnExplorer });

            lblPath = new Label { Dock = DockStyle.Top, Height = 28, Padding = new Padding(14, 4, 10, 0), Font = new Font("Segoe UI Semibold", UiFont.Pt), AutoEllipsis = true };
            lblStatus = new Label { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(14, 6, 10, 0), ForeColor = Color.DimGray, AutoEllipsis = true };

            list = new ListView
            {
                View = View.Details, FullRowSelect = true, MultiSelect = false, Dock = DockStyle.Fill, OwnerDraw = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable, HideSelection = false, ShowItemToolTips = true
            };
            list.Columns.Add(L.T("Nome", "Name"));
            list.Columns.Add(L.T("Dimensione", "Size"), 100, HorizontalAlignment.Right);
            list.Columns.Add(L.T("Quota", "Share"));
            list.Columns.Add(L.T("Modificato", "Modified"));
            list.DrawColumnHeader += (s, e) => e.DrawDefault = true;
            list.DrawItem += (s, e) => { };
            list.DrawSubItem += DrawSubItem;
            list.Resize += delegate { FitColumns(); };
            list.ItemActivate += delegate { OpenSelected(); };
            list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Back) { e.Handled = true; GoUp(); } };
            list.MouseUp += (s, e) => { if (e.Button == MouseButtons.XButton1) GoUp(); };

            ctx = new ContextMenuStrip { Font = Font };
            ctx.Items.Add(L.T("Apri", "Open"), null, delegate { OpenSelected(); });
            ctx.Items.Add(L.T("Apri in Esplora file", "Open in File Explorer"), null, delegate { var e = Selected(); if (e != null) ShowInExplorer(e.Path, !e.IsDir); });
            ctx.Items.Add(L.T("Copia percorso", "Copy path"), null, delegate { var e = Selected(); if (e != null) try { Clipboard.SetText(e.Path); } catch { } });
            ctx.Opening += (s, e) => { var sel = Selected(); if (sel == null || sel.Others) e.Cancel = true; else ctx.Items[0].Enabled = sel.IsDir; };
            list.ContextMenuStrip = ctx;

            btnUp.Click += delegate { GoUp(); };
            btnRefresh.Click += delegate { Reload(true); };
            btnExplorer.Click += delegate { if (current != null) ShowInExplorer(current, false); };
            cbDrive.SelectedIndexChanged += delegate
            {
                var d = cbDrive.SelectedItem as DriveItem;
                if (d != null && (current == null || !current.StartsWith(d.Root, StringComparison.OrdinalIgnoreCase))) Navigate(d.Root);
            };

            Controls.Add(list);
            Controls.Add(lblPath);
            Controls.Add(top);
            Controls.Add(lblStatus);
            ClientSize = new Size(760, 560);
            MinimumSize = new Size(560, 400);
            Scale(new SizeF(UiFont.Scale, UiFont.Scale));

            // aggiornamento dell'elenco al massimo 4 volte al secondo mentre arrivano le dimensioni
            uiTimer = new System.Windows.Forms.Timer { Interval = 250 };
            uiTimer.Tick += delegate { if (dirty) { dirty = false; Fill(); } UpdateStatus(); };
            uiTimer.Start();

            Load += delegate
            {
                var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                Size = new Size(Math.Min(Width, wa.Width * 95 / 100), Math.Min(Height, wa.Height * 95 / 100));
                Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
                FitColumns();
                LoadDrives();
                Navigate(Root(drive ?? (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:")));
            };
        }

        class DriveItem
        {
            public string Root, Text;
            public override string ToString() { return Text; }
        }

        void LoadDrives()
        {
            cbDrive.Items.Clear();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (!d.IsReady || (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable)) continue;
                    string label = string.IsNullOrEmpty(d.VolumeLabel) ? "" : " " + d.VolumeLabel;
                    cbDrive.Items.Add(new DriveItem
                    {
                        Root = d.RootDirectory.FullName,
                        Text = d.Name.TrimEnd('\\') + label + " · " + L.F("{0} liberi di {1}", "{0} free of {1}", Fmt.Bytes(d.TotalFreeSpace), Fmt.Bytes(d.TotalSize))
                    });
                }
            }
            catch { }
        }

        void FitColumns()
        {
            int w = list.ClientSize.Width;
            if (w <= 0 || list.Columns.Count < 4) return;
            list.Columns[1].Width = (int)(w * 0.14);
            list.Columns[2].Width = (int)(w * 0.26);
            list.Columns[3].Width = (int)(w * 0.18);
            list.Columns[0].Width = w - list.Columns[1].Width - list.Columns[2].Width - list.Columns[3].Width - 2;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (cts != null) cts.Cancel();
            uiTimer.Stop();
            base.OnFormClosing(e);
        }

        // ---------------- navigazione ----------------

        void Navigate(string path)
        {
            if (cts != null) cts.Cancel();
            current = path;
            lblPath.Text = path;
            btnUp.Enabled = Directory.GetParent(path) != null;
            foreach (DriveItem d in cbDrive.Items)
                if (path.StartsWith(d.Root, StringComparison.OrdinalIgnoreCase) && cbDrive.SelectedItem != d) { cbDrive.SelectedItem = d; break; }
            Reload(false);
        }

        void GoUp()
        {
            if (current == null) return;
            var parent = Directory.GetParent(current.TrimEnd('\\') + (current.Length <= 3 ? "\\" : ""));
            if (parent != null) Navigate(parent.FullName);
        }

        Entry Selected()
        {
            return list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as Entry : null;
        }

        void OpenSelected()
        {
            var e = Selected();
            if (e == null || e.Others) return;
            if (e.IsDir) Navigate(e.Path);
            else ShowInExplorer(e.Path, true);
        }

        static void ShowInExplorer(string path, bool select)
        {
            try { Process.Start("explorer.exe", select ? "/select,\"" + path + "\"" : "\"" + path + "\""); } catch { }
        }

        // Elenca la cartella e calcola in background le dimensioni delle sottocartelle che mancano.
        // force: ricalcola anche quelle già note (pulsante Aggiorna); azzera anche il conteggio delle cartelle negate
        void Reload(bool force)
        {
            if (cts != null) cts.Cancel();
            loadError = false;
            string dir = current;
            if (force)
                lock (cache)
                    foreach (var k in cache.Keys.Where(k => k.StartsWith(dir, StringComparison.OrdinalIgnoreCase)).ToList()) cache.Remove(k);

            var items = new List<Entry>();
            var files = new List<Entry>();
            try
            {
                var di = new DirectoryInfo(dir);
                foreach (var d in di.EnumerateDirectories())
                {
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var en = new Entry { Name = d.Name, Path = d.FullName, IsDir = true, Modified = SafeTime(d) };
                    long size;
                    lock (cache) if (cache.TryGetValue(en.Path, out size)) { en.Size = size; en.Done = true; }
                    items.Add(en);
                }
                foreach (var f in di.EnumerateFiles())
                {
                    if ((f.Attributes & CloudOnly) != 0) continue;
                    files.Add(new Entry { Name = f.Name, Path = f.FullName, Size = f.Length, Done = true, Modified = SafeTime(f) });
                }
            }
            catch (Exception ex)
            {
                entries = new List<Entry>();
                Fill();
                loadError = true;
                lblStatus.Text = L.T("Cartella non accessibile:", "Folder not accessible:") + " " + ex.Message;
                return;
            }
            files = files.OrderByDescending(f => f.Size).ToList();
            items.AddRange(files.Take(MaxFiles));
            if (files.Count > MaxFiles)
            {
                var rest = files.Skip(MaxFiles).ToList();
                items.Add(new Entry { Name = L.F("(altri {0} file)", "({0} more files)", rest.Count), Others = true, Done = true, Size = rest.Sum(f => f.Size), Count = rest.Count });
            }
            entries = items;
            Fill();

            var todo = items.Where(e => e.IsDir && !e.Done).ToList();
            if (todo.Count == 0) { UpdateStatus(); return; }
            var c = cts = new CancellationTokenSource();
            scannedFiles = 0;
            var t = new Thread(delegate ()
            {
                // prima le cartelle già viste altrove, poi le altre nell'ordine dell'elenco
                foreach (var e in todo)
                {
                    if (c.IsCancellationRequested) return;
                    try
                    {
                        long s = Scan(e.Path, c.Token);
                        e.Size = s;
                        e.Done = true;
                        dirty = true;
                    }
                    catch (OperationCanceledException) { return; }
                }
                dirty = true;
            }) { IsBackground = true, Name = "AppSys disk space", Priority = ThreadPriority.BelowNormal };
            t.Start();
        }

        static DateTime SafeTime(FileSystemInfo f)
        {
            try { return f.LastWriteTime; } catch { return DateTime.MinValue; }
        }

        // Dimensione di una cartella con tutte le sottocartelle; ricorda il risultato di ognuna (solo se completo)
        long Scan(string dir, CancellationToken ct)
        {
            long cached;
            lock (cache) if (cache.TryGetValue(dir, out cached)) return cached;
            long total = 0;
            try
            {
                foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException();
                    var a = e.Attributes;
                    if ((a & FileAttributes.Directory) != 0)
                    {
                        if ((a & FileAttributes.ReparsePoint) == 0) total += Scan(e.FullName, ct);
                    }
                    else if ((a & CloudOnly) == 0)
                    {
                        try { total += ((FileInfo)e).Length; } catch { }
                        Interlocked.Increment(ref scannedFiles);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException) { Interlocked.Increment(ref denied); }
            catch (IOException) { Interlocked.Increment(ref denied); }
            catch (System.Security.SecurityException) { Interlocked.Increment(ref denied); }
            lock (cache) cache[dir] = total;
            return total;
        }

        // ---------------- elenco ----------------

        void Fill()
        {
            var sel = Selected();
            string selPath = sel != null ? sel.Path ?? sel.Name : null;
            var sorted = entries.OrderByDescending(e => e.Done ? e.Size : -1).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var e in sorted)
            {
                var it = new ListViewItem(new[]
                {
                    (e.IsDir ? "📁 " : e.Others ? "" : "    ") + e.Name,
                    e.Done ? Fmt.Bytes(e.Size) : "…",
                    "",
                    e.Others || e.Modified == DateTime.MinValue ? "" : e.Modified.ToString("g")
                }) { Tag = e, ToolTipText = e.Path ?? e.Name };
                if (e.Others) it.ForeColor = Color.DimGray;
                list.Items.Add(it);
                if (selPath != null && (e.Path ?? e.Name) == selPath) it.Selected = true;
            }
            list.EndUpdate();
        }

        void DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ColumnIndex != 2) { e.DrawDefault = true; return; }
            var en = e.Item.Tag as Entry;
            e.DrawBackground();
            if (e.Item.Selected) e.Graphics.FillRectangle(SystemBrushes.Highlight, e.Bounds);
            long total = entries.Where(x => x.Done).Sum(x => x.Size);
            if (en == null || !en.Done || total <= 0) return;
            double share = (double)en.Size / total;
            var r = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + e.Bounds.Height / 2 - 4, Math.Max(10, e.Bounds.Width - 56), 8);
            using (var b = new SolidBrush(barTrack)) e.Graphics.FillRectangle(b, r);
            int w = (int)Math.Round(r.Width * share);
            if (w > 0) using (var b = new SolidBrush(en.Others ? Color.Gray : barColor)) e.Graphics.FillRectangle(b, r.X, r.Y, w, r.Height);
            string pct = (share * 100).ToString(share < 0.1 ? "0.0" : "0") + "%";
            TextRenderer.DrawText(e.Graphics, pct, list.Font, new Rectangle(r.Right + 4, e.Bounds.Y, e.Bounds.Right - r.Right - 4, e.Bounds.Height),
                e.Item.Selected ? SystemColors.HighlightText : SystemColors.WindowText, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
        }

        void UpdateStatus()
        {
            if (current == null || loadError) return;
            int left = entries.Count(e => e.IsDir && !e.Done);
            long total = entries.Where(e => e.Done).Sum(e => e.Size);
            string s;
            if (left > 0)
                s = L.F("Calcolo in corso: {0} cartelle da completare, {1} file letti finora...", "Calculating: {0} folders to go, {1} files read so far...", left, Interlocked.Read(ref scannedFiles).ToString("N0"));
            else
            {
                s = L.F("Totale: {0}", "Total: {0}", Fmt.Bytes(total));
                if (denied > 0) s += " · " + L.T("alcune cartelle non sono accessibili senza permessi di amministratore: le dimensioni sono minime", "some folders are not accessible without administrator rights: sizes are minimums");
            }
            if (lblStatus.Text != s) lblStatus.Text = s;
        }
    }
}
