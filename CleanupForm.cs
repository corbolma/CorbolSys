// Pulizia disco: categorie di file che si possono eliminare senza rischi e senza permessi di amministratore
// (file temporanei dell'utente, Cestino, cache di miniature, DirectX, browser e Internet, segnalazioni
// errori, dump dei crash). Si vede quanto occupa ognuna, si scelgono e si conferma: i file in uso vengono
// saltati. Per i file di sistema (aggiornamenti di Windows...) apre la Pulizia disco di Windows.
// Sicurezza: si cancella solo dentro %LOCALAPPDATA% e la cartella temporanea dell'utente, mai seguendo
// collegamenti (junction, link simbolici).
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AppSys
{
    class CleanTarget
    {
        public string Dir;
        public string[] Patterns = { "*" };
        public bool Recursive = true;
        public TimeSpan MinAge = TimeSpan.Zero;   // solo file non modificati da almeno questo tempo
    }

    class CleanCategory
    {
        public string Id, Title, Description;
        public bool DefaultOn, RecycleBin;
        public Func<List<CleanTarget>> Targets;
        public long Size = -1;                    // -1 = non ancora misurata
        public int Files;
        public ListViewItem Item;
    }

    class CleanResult { public long Freed; public int Deleted, Skipped; }

    static class Cleaner
    {
        static string LocalAppData { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        static string LA(params string[] parts) { return Path.Combine(new[] { LocalAppData }.Concat(parts).ToArray()); }

        public static List<CleanCategory> Categories()
        {
            return new List<CleanCategory>
            {
                new CleanCategory
                {
                    Id = "temp", DefaultOn = true, Title = L.T("File temporanei dell'utente", "User temporary files"),
                    Description = L.T("File lasciati dai programmi nella cartella temporanea. Si eliminano solo quelli non modificati da almeno 24 ore, per non disturbare installazioni e programmi aperti.",
                                      "Files left by programs in the temporary folder. Only files not modified for at least 24 hours are deleted, so installers and open programs are not disturbed."),
                    Targets = () => new List<CleanTarget> { new CleanTarget { Dir = Path.GetTempPath(), MinAge = TimeSpan.FromHours(24) } }
                },
                new CleanCategory
                {
                    Id = "thumbs", DefaultOn = true, Title = L.T("Cache delle miniature", "Thumbnail cache"),
                    Description = L.T("Anteprime di immagini e video mostrate da Esplora file: Windows le ricrea quando servono. Quelle in uso restano.",
                                      "Image and video previews shown by File Explorer: Windows recreates them when needed. Those in use are kept."),
                    Targets = () => new List<CleanTarget> { new CleanTarget { Dir = LA("Microsoft", "Windows", "Explorer"), Patterns = new[] { "thumbcache_*.db", "iconcache_*.db" }, Recursive = false } }
                },
                new CleanCategory
                {
                    Id = "dx", DefaultOn = true, Title = L.T("Cache degli shader (DirectX, scheda video)", "Shader cache (DirectX, graphics card)"),
                    Description = L.T("File creati dalla scheda video per giochi e programmi: vengono ricreati, al massimo il primo avvio di un gioco è un po' più lento.",
                                      "Files created by the graphics card for games and programs: they are recreated; at most the first start of a game is a bit slower."),
                    Targets = () => new List<CleanTarget>
                    {
                        new CleanTarget { Dir = LA("D3DSCache") }, new CleanTarget { Dir = LA("NVIDIA", "DXCache") },
                        new CleanTarget { Dir = LA("NVIDIA", "GLCache") }, new CleanTarget { Dir = LA("AMD", "DxCache") }, new CleanTarget { Dir = LA("AMD", "GLCache") }
                    }
                },
                new CleanCategory
                {
                    Id = "inet", DefaultOn = true, Title = L.T("File Internet temporanei", "Temporary Internet files"),
                    Description = L.T("Cache web usata da Windows e dai programmi che mostrano pagine web al loro interno.",
                                      "Web cache used by Windows and by programs that show web pages inside them."),
                    Targets = () => new List<CleanTarget> { new CleanTarget { Dir = LA("Microsoft", "Windows", "INetCache") } }
                },
                new CleanCategory
                {
                    Id = "wer", DefaultOn = true, Title = L.T("Segnalazioni errori di Windows", "Windows error reports"),
                    Description = L.T("Rapporti sui programmi che si sono bloccati, già inviati o in attesa. Servono solo a Microsoft e ai produttori.",
                                      "Reports about programs that crashed, already sent or queued. They are only useful to Microsoft and vendors."),
                    Targets = () => new List<CleanTarget> { new CleanTarget { Dir = LA("Microsoft", "Windows", "WER") } }
                },
                new CleanCategory
                {
                    Id = "dumps", DefaultOn = true, Title = L.T("Dump dei programmi bloccati", "Crash dumps"),
                    Description = L.T("Copie della memoria dei programmi al momento del blocco: servono solo agli sviluppatori.",
                                      "Memory copies of programs taken when they crashed: only useful to developers."),
                    Targets = () => new List<CleanTarget> { new CleanTarget { Dir = LA("CrashDumps") } }
                },
                new CleanCategory
                {
                    Id = "browsers", DefaultOn = false, Title = L.T("Cache dei browser (Edge, Chrome, Firefox, Brave)", "Browser cache (Edge, Chrome, Firefox, Brave)"),
                    Description = L.T("Copie di pagine e immagini dei siti visitati. Non tocca password, cronologia, cookie né preferiti. I siti si ricaricano un po' più lentamente la prima volta. Chiudi i browser prima: i file in uso vengono saltati.",
                                      "Copies of pages and images of visited sites. Passwords, history, cookies and favorites are not touched. Sites load a bit slower the first time. Close the browsers first: files in use are skipped."),
                    Targets = BrowserTargets
                },
                new CleanCategory
                {
                    Id = "recycle", DefaultOn = false, RecycleBin = true, Title = L.T("Cestino", "Recycle Bin"),
                    Description = L.T("I file che hai eliminato: svuotandolo non si possono più recuperare. Controlla il Cestino prima di spuntare questa voce.",
                                      "The files you deleted: once emptied they can no longer be recovered. Check the Recycle Bin before ticking this item."),
                    Targets = () => new List<CleanTarget>()
                }
            };
        }

        // Cartelle di cache di ogni profilo dei browser basati su Chromium e di Firefox
        static List<CleanTarget> BrowserTargets()
        {
            var res = new List<CleanTarget>();
            foreach (var ud in new[] { LA("Microsoft", "Edge", "User Data"), LA("Google", "Chrome", "User Data"), LA("BraveSoftware", "Brave-Browser", "User Data") })
            {
                if (!Directory.Exists(ud)) continue;
                try
                {
                    foreach (var prof in Directory.GetDirectories(ud))
                        foreach (var c in new[] { "Cache", "Code Cache", "GPUCache" })
                        {
                            string d = Path.Combine(prof, c);
                            if (Directory.Exists(d)) res.Add(new CleanTarget { Dir = d });
                        }
                }
                catch { }
            }
            string ff = LA("Mozilla", "Firefox", "Profiles");
            if (Directory.Exists(ff))
                try { foreach (var prof in Directory.GetDirectories(ff)) if (Directory.Exists(Path.Combine(prof, "cache2"))) res.Add(new CleanTarget { Dir = Path.Combine(prof, "cache2") }); }
                catch { }
            return res;
        }

        // Seconda barriera, indipendente dalle categorie: si cancella solo nella cartella temporanea dell'utente
        // e in un elenco chiuso di cartelle di cache dentro %LOCALAPPDATA% (mai altrove, nemmeno altre cartelle
        // di %LOCALAPPDATA% come i programmi installati per utente)
        static readonly string[] AllowedUnderLocal =
        {
            @"Microsoft\Windows\Explorer", @"Microsoft\Windows\INetCache", @"Microsoft\Windows\WER", "CrashDumps",
            "D3DSCache", @"NVIDIA\DXCache", @"NVIDIA\GLCache", @"AMD\DxCache", @"AMD\GLCache"
        };
        static readonly string[] BrowserUserData = { @"Microsoft\Edge\User Data", @"Google\Chrome\User Data", @"BraveSoftware\Brave-Browser\User Data" };
        static readonly string[] BrowserCacheNames = { "Cache", "Code Cache", "GPUCache" };

        static bool Under(string full, string root)
        {
            string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            return r.Length > 4 && full.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }

        static bool Allowed(string dir)
        {
            try
            {
                string full = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
                string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
                if (full.Equals(temp, StringComparison.OrdinalIgnoreCase) || Under(full, temp)) return true;
                if (AllowedUnderLocal.Any(rel => full.Equals(LA(rel).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || Under(full, LA(rel)))) return true;
                // cache dei browser: <User Data>\<profilo>\Cache (o Code Cache, GPUCache) e Firefox <profilo>\cache2
                string leaf = Path.GetFileName(full.TrimEnd('\\'));
                string parent = Path.GetDirectoryName(Path.GetDirectoryName(full.TrimEnd('\\')) ?? "") ?? "";
                if (BrowserCacheNames.Contains(leaf, StringComparer.OrdinalIgnoreCase)
                    && BrowserUserData.Any(u => parent.Equals(LA(u), StringComparison.OrdinalIgnoreCase))) return true;
                if (leaf.Equals("cache2", StringComparison.OrdinalIgnoreCase) && parent.Equals(LA("Mozilla", "Firefox", "Profiles"), StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        // File della categoria (dimensione e quanti), senza eliminare nulla
        public static void Measure(CleanCategory c, CancellationToken ct)
        {
            if (c.RecycleBin)
            {
                var i = new SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO)) };
                c.Size = SHQueryRecycleBin(null, ref i) == 0 ? i.i64Size : 0;
                c.Files = (int)Math.Min(int.MaxValue, Math.Max(0, i.i64NumItems));
                return;
            }
            long size = 0; int n = 0;
            foreach (var t in c.Targets())
                foreach (var f in Files(t, ct)) { size += f.Length; n++; }
            c.Size = size; c.Files = n;
        }

        public static CleanResult Clean(CleanCategory c, CancellationToken ct)
        {
            var r = new CleanResult();
            if (c.RecycleBin)
            {
                Measure(c, ct);
                long before = c.Size;
                // SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND
                if (SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4) == 0) { r.Freed = before; r.Deleted = c.Files; }
                else r.Skipped = c.Files;
                return r;
            }
            foreach (var t in c.Targets())
            {
                if (!Allowed(t.Dir)) continue;
                foreach (var f in Files(t, ct).ToList())
                {
                    if (ct.IsCancellationRequested) return r;
                    try
                    {
                        long len = f.Length;
                        if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
                        f.Delete();
                        r.Freed += len; r.Deleted++;
                    }
                    catch { r.Skipped++; }   // in uso o protetto
                }
                if (t.Recursive) RemoveEmptyDirs(t.Dir);
            }
            return r;
        }

        static IEnumerable<FileInfo> Files(CleanTarget t, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(t.Dir) || !Directory.Exists(t.Dir) || !Allowed(t.Dir)) yield break;
            DateTime limit = DateTime.Now - t.MinAge;
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(t.Dir));
            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) yield break;
                var d = stack.Pop();
                var found = new List<FileInfo>();
                try
                {
                    foreach (var p in t.Patterns)
                        foreach (var f in d.EnumerateFiles(p))
                            if ((f.Attributes & FileAttributes.ReparsePoint) == 0 && f.LastWriteTime <= limit) found.Add(f);
                    if (t.Recursive)
                        foreach (var s in d.EnumerateDirectories())
                            if ((s.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(s);
                }
                catch { }
                foreach (var f in found) yield return f;
            }
        }

        // Toglie le sottocartelle rimaste vuote (non la cartella di partenza)
        static void RemoveEmptyDirs(string root)
        {
            try
            {
                foreach (var d in new DirectoryInfo(root).EnumerateDirectories())
                {
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    RemoveEmptyDirs(d.FullName);
                    try { if (!d.EnumerateFileSystemInfos().Any()) d.Delete(); } catch { }
                }
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SHQUERYRBINFO { public int cbSize; public long i64Size, i64NumItems; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBin(string root, ref SHQUERYRBINFO info);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);
    }

    class CleanupForm : Form
    {
        static CleanupForm open;
        public static CleanupForm Current { get { return open != null && !open.IsDisposed ? open : null; } }
        public bool Busy { get { return busy || !measured; } }   // anche prima della prima misurazione
        public void SelectFirst() { if (list.Items.Count > 0) list.Items[0].Selected = true; }

        public static void ShowOnce(Icon icon)
        {
            if (open == null || open.IsDisposed) open = new CleanupForm(icon);
            open.Show();
            if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal;
            open.Activate();
        }

        readonly ListView list;
        readonly Label lblDesc, lblTotal, lblStatus;
        readonly Button btnClean, btnClose;
        readonly List<CleanCategory> cats = Cleaner.Categories();
        readonly Icon icon;
        CancellationTokenSource cts;
        bool busy, measured, ready;   // ready: finestra comparsa (prima le spunte non sono ancora leggibili)

        CleanupForm(Icon icon)
        {
            this.icon = icon;
            Text = L.T("Pulizia disco - AppSys", "Disk cleanup - AppSys");
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", UiFont.Pt);
            BackColor = Color.White;
            if (icon != null) Icon = icon;

            var intro = new Label
            {
                Text = L.T("Scegli cosa eliminare. Sono file che Windows e i programmi ricreano da soli, oppure che non servono più. I file in uso vengono saltati.",
                           "Choose what to delete. These are files that Windows and programs recreate by themselves, or that are no longer needed. Files in use are skipped."),
                Location = new Point(16, 12), Size = new Size(528, 40)
            };
            list = new ListView
            {
                View = View.Details, CheckBoxes = true, FullRowSelect = true, MultiSelect = false, HideSelection = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable, Location = new Point(16, 58), Size = new Size(528, 230)
            };
            list.Columns.Add(L.T("Categoria", "Category"), 390);
            list.Columns.Add(L.T("Dimensione", "Size"), 120, HorizontalAlignment.Right);
            foreach (var c in cats)
            {
                c.Item = new ListViewItem(new[] { c.Title, "…" }) { Checked = c.DefaultOn, Tag = c };
                list.Items.Add(c.Item);
            }
            // alla creazione dell'elenco Windows segnala le spunte mentre le voci sono ancora a metà inserimento:
            // leggerle in quel momento dà errore, quindi il totale si calcola solo a finestra comparsa
            list.ItemChecked += delegate { if (ready) UpdateTotal(); };
            list.SelectedIndexChanged += delegate { var c = Sel(); if (c != null) lblDesc.Text = c.Description; };

            lblDesc = new Label { Location = new Point(16, 296), Size = new Size(528, 62), ForeColor = Color.DimGray,
                                  Text = L.T("Seleziona una voce per vedere cosa contiene.", "Select an item to see what it contains.") };
            lblTotal = new Label { Location = new Point(16, 362), Size = new Size(528, 24), Font = new Font("Segoe UI Semibold", UiFont.Pt + 1f) };
            lblStatus = new Label { Location = new Point(16, 388), Size = new Size(528, 40), ForeColor = Color.DimGray };

            var links = new FlowLayoutPanel { Location = new Point(12, 430), Size = new Size(536, 28), WrapContents = false };
            links.Controls.Add(Link(L.T("Pulizia file di sistema di Windows...", "Windows system file cleanup..."), delegate { Start("cleanmgr.exe", "/d " + (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd(':', '\\')); }));
            links.Controls.Add(Link(L.T("Sensore memoria", "Storage Sense"), delegate { Start("ms-settings:storagesense", ""); }));
            links.Controls.Add(Link(L.T("Spazio su disco...", "Disk space..."), delegate { DiskSpaceForm.ShowOnce(this.icon, null); }));

            btnClean = new Button { Text = L.T("Pulisci", "Clean up"), Location = new Point(338, 470), Size = new Size(100, 30), Enabled = false };
            btnClean.Font = new Font(Font, FontStyle.Bold);
            btnClose = new Button { Text = L.T("Chiudi", "Close"), Location = new Point(444, 470), Size = new Size(100, 30) };
            btnClean.Click += delegate { CleanSelected(); };
            btnClose.Click += delegate { Close(); };
            CancelButton = btnClose;

            Controls.AddRange(new Control[] { intro, list, lblDesc, lblTotal, lblStatus, links, btnClean, btnClose });
            ClientSize = new Size(560, 514);
            Scale(new SizeF(UiFont.Scale, UiFont.Scale));
            Shown += delegate
            {
                int w = list.ClientSize.Width;
                list.Columns[1].Width = (int)(w * 0.25);
                list.Columns[0].Width = w - list.Columns[1].Width - 2;
                ready = true;
                MeasureAll();
            };
        }

        static LinkText Link(string text, Action click)
        {
            return new LinkText(text, click) { Margin = new Padding(4, 4, 14, 0) };
        }

        static void Start(string exe, string args)
        {
            try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }); } catch { }
        }

        CleanCategory Sel() { return list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as CleanCategory : null; }

        void Ui(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy && cts != null)
            {
                // a pulizia in corso si interrompe dopo il file attuale
                cts.Cancel();
            }
            base.OnFormClosing(e);
        }

        // done: messaggio da lasciare alla fine (il risultato della pulizia), null = nessuno
        void MeasureAll(string done = null)
        {
            busy = true;
            btnClean.Enabled = false;
            lblStatus.Text = L.T("Calcolo dello spazio in corso...", "Calculating space...");
            var c = cts = new CancellationTokenSource();
            var t = new Thread(delegate ()
            {
                foreach (var cat in cats)
                {
                    if (c.IsCancellationRequested) return;
                    try { Cleaner.Measure(cat, c.Token); } catch { cat.Size = 0; }
                    var cc = cat;
                    Ui(delegate { cc.Item.SubItems[1].Text = Fmt.Bytes(cc.Size); UpdateTotal(); });
                }
                Ui(delegate { busy = false; measured = true; lblStatus.Text = done ?? ""; UpdateTotal(); });
            }) { IsBackground = true, Name = "AppSys cleanup measure", Priority = ThreadPriority.BelowNormal };
            t.Start();
        }

        List<CleanCategory> Checked() { return cats.Where(c => c.Item.Checked).ToList(); }

        void UpdateTotal()
        {
            var sel = Checked();
            long total = sel.Where(c => c.Size > 0).Sum(c => c.Size);
            lblTotal.Text = L.F("Spazio da liberare: {0}", "Space to free: {0}", Fmt.Bytes(total));
            btnClean.Enabled = !busy && sel.Count > 0 && total > 0;
        }

        void CleanSelected()
        {
            var sel = Checked().Where(c => c.Size > 0).ToList();
            if (sel.Count == 0) return;
            var sb = new StringBuilder(L.T("Verranno eliminati definitivamente:", "The following will be permanently deleted:") + "\n\n");
            foreach (var c in sel) sb.AppendLine("• " + c.Title + ": " + Fmt.Bytes(c.Size));
            sb.AppendLine().Append(L.F("Totale: {0}. Continuare?", "Total: {0}. Continue?", Fmt.Bytes(sel.Sum(c => c.Size))));
            if (sel.Any(c => c.Id == "browsers") && Process.GetProcesses().Any(p => { try { var n = p.ProcessName.ToLowerInvariant(); return n == "msedge" || n == "chrome" || n == "firefox" || n == "brave"; } catch { return false; } }))
                sb.Append("\n\n" + L.T("Un browser è aperto: la parte della sua cache in uso verrà saltata. Per pulirla tutta chiudi prima i browser.",
                                       "A browser is open: the part of its cache in use will be skipped. To clean all of it, close the browsers first."));
            if (MessageBox.Show(this, sb.ToString(), Text, MessageBoxButtons.YesNo, sel.Any(c => c.RecycleBin) ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            busy = true;
            btnClean.Enabled = false;
            list.Enabled = false;
            var c2 = cts = new CancellationTokenSource();
            var t = new Thread(delegate ()
            {
                var total = new CleanResult();
                foreach (var cat in sel)
                {
                    if (c2.IsCancellationRequested) break;
                    var cc = cat;
                    Ui(delegate { lblStatus.Text = L.F("Pulizia: {0}...", "Cleaning: {0}...", cc.Title); });
                    CleanResult r;
                    try { r = Cleaner.Clean(cat, c2.Token); } catch { r = new CleanResult(); }
                    total.Freed += r.Freed; total.Deleted += r.Deleted; total.Skipped += r.Skipped;
                }
                Ui(delegate
                {
                    list.Enabled = true;
                    string done = L.F("Liberati {0} ({1} file eliminati", "Freed {0} ({1} files deleted", Fmt.Bytes(total.Freed), total.Deleted.ToString("N0"))
                                + (total.Skipped > 0 ? L.F(", {0} saltati perché in uso o protetti).", ", {0} skipped because in use or protected).", total.Skipped.ToString("N0")) : ").");
                    MeasureAll(done);
                });
            }) { IsBackground = true, Name = "AppSys cleanup" };
            t.Start();
        }
    }
}
