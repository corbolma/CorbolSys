// Analisi approfondita del PC: una serie di controlli (sistema, CPU, memoria, spazio e salute dei dischi,
// errori nel registro eventi, arresti anomali, temperatura, batteria, avvio automatico, aggiornamenti,
// antivirus, dispositivi, file temporanei, affidabilità) con una conclusione a regole e un punteggio.
// Tutto in sola lettura, senza permessi di amministratore (quando un dato li richiede si dice).
// Il testo del report è quello che si copia e che riceve l'assistente AI.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace AppSys
{
    enum CheckState { Wait, Running, Ok, Info, Warn, Error }

    class SysCheck
    {
        public string Id, Title, Detail = "";
        public CheckState State = CheckState.Wait;
        public string Advice = "";                                 // cosa fare (a regole), vuoto se va tutto bene
        public readonly List<string> Extra = new List<string>();  // dettagli per il report e l'assistente
        public object Tag;                                          // usato dall'interfaccia
    }

    class Analysis
    {
        public readonly List<SysCheck> Checks = new List<SysCheck>();
        public DateTime Time = DateTime.Now;
        public int Score = 100;
        public CheckState Overall = CheckState.Ok;
        public string ConclusionTitle = "", ConclusionText = "";
        public string Host = Environment.MachineName, Domain = "";

        // Chiamato dal thread di lavoro a ogni cambiamento di un controllo
        public Action<SysCheck> Changed;
        public Func<bool> Cancelled = () => false;

        public Analysis()
        {
            // titoli scritti per esteso in L.T: così lang-check.ps1 li trova
            Add("system", L.T("Sistema", "System"));
            Add("cpu", L.T("Processore", "Processor"));
            Add("memory", L.T("Memoria", "Memory"));
            Add("space", L.T("Spazio su disco", "Disk space"));
            Add("health", L.T("Salute dei dischi", "Disk health"));
            Add("diskerr", L.T("Errori dei dischi", "Disk errors"));
            Add("crash", L.T("Arresti anomali", "Crashes"));
            Add("whea", L.T("Errori hardware", "Hardware errors"));
            Add("apps", L.T("Programmi bloccati", "Crashing programs"));
            Add("temp", L.T("Temperatura", "Temperature"));
            Add("battery", L.T("Batteria", "Battery"));
            Add("startup", L.T("Avvio automatico", "Startup programs"));
            Add("updates", L.T("Aggiornamenti di Windows", "Windows Update"));
            Add("av", L.T("Antivirus", "Antivirus"));
            Add("devices", L.T("Dispositivi e driver", "Devices and drivers"));
            Add("junk", L.T("File temporanei", "Temporary files"));
            Add("reliability", L.T("Indice di affidabilità", "Reliability index"));
        }

        void Add(string id, string title) { Checks.Add(new SysCheck { Id = id, Title = title }); }

        public SysCheck Get(string id) { return Checks.First(c => c.Id == id); }

        void Set(SysCheck c, CheckState st, string detail, string advice = null)
        {
            c.State = st;
            c.Detail = detail ?? "";
            if (advice != null) c.Advice = advice;
            var h = Changed;
            if (h != null) h(c);
        }

        // ---------------- esecuzione ----------------

        public void Run()
        {
            try { Domain = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName ?? ""; } catch { }
            var steps = new List<KeyValuePair<string, Action<SysCheck>>>
            {
                Step("system", CheckSystem), Step("cpu", CheckCpu), Step("memory", CheckMemory), Step("space", CheckSpace),
                Step("health", CheckHealth), Step("diskerr", CheckDiskErrors), Step("crash", CheckCrashes), Step("whea", CheckWhea),
                Step("apps", CheckApps), Step("temp", CheckTemp), Step("battery", CheckBattery), Step("startup", CheckStartup),
                Step("updates", CheckUpdates), Step("av", CheckAntivirus), Step("devices", CheckDevices), Step("junk", CheckJunk),
                Step("reliability", CheckReliability)
            };
            foreach (var st in steps)
            {
                if (Cancelled()) return;
                var c = Get(st.Key);
                Set(c, CheckState.Running, L.T("controllo in corso...", "checking..."));
                try { st.Value(c); }
                catch (UnauthorizedAccessException) { Set(c, CheckState.Info, L.T("non disponibile senza permessi di amministratore", "not available without administrator rights")); }
                catch (Exception ex) { Set(c, CheckState.Info, L.T("non disponibile", "not available") + " (" + Short(ex.Message, 80) + ")"); }
                if (c.State == CheckState.Running) Set(c, CheckState.Info, L.T("nessun dato", "no data"));
            }
            Conclude();
        }

        static KeyValuePair<string, Action<SysCheck>> Step(string id, Action<SysCheck> a) { return new KeyValuePair<string, Action<SysCheck>>(id, a); }

        // ---------------- controlli ----------------

        void CheckSystem(SysCheck c)
        {
            var s = SysMonitor.Instance.Current;
            string os = "", build = "", model = "";
            DateTime installed = DateTime.MinValue;
            foreach (var o in Wmi(@"root\cimv2", "SELECT Caption, Version, BuildNumber, InstallDate, OSArchitecture FROM Win32_OperatingSystem"))
            {
                os = (Str(o, "Caption") + " " + Str(o, "OSArchitecture")).Trim();
                build = Str(o, "Version");
                installed = Dmtf(Str(o, "InstallDate"));
            }
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    if (k != null)
                    {
                        string disp = k.GetValue("DisplayVersion") as string;
                        object ubr = k.GetValue("UBR");
                        if (!string.IsNullOrEmpty(disp)) build = disp + " (build " + k.GetValue("CurrentBuildNumber") + (ubr != null ? "." + ubr : "") + ")";
                    }
            }
            catch { }
            foreach (var o in Wmi(@"root\cimv2", "SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
                model = (Str(o, "Manufacturer") + " " + Str(o, "Model")).Trim();
            c.Extra.Add("Windows: " + os + " " + build);
            if (model.Length > 0) c.Extra.Add(L.T("Modello", "Model") + ": " + model);
            c.Extra.Add("CPU: " + SysMonitor.CpuName + ", " + SysMonitor.Cores + " " + L.T("processori logici", "logical processors"));
            c.Extra.Add("RAM: " + Fmt.Bytes(s.RamTotal));
            foreach (var o in Wmi(@"root\cimv2", "SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController"))
            {
                var dd = Dmtf(Str(o, "DriverDate"));
                c.Extra.Add("GPU: " + Str(o, "Name") + ", driver " + Str(o, "DriverVersion") + (dd > DateTime.MinValue ? " (" + dd.ToString("yyyy-MM-dd") + ")" : ""));
            }
            if (installed > DateTime.MinValue) c.Extra.Add(L.T("Windows installato il", "Windows installed on") + " " + installed.ToString("yyyy-MM-dd"));
            c.Extra.Add(L.T("Acceso da", "Up for") + " " + Fmt.Duration(s.Uptime) + " (" + L.T("ultimo avvio", "last boot") + " " + (DateTime.Now - s.Uptime).ToString("yyyy-MM-dd HH:mm") + ")");
            c.Extra.Add(L.F("{0} processi, {1} thread, {2} handle", "{0} processes, {1} threads, {2} handles", s.ProcessCount, s.ThreadCount, s.HandleCount));

            string detail = os.Replace("Microsoft ", "") + ", " + L.T("acceso da", "up for") + " " + Fmt.Duration(s.Uptime);
            if (s.Uptime.TotalDays >= 14)
                Set(c, CheckState.Warn, detail, L.T("Il PC non si riavvia da più di due settimane: usa Riavvia (non Arresta, che con l'avvio rapido non azzera il sistema).",
                                                   "The PC has not restarted for over two weeks: use Restart (not Shut down, which with fast startup does not reset the system)."));
            else Set(c, CheckState.Ok, detail);
        }

        void CheckCpu(SysCheck c)
        {
            var hist = SysMonitor.Instance.History();
            double now = hist.Count > 0 ? hist.Average(h => h.Cpu) : SysMonitor.Instance.Current.Cpu;
            var hour = SysMonitor.Instance.Minutes(1);
            var day = SysMonitor.Instance.Minutes(24);
            var top = SysMonitor.Instance.Current.Procs.OrderByDescending(p => p.Cpu).Take(6).Where(p => p.Cpu >= 0.5).ToList();
            c.Extra.Add(L.F("Media ultimo minuto: {0}", "Last minute average: {0}", Fmt.Pct(now)));
            if (hour.Count > 0) c.Extra.Add(L.F("Ultima ora: media {0}, massimo {1}", "Last hour: average {0}, peak {1}", Fmt.Pct(hour.Average(m => m.CpuAvg)), Fmt.Pct(hour.Max(m => m.CpuMax))));
            if (day.Count > 30)
            {
                int busy = day.Count(m => m.CpuAvg >= 80);
                c.Extra.Add(L.F("Ultime 24 ore ({0} min registrati): media {1}, minuti sopra l'80%: {2}", "Last 24 hours ({0} min recorded): average {1}, minutes above 80%: {2}",
                                day.Count, Fmt.Pct(day.Average(m => m.CpuAvg)), busy));
            }
            if (top.Count > 0) c.Extra.Add(L.T("Processi che usano più CPU ora", "Processes using most CPU now") + ": " + string.Join(", ", top.Select(p => p.Name + (p.Count > 1 ? " (×" + p.Count + ")" : "") + " " + p.Cpu.ToString("0.0") + "%")));

            string topName = top.Count > 0 ? top[0].Name : "";
            string detail = L.F("ora {0}", "now {0}", Fmt.Pct(now)) + (hour.Count > 0 ? ", " + L.F("media ultima ora {0}", "last hour average {0}", Fmt.Pct(hour.Average(m => m.CpuAvg))) : "");
            double hourAvg = hour.Count >= 10 ? hour.Average(m => m.CpuAvg) : now;
            if (hourAvg >= 85 || now >= 95)
                Set(c, CheckState.Error, detail, L.F("Il processore è quasi sempre al massimo: il PC risulta lento. Il più pesante è {0}: chiudilo o verifica cosa sta facendo.",
                                                    "The processor is almost always maxed out: the PC feels slow. The heaviest is {0}: close it or check what it is doing.", topName));
            else if (hourAvg >= 60 || now >= 80)
                Set(c, CheckState.Warn, detail, L.F("Il processore è molto impegnato, soprattutto da {0}.", "The processor is very busy, mostly by {0}.", topName));
            else Set(c, CheckState.Ok, detail);
        }

        void CheckMemory(SysCheck c)
        {
            var s = SysMonitor.Instance.Current;
            var top = s.Procs.OrderByDescending(p => p.Private).Take(6).ToList();
            c.Extra.Add(L.F("RAM in uso: {0} di {1} ({2})", "RAM in use: {0} of {1} ({2})", Fmt.Bytes(s.RamTotal - s.RamAvail), Fmt.Bytes(s.RamTotal), Fmt.Pct(s.RamPct)));
            c.Extra.Add(L.F("Memoria impegnata: {0} di {1} ({2})", "Committed memory: {0} of {1} ({2})", Fmt.Bytes(s.CommitTotal), Fmt.Bytes(s.CommitLimit), Fmt.Pct(s.CommitPct)));
            foreach (var o in Wmi(@"root\cimv2", "SELECT Name, AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage"))
                c.Extra.Add(L.F("File di paging {0}: {1} MB, in uso {2} MB, picco {3} MB", "Page file {0}: {1} MB, in use {2} MB, peak {3} MB",
                                Str(o, "Name"), Str(o, "AllocatedBaseSize"), Str(o, "CurrentUsage"), Str(o, "PeakUsage")));
            var day = SysMonitor.Instance.Minutes(24);
            if (day.Count > 30) c.Extra.Add(L.F("Ultime 24 ore: media {0}, massimo {1}", "Last 24 hours: average {0}, peak {1}", Fmt.Pct(day.Average(m => m.RamAvg)), Fmt.Pct(day.Max(m => m.RamAvg))));
            if (top.Count > 0) c.Extra.Add(L.T("Processi che usano più memoria", "Processes using most memory") + ": " + string.Join(", ", top.Select(p => p.Name + (p.Count > 1 ? " (×" + p.Count + ")" : "") + " " + Fmt.Bytes(p.Private))));

            string detail = L.F("{0} in uso di {1}", "{0} used of {1}", Fmt.Pct(s.RamPct), Fmt.Bytes(s.RamTotal));
            string topName = top.Count > 0 ? top[0].Name : "";
            bool small = s.RamTotal > 0 && s.RamTotal < (15L << 30) / 2;   // meno di ~8 GB
            if (s.RamPct >= 95 || s.CommitPct >= 92)
                Set(c, CheckState.Error, detail, L.F("La memoria è esaurita e Windows usa il disco al suo posto (molto più lento). Chiudi i programmi che non usi, a partire da {0}.",
                                                    "Memory is exhausted and Windows uses the disk instead (much slower). Close programs you don't use, starting with {0}.", topName)
                                                 + (small ? " " + L.T("Con meno di 8 GB di RAM conviene valutare un'espansione.", "With less than 8 GB of RAM an upgrade is worth considering.") : ""));
            else if (s.RamPct >= 85)
                Set(c, CheckState.Warn, detail, L.F("La memoria è quasi piena: il più pesante è {0}.", "Memory is nearly full: the heaviest is {0}.", topName)
                                                + (small ? " " + L.T("Con meno di 8 GB di RAM conviene valutare un'espansione.", "With less than 8 GB of RAM an upgrade is worth considering.") : ""));
            else Set(c, CheckState.Ok, detail);
        }

        void CheckSpace(SysCheck c)
        {
            var vols = SysMonitor.Instance.Current.Volumes;
            if (vols.Count == 0) { Set(c, CheckState.Info, L.T("nessun disco trovato", "no disks found")); return; }
            string sysDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');
            CheckState worst = CheckState.Ok;
            var parts = new List<string>();
            var low = new List<string>();
            foreach (var v in vols)
            {
                double freePct = v.Total > 0 ? 100.0 * v.Free / v.Total : 100;
                bool isSys = v.Name.Equals(sysDrive, StringComparison.OrdinalIgnoreCase);
                c.Extra.Add(L.F("{0} {1}: {2} liberi di {3} ({4} libero){5}", "{0} {1}: {2} free of {3} ({4} free){5}",
                                v.Name, (v.Label ?? "") + " " + v.Format, Fmt.Bytes(v.Free), Fmt.Bytes(v.Total), Fmt.Pct(freePct), isSys ? " - " + L.T("disco di sistema", "system drive") : ""));
                parts.Add(v.Name + " " + L.F("{0} liberi", "{0} free", Fmt.Bytes(v.Free)));
                CheckState st = CheckState.Ok;
                if (freePct < 5 || (isSys && v.Free < (5L << 30))) st = CheckState.Error;
                else if (freePct < 12 || (isSys && v.Free < (15L << 30))) st = CheckState.Warn;
                if (st != CheckState.Ok) low.Add(v.Name);
                if (st > worst) worst = st;
            }
            string advice = worst == CheckState.Ok ? "" : L.F("Poco spazio libero su {0}: svuota il Cestino e i file temporanei (Impostazioni > Sistema > Archiviazione > File temporanei) e sposta o elimina i file grandi.",
                                                              "Low free space on {0}: empty the Recycle Bin and temporary files (Settings > System > Storage > Temporary files) and move or delete large files.", string.Join(", ", low));
            Set(c, worst, string.Join(" · ", parts), advice);
        }

        void CheckHealth(SysCheck c)
        {
            var disks = Wmi(@"root\Microsoft\Windows\Storage", "SELECT ObjectId, FriendlyName, MediaType, BusType, HealthStatus, Size FROM MSFT_PhysicalDisk");
            if (disks.Count == 0)
            {
                // riserva: lo stato SMART "OK" / "Pred Fail" di Win32_DiskDrive
                foreach (var o in Wmi(@"root\cimv2", "SELECT Model, Status, Size FROM Win32_DiskDrive"))
                    c.Extra.Add(Str(o, "Model") + ": " + Str(o, "Status"));
                bool fail = c.Extra.Any(x => x.EndsWith("Pred Fail"));
                Set(c, fail ? CheckState.Error : c.Extra.Count > 0 ? CheckState.Ok : CheckState.Info,
                    fail ? L.T("un disco segnala un guasto imminente", "a disk reports an imminent failure") : c.Extra.Count > 0 ? L.T("nessun problema segnalato", "no problem reported") : L.T("non disponibile", "not available"),
                    fail ? L.T("Fai subito un backup dei dati: un disco segnala un guasto imminente.", "Back up your data now: a disk reports an imminent failure.") : null);
                return;
            }
            CheckState worst = CheckState.Ok;
            var parts = new List<string>();
            bool needAdmin = false;
            foreach (var o in disks)
            {
                string name = Str(o, "FriendlyName");
                int media = Int(o, "MediaType"), bus = Int(o, "BusType"), health = Int(o, "HealthStatus");
                string kind = media == 4 ? "SSD" : media == 3 ? "HDD" : media == 5 ? "SCM" : L.T("disco", "disk");
                string busName = bus == 17 ? "NVMe" : bus == 11 ? "SATA" : bus == 7 ? "USB" : bus == 8 ? "RAID" : bus == 10 ? "SAS" : bus == 3 ? "ATA" : "";
                string h = health == 0 ? L.T("buono", "healthy") : health == 1 ? L.T("attenzione", "warning") : health == 2 ? L.T("guasto", "unhealthy") : L.T("sconosciuto", "unknown");
                var line = new StringBuilder(name + " (" + kind + (busName.Length > 0 ? " " + busName : "") + ", " + Fmt.Bytes(Long(o, "Size")) + "): " + L.T("stato", "status") + " " + h);
                // Contatori di affidabilità (usura, temperatura, errori): di solito solo come amministratore
                try
                {
                    string id = Str(o, "ObjectId").Replace("\\", "\\\\").Replace("\"", "\\\"");
                    foreach (var r in Wmi(@"root\Microsoft\Windows\Storage", "ASSOCIATORS OF {MSFT_PhysicalDisk.ObjectId=\"" + id + "\"} WHERE ResultClass = MSFT_StorageReliabilityCounter"))
                    {
                        int wear = Int(r, "Wear"), t = Int(r, "Temperature"), tmax = Int(r, "TemperatureMax");
                        long hours = Long(r, "PowerOnHours"), rerr = Long(r, "ReadErrorsUncorrected"), werr = Long(r, "WriteErrorsUncorrected");
                        if (wear > 0) line.Append(", " + L.T("usura", "wear") + " " + wear + "%");
                        if (t > 0) line.Append(", " + t + " °C" + (tmax > 0 ? " (max " + tmax + " °C)" : ""));
                        if (hours > 0) line.Append(", " + hours + " " + L.T("ore di accensione", "power-on hours"));
                        if (rerr > 0 || werr > 0) { line.Append(", " + L.F("errori non corretti: lettura {0}, scrittura {1}", "uncorrected errors: read {0}, write {1}", rerr, werr)); if (health == 0) health = 1; }
                        if (wear >= 90 && health == 0) health = 1;
                    }
                }
                catch (ManagementException ex) { if (ex.ErrorCode == ManagementStatus.AccessDenied) needAdmin = true; }
                catch (UnauthorizedAccessException) { needAdmin = true; }
                catch { }
                c.Extra.Add(line.ToString());
                parts.Add(kind + " " + h);
                var st = health == 0 ? CheckState.Ok : health == 1 ? CheckState.Warn : health == 2 ? CheckState.Error : CheckState.Info;
                if (st > worst) worst = st;
            }
            if (needAdmin) c.Extra.Add(L.T("Usura, temperatura e ore di accensione dei dischi si leggono solo avviando AppSys come amministratore.",
                                            "Disk wear, temperature and power-on hours can only be read when AppSys runs as administrator."));
            Set(c, worst, string.Join(" · ", parts), worst >= CheckState.Warn
                ? L.T("Un disco segnala problemi: fai subito un backup dei dati importanti e valuta la sostituzione.", "A disk reports problems: back up important data now and consider replacing it.") : null);
        }

        void CheckDiskErrors(SysCheck c)
        {
            var evs = Events("System",
                "*[System[(Provider[@Name='disk'] or Provider[@Name='Ntfs'] or Provider[@Name='Microsoft-Windows-Ntfs'] or Provider[@Name='storahci'] or Provider[@Name='stornvme'] or Provider[@Name='volmgr']) and (Level=1 or Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) <= " + Days(14) + "]]]", 300);
            // Solo gli eventi che indicano davvero problemi: blocchi danneggiati, errori di I/O, reset del controller
            var bad = evs.Where(e =>
            {
                string p = e.Provider.ToLowerInvariant();
                if (p == "disk") return e.Id == 7 || e.Id == 11 || e.Id == 51 || e.Id == 52 || e.Id == 153 || e.Id == 154;
                if (p.Contains("ntfs")) return e.Level <= 2 && (e.Id == 55 || e.Id == 57 || e.Id == 98 || e.Id == 137 || e.Id == 140);
                if (p == "volmgr") return e.Id == 161 || e.Id == 162;
                return e.Id == 129 || e.Level <= 2;
            }).ToList();
            foreach (var g in bad.GroupBy(e => e.Provider + " " + e.Id).OrderByDescending(g => g.Count()))
                c.Extra.Add(L.F("{0} ×{1}, ultimo {2}: {3}", "{0} ×{1}, last {2}: {3}", g.Key, g.Count(), g.First().Time.ToString("yyyy-MM-dd HH:mm"), Short(g.First().Message, 160)));
            if (bad.Count == 0) Set(c, CheckState.Ok, L.T("nessun errore negli ultimi 14 giorni", "no errors in the last 14 days"));
            else
            {
                bool severe = bad.Any(e => e.Provider == "disk" && (e.Id == 7 || e.Id == 11 || e.Id == 51));
                Set(c, severe ? CheckState.Error : CheckState.Warn, L.F("{0} errori negli ultimi 14 giorni", "{0} errors in the last 14 days", bad.Count),
                    severe ? L.T("Il registro segnala blocchi danneggiati o errori di lettura: fai un backup e controlla il disco (chkdsk) o il cavo.",
                                 "The log reports bad blocks or read errors: back up and check the disk (chkdsk) or its cable.")
                           : L.T("Ci sono avvisi del disco nel registro eventi: tienili d'occhio e aggiorna i driver del controller.",
                                 "There are disk warnings in the event log: keep an eye on them and update the storage controller drivers."));
            }
        }

        void CheckCrashes(SysCheck c)
        {
            var evs = Events("System",
                "*[System[((Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or (Provider[@Name='EventLog'] and EventID=6008) or (Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001)) and TimeCreated[timediff(@SystemTime) <= " + Days(30) + "]]]", 100);
            var bsod = evs.Where(e => e.Id == 1001).ToList();
            var power = evs.Where(e => e.Id == 41).ToList();
            foreach (var e in bsod.Take(5)) c.Extra.Add(L.T("Schermata blu", "Blue screen") + " " + e.Time.ToString("yyyy-MM-dd HH:mm") + ": " + Short(e.Message, 200));
            foreach (var e in power.Take(5)) c.Extra.Add(L.T("Spegnimento improvviso (Kernel-Power 41)", "Unexpected shutdown (Kernel-Power 41)") + " " + e.Time.ToString("yyyy-MM-dd HH:mm"));
            if (bsod.Count > 0)
                Set(c, CheckState.Error, L.F("{0} schermate blu e {1} spegnimenti improvvisi in 30 giorni", "{0} blue screens and {1} unexpected shutdowns in 30 days", bsod.Count, power.Count),
                    L.T("Il PC si è bloccato con schermate blu: aggiorna Windows e i driver (soprattutto scheda video e chipset); se continua, prova la diagnostica della memoria di Windows.",
                        "The PC crashed with blue screens: update Windows and drivers (especially graphics and chipset); if it continues, run Windows Memory Diagnostic."));
            else if (power.Count > 0)
                Set(c, power.Count >= 3 ? CheckState.Warn : CheckState.Info, L.F("{0} spegnimenti improvvisi in 30 giorni", "{0} unexpected shutdowns in 30 days", power.Count),
                    L.T("Il PC si è spento senza chiudere Windows (blocco, mancanza di corrente o tasto di accensione tenuto premuto). Se non sei stato tu, controlla alimentazione e temperature.",
                        "The PC turned off without shutting down Windows (freeze, power loss or power button held). If it wasn't you, check the power supply and temperatures."));
            else Set(c, CheckState.Ok, L.T("nessuno negli ultimi 30 giorni", "none in the last 30 days"));
        }

        void CheckWhea(SysCheck c)
        {
            var evs = Events("System", "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=1 or Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) <= " + Days(30) + "]]]", 200);
            foreach (var g in evs.GroupBy(e => e.Id).OrderByDescending(g => g.Count()))
                c.Extra.Add("WHEA " + g.Key + " ×" + g.Count() + ", " + L.T("ultimo", "last") + " " + g.First().Time.ToString("yyyy-MM-dd HH:mm") + ": " + Short(g.First().Message, 200));
            int fatal = evs.Count(e => e.Level <= 2);
            if (evs.Count == 0) Set(c, CheckState.Ok, L.T("nessuno negli ultimi 30 giorni", "none in the last 30 days"));
            else Set(c, fatal > 0 ? CheckState.Error : CheckState.Warn, L.F("{0} segnalazioni in 30 giorni ({1} gravi)", "{0} reports in 30 days ({1} severe)", evs.Count, fatal),
                     L.T("Il processore, la memoria o il bus PCIe segnalano errori hardware: aggiorna BIOS e driver del chipset; se sono gravi o frequenti, serve un controllo tecnico.",
                         "The processor, memory or PCIe bus report hardware errors: update the BIOS and chipset drivers; if severe or frequent, have the PC checked."));
        }

        void CheckApps(SysCheck c)
        {
            var evs = Events("Application", "*[System[((Provider[@Name='Application Error'] and EventID=1000) or (Provider[@Name='Application Hang'] and EventID=1002)) and TimeCreated[timediff(@SystemTime) <= " + Days(7) + "]]]", 300);
            var byApp = evs.GroupBy(e => (e.Props.Count > 0 ? e.Props[0] : "?") + (e.Id == 1002 ? " (" + L.T("non risponde", "not responding") + ")" : ""))
                           .OrderByDescending(g => g.Count()).ToList();
            foreach (var g in byApp.Take(10))
            {
                var e = g.First();
                string module = e.Id == 1000 && e.Props.Count > 3 ? ", " + L.T("modulo", "module") + " " + e.Props[3] : "";
                c.Extra.Add(g.Key + " ×" + g.Count() + module + ", " + L.T("ultimo", "last") + " " + e.Time.ToString("yyyy-MM-dd HH:mm"));
            }
            if (evs.Count == 0) Set(c, CheckState.Ok, L.T("nessun blocco negli ultimi 7 giorni", "no crashes in the last 7 days"));
            else
            {
                string worst = byApp[0].Key;
                Set(c, evs.Count >= 10 ? CheckState.Warn : CheckState.Info, L.F("blocchi in 7 giorni: {0}, soprattutto {1}", "crashes in 7 days: {0}, mostly {1}", evs.Count, worst),
                    evs.Count >= 10 ? L.F("{0} si chiude o si blocca spesso: aggiornalo o reinstallalo.", "{0} closes or freezes often: update or reinstall it.", worst) : null);
            }
        }

        void CheckTemp(SysCheck c)
        {
            double t = SysMonitor.Instance.Current.TempC;
            var day = SysMonitor.Instance.Minutes(24).Where(m => m.TempMax > 0).ToList();
            if (day.Count > 0) c.Extra.Add(L.F("Massimo nelle ultime 24 ore: {0:0} °C", "Peak in the last 24 hours: {0:0} °C", day.Max(m => m.TempMax)));
            c.Extra.Add(L.T("Fonte: zone termiche ACPI di Windows (non sempre la temperatura esatta del processore).", "Source: Windows ACPI thermal zones (not always the exact processor temperature)."));
            if (t <= 0) { c.Extra.Clear(); Set(c, CheckState.Info, L.T("questo PC non espone la temperatura a Windows", "this PC does not expose its temperature to Windows")); return; }
            string detail = t.ToString("0") + " °C";
            if (t >= 90) Set(c, CheckState.Error, detail, L.T("Il PC è molto caldo: pulisci le prese d'aria dalla polvere, non usarlo su superfici morbide e controlla la ventola.",
                                                             "The PC is very hot: clean dust from the vents, don't use it on soft surfaces and check the fan."));
            else if (t >= 80) Set(c, CheckState.Warn, detail, L.T("Il PC è caldo: controlla che le prese d'aria siano libere.", "The PC is hot: make sure the vents are clear."));
            else Set(c, CheckState.Ok, detail);
        }

        void CheckBattery(SysCheck c)
        {
            var s = SysMonitor.Instance.Current;
            if (s.Battery < 0) { Set(c, CheckState.Info, L.T("nessuna batteria (PC fisso)", "no battery (desktop PC)")); return; }
            long design = 0, full = 0, cycles = -1;
            try
            {
                foreach (var o in Wmi(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData")) design += Long(o, "DesignedCapacity");
                foreach (var o in Wmi(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity")) full += Long(o, "FullChargedCapacity");
                foreach (var o in Wmi(@"root\wmi", "SELECT CycleCount FROM BatteryCycleCount")) cycles = Math.Max(cycles, Long(o, "CycleCount"));
            }
            catch { }
            c.Extra.Add(L.F("Carica: {0}%, {1}", "Charge: {0}%, {1}", s.Battery, s.OnAc ? (s.Charging ? L.T("in carica", "charging") : L.T("collegato alla corrente", "plugged in")) : L.T("a batteria", "on battery")));
            if (design > 0 && full > 0)
            {
                double health = Math.Min(100, 100.0 * full / design);
                c.Extra.Add(L.F("Capacità attuale {0} mWh su {1} mWh di progetto ({2:0}%)", "Current capacity {0} mWh of {1} mWh design ({2:0}%)", full, design, health));
                if (cycles > 0) c.Extra.Add(L.F("Cicli di carica: {0}", "Charge cycles: {0}", cycles));
                string detail = L.F("salute {0:0}%, carica {1}%", "health {0:0}%, charge {1}%", health, s.Battery);
                if (health < 50) Set(c, CheckState.Error, detail, L.T("La batteria tiene meno della metà della carica originale: valuta la sostituzione.", "The battery holds less than half its original charge: consider replacing it."));
                else if (health < 70) Set(c, CheckState.Warn, detail, L.T("La batteria è usurata: dura sensibilmente meno di quando era nuova.", "The battery is worn: it lasts noticeably less than when new."));
                else Set(c, CheckState.Ok, detail);
            }
            else Set(c, CheckState.Ok, L.F("carica {0}%", "charge {0}%", s.Battery));
        }

        void CheckStartup(SysCheck c)
        {
            var items = StartupItems();
            var on = items.Where(i => i.Enabled).ToList();
            foreach (var i in items) c.Extra.Add((i.Enabled ? "" : "(" + L.T("disattivato", "disabled") + ") ") + i.Name + " - " + Short(i.Command, 120) + " [" + i.Where + "]");
            string detail = L.F("{0} programmi attivi all'avvio ({1} disattivati)", "{0} programs run at startup ({1} disabled)", on.Count, items.Count - on.Count);
            if (on.Count > 15) Set(c, CheckState.Warn, detail, L.T("Molti programmi partono con Windows e rallentano l'avvio: disattiva quelli che non ti servono in Gestione attività > App di avvio.",
                                                                  "Many programs start with Windows and slow down startup: disable the ones you don't need in Task Manager > Startup apps."));
            else Set(c, on.Count > 10 ? CheckState.Info : CheckState.Ok, detail);
        }

        public class StartupItem { public string Name, Command, Where; public bool Enabled = true; }

        public static List<StartupItem> StartupItems()
        {
            var res = new List<StartupItem>();
            Action<RegistryKey, string, string, string> reg = (root, path, approved, where) =>
            {
                try
                {
                    using (var k = root.OpenSubKey(path))
                    using (var a = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + approved))
                    {
                        if (k == null) return;
                        foreach (var n in k.GetValueNames())
                        {
                            if (n.Length == 0) continue;
                            var it = new StartupItem { Name = n, Command = Convert.ToString(k.GetValue(n)), Where = where };
                            var b = a != null ? a.GetValue(n) as byte[] : null;
                            if (b != null && b.Length > 0 && (b[0] & 1) == 1) it.Enabled = false;
                            res.Add(it);
                        }
                    }
                }
                catch { }
            };
            reg(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "HKCU Run");
            reg(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "HKLM Run");
            reg(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", "HKLM Run (32 bit)");
            foreach (var folder in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
            {
                try
                {
                    string dir = Environment.GetFolderPath(folder);
                    var root = folder == Environment.SpecialFolder.Startup ? Registry.CurrentUser : Registry.LocalMachine;
                    using (var a = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder"))
                        foreach (var f in Directory.GetFiles(dir))
                        {
                            string n = Path.GetFileName(f);
                            if (n.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                            var it = new StartupItem { Name = Path.GetFileNameWithoutExtension(f), Command = f, Where = L.T("cartella Esecuzione automatica", "Startup folder") };
                            var b = a != null ? a.GetValue(n) as byte[] : null;
                            if (b != null && b.Length > 0 && (b[0] & 1) == 1) it.Enabled = false;
                            res.Add(it);
                        }
                }
                catch { }
            }
            return res;
        }

        void CheckUpdates(SysCheck c)
        {
            DateTime last = DateTime.MinValue;
            string lastId = "";
            foreach (var o in Wmi(@"root\cimv2", "SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering"))
            {
                var d = ParseQfeDate(Str(o, "InstalledOn"));
                if (d > last) { last = d; lastId = Str(o, "HotFixID"); }
            }
            bool reboot = KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")
                       || KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            if (last > DateTime.MinValue) c.Extra.Add(L.F("Ultimo aggiornamento installato: {0} il {1}", "Last update installed: {0} on {1}", lastId, last.ToString("yyyy-MM-dd")));
            if (reboot) c.Extra.Add(L.T("Un aggiornamento aspetta il riavvio del PC.", "An update is waiting for the PC to restart."));
            int days = last > DateTime.MinValue ? (int)(DateTime.Now - last).TotalDays : -1;
            string detail = (days >= 0 ? L.F("ultimo {0} giorni fa", "last one {0} days ago", days) : L.T("data non disponibile", "date not available")) + (reboot ? ", " + L.T("riavvio in sospeso", "restart pending") : "");
            if (days > 60) Set(c, CheckState.Warn, detail, L.T("Windows non si aggiorna da più di due mesi: apri Impostazioni > Windows Update e installa gli aggiornamenti.",
                                                              "Windows has not updated for over two months: open Settings > Windows Update and install updates."));
            else if (reboot) Set(c, CheckState.Warn, detail, L.T("Riavvia il PC per completare gli aggiornamenti.", "Restart the PC to finish installing updates."));
            else Set(c, days >= 0 ? CheckState.Ok : CheckState.Info, detail);
        }

        void CheckAntivirus(SysCheck c)
        {
            var list = Wmi(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct");
            if (list.Count == 0) { Set(c, CheckState.Info, L.T("informazione non disponibile (Windows Server o Centro sicurezza spento)", "not available (Windows Server or Security Center off)")); return; }
            bool anyOn = false, outdated = false;
            var names = new List<string>();
            foreach (var o in list)
            {
                long st = Long(o, "productState");
                bool on = (st & 0x1000) != 0, old = (st & 0x10) != 0;
                string n = Str(o, "displayName");
                c.Extra.Add(n + ": " + (on ? L.T("attivo", "on") : L.T("spento", "off")) + ", " + (old ? L.T("definizioni non aggiornate", "definitions out of date") : L.T("aggiornato", "up to date")));
                if (on) { anyOn = true; names.Add(n); if (old) outdated = true; }
            }
            if (!anyOn) Set(c, CheckState.Error, L.T("nessun antivirus attivo", "no active antivirus"),
                            L.T("Nessun antivirus è attivo: apri Sicurezza di Windows e attiva la protezione in tempo reale.", "No antivirus is active: open Windows Security and turn on real-time protection."));
            else if (outdated) Set(c, CheckState.Warn, string.Join(", ", names) + " - " + L.T("definizioni vecchie", "old definitions"),
                                   L.T("Aggiorna le definizioni dell'antivirus.", "Update the antivirus definitions."));
            else Set(c, CheckState.Ok, string.Join(", ", names));
        }

        void CheckDevices(SysCheck c)
        {
            var bad = new List<string>();
            foreach (var o in Wmi(@"root\cimv2", "SELECT Name, ConfigManagerErrorCode, PNPClass FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"))
            {
                int code = Int(o, "ConfigManagerErrorCode");
                if (code == 22 || code == 45) continue;   // disattivato dall'utente / non collegato
                string n = Str(o, "Name");
                if (string.IsNullOrEmpty(n)) n = L.T("dispositivo sconosciuto", "unknown device");
                bad.Add(n);
                c.Extra.Add(n + " (" + Str(o, "PNPClass") + "): " + L.T("codice errore", "error code") + " " + code + " - " + DeviceError(code));
            }
            if (bad.Count == 0) Set(c, CheckState.Ok, L.T("nessun dispositivo con problemi", "no devices with problems"));
            else Set(c, CheckState.Warn, L.F("{0} con problemi: {1}", "{0} with problems: {1}", bad.Count, Short(string.Join(", ", bad), 90)),
                     L.T("Alcuni dispositivi non funzionano: in Gestione dispositivi aggiorna o reinstalla il driver (o scaricalo dal sito del produttore).",
                         "Some devices are not working: in Device Manager update or reinstall the driver (or download it from the manufacturer's site)."));
        }

        static string DeviceError(int code)
        {
            switch (code)
            {
                case 1: case 3: case 18: case 39: return L.T("driver mancante o danneggiato", "driver missing or corrupted");
                case 10: return L.T("il dispositivo non si avvia", "the device cannot start");
                case 28: return L.T("driver non installato", "driver not installed");
                case 31: return L.T("Windows non riesce a caricare il driver", "Windows cannot load the driver");
                case 43: return L.T("il dispositivo ha segnalato un problema", "the device reported a problem");
                case 52: return L.T("firma digitale del driver non valida", "driver signature not valid");
                default: return L.T("problema del driver", "driver problem");
            }
        }

        void CheckJunk(SysCheck c)
        {
            long temp = FolderSize(Path.GetTempPath(), 4000);
            long bin = RecycleBinSize();
            c.Extra.Add(L.F("Cartella temporanea dell'utente: {0}", "User temporary folder: {0}", Fmt.Bytes(temp)));
            if (bin >= 0) c.Extra.Add(L.F("Cestino: {0}", "Recycle Bin: {0}", Fmt.Bytes(bin)));
            long total = temp + Math.Max(0, bin);
            string detail = L.F("{0} recuperabili", "{0} recoverable", Fmt.Bytes(total));
            if (total > (10L << 30)) Set(c, CheckState.Warn, detail, L.T("Puoi liberare molto spazio svuotando il Cestino e i file temporanei.", "You can free a lot of space by emptying the Recycle Bin and temporary files."));
            else Set(c, total > (2L << 30) ? CheckState.Info : CheckState.Ok, detail);
        }

        void CheckReliability(SysCheck c)
        {
            string from = ManagementDateTimeConverter.ToDmtfDateTime(DateTime.Now.AddDays(-3));
            double idx = -1; DateTime when = DateTime.MinValue;
            foreach (var o in Wmi(@"root\cimv2", "SELECT SystemStabilityIndex, TimeGenerated FROM Win32_ReliabilityStabilityMetrics WHERE TimeGenerated >= '" + from + "'", 20))
            {
                var t = Dmtf(Str(o, "TimeGenerated"));
                if (t > when) { when = t; try { idx = Convert.ToDouble(o["SystemStabilityIndex"], CultureInfo.InvariantCulture); } catch { } }
            }
            if (idx < 0) { Set(c, CheckState.Info, L.T("non disponibile (Monitoraggio affidabilità spento)", "not available (Reliability Monitor off)")); return; }
            c.Extra.Add(L.F("Indice di stabilità di Windows: {0:0.0} su 10 ({1})", "Windows stability index: {0:0.0} out of 10 ({1})", idx, when.ToString("yyyy-MM-dd HH:mm")));
            c.Extra.Add(L.T("Dettagli: Pannello di controllo > Sicurezza e manutenzione > Visualizza cronologia affidabilità.", "Details: Control Panel > Security and Maintenance > View reliability history."));
            string detail = idx.ToString("0.0") + " / 10";
            if (idx < 3) Set(c, CheckState.Error, detail, L.T("Windows ha registrato molti errori di recente: la Cronologia affidabilità mostra quali.", "Windows logged many failures recently: Reliability history shows which ones."));
            else if (idx < 6) Set(c, CheckState.Warn, detail, L.T("Ci sono stati diversi errori nei giorni scorsi (vedi Cronologia affidabilità).", "There were several failures in recent days (see Reliability history)."));
            else Set(c, CheckState.Ok, detail);
        }

        // ---------------- conclusione e report ----------------

        void Conclude()
        {
            Score = 100;
            foreach (var c in Checks)
            {
                if (c.State == CheckState.Error) Score -= 18;
                else if (c.State == CheckState.Warn) Score -= 7;
            }
            Score = Math.Max(0, Score);
            Overall = Checks.Any(c => c.State == CheckState.Error) ? CheckState.Error : Checks.Any(c => c.State == CheckState.Warn) ? CheckState.Warn : CheckState.Ok;
            var todo = Checks.Where(c => c.State >= CheckState.Warn && c.Advice.Length > 0).OrderByDescending(c => c.State).ToList();
            if (Overall == CheckState.Ok)
            {
                ConclusionTitle = L.F("Il PC è in buona salute ({0}/100)", "The PC is in good health ({0}/100)", Score);
                ConclusionText = L.T("Nessun problema rilevato.", "No problems found.");
            }
            else
            {
                ConclusionTitle = Overall == CheckState.Error
                    ? L.F("Ci sono problemi da risolvere ({0}/100)", "There are problems to fix ({0}/100)", Score)
                    : L.F("Il PC funziona, con alcuni punti da sistemare ({0}/100)", "The PC works, with a few things to fix ({0}/100)", Score);
                ConclusionText = string.Join("\n", todo.Take(3).Select(c => "• " + c.Advice));
            }
        }

        static string StateWord(CheckState s)
        {
            switch (s)
            {
                case CheckState.Ok: return "OK";
                case CheckState.Warn: return "WARNING";
                case CheckState.Error: return "ERROR";
                default: return "INFO";
            }
        }

        // Report completo (per "Copia report" e per l'assistente): controlli, dettagli e andamento delle ultime ore
        // compact: solo i dettagli dei controlli con problemi (per i modelli piccoli dell'assistente locale)
        public string ReportText(bool compact = false)
        {
            var sb = new StringBuilder();
            sb.AppendLine(L.F("Analisi del PC (AppSys {0}) - {1}", "PC analysis (AppSys {0}) - {1}", AppVersion.Text, Time.ToString("yyyy-MM-dd HH:mm")));
            sb.AppendLine(L.F("PC: {0}", "PC: {0}", Host + (Domain.Length > 0 ? "." + Domain : "")));
            sb.AppendLine(ConclusionTitle);
            if (ConclusionText.Length > 0) sb.AppendLine(ConclusionText);
            sb.AppendLine();
            foreach (var c in Checks)
            {
                if (compact && c.State < CheckState.Warn) continue;
                sb.AppendLine("[" + StateWord(c.State) + "] " + c.Title + ": " + c.Detail);
                foreach (var x in c.Extra) sb.AppendLine("    - " + x);
            }
            if (compact)
            {
                var fine = Checks.Where(c => c.State < CheckState.Warn).ToList();
                if (fine.Count > 0) sb.AppendLine("[OK/INFO, " + L.T("nessun problema", "no problem") + "] " + string.Join("; ", fine.Select(c => c.Title + ": " + c.Detail)));
                var sys = Get("system");
                if (sys.Extra.Count > 0) sb.AppendLine(string.Join("; ", sys.Extra.Take(4)));
                return sb.ToString();
            }
            string trend = TrendText(24);
            if (trend.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine(L.T("Andamento (medie orarie; CPU/RAM/GPU/disco in %, temperatura in °C):", "Trend (hourly averages; CPU/RAM/GPU/disk in %, temperature in °C):"));
                sb.Append(trend);
            }
            return sb.ToString();
        }

        // Riassunto della cronologia a blocchi (60 minuti = medie orarie)
        public static string TrendText(double hours, int bucketMinutes = 60)
        {
            var mins = hours <= 24 ? SysMonitor.Instance.Minutes(hours) : SysMonitor.FileHistory(hours);
            if (mins.Count == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine("time | cpu avg | cpu max | ram | gpu | disk busy | temp max | net down/up KB/s | top CPU process | top memory process");
            foreach (var g in mins.GroupBy(m => new DateTime(m.Time.Ticks - m.Time.Ticks % TimeSpan.FromMinutes(bucketMinutes).Ticks)))
            {
                var l = g.ToList();
                var gpu = l.Where(m => m.GpuAvg >= 0).ToList();
                var disk = l.Where(m => m.DiskBusyAvg >= 0).ToList();
                var t = l.Where(m => m.TempMax > 0).ToList();
                string topCpu = l.GroupBy(m => Name(m.TopCpu)).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault() ?? "";
                string topMem = l.GroupBy(m => Name(m.TopMem)).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault() ?? "";
                sb.AppendLine(string.Join(" | ", new[]
                {
                    g.Key.ToString("MM-dd HH:mm"), l.Average(m => m.CpuAvg).ToString("0"), l.Max(m => m.CpuMax).ToString("0"), l.Average(m => m.RamAvg).ToString("0"),
                    gpu.Count > 0 ? gpu.Average(m => m.GpuAvg).ToString("0") : "-", disk.Count > 0 ? disk.Average(m => m.DiskBusyAvg).ToString("0") : "-",
                    t.Count > 0 ? t.Max(m => m.TempMax).ToString("0") : "-",
                    (l.Average(m => m.NetRxAvg) / 1024).ToString("0") + "/" + (l.Average(m => m.NetTxAvg) / 1024).ToString("0"),
                    topCpu, topMem
                }));
            }
            return sb.ToString();
        }

        static string Name(string top)
        {
            if (string.IsNullOrEmpty(top)) return "";
            int i = top.LastIndexOf(' ');
            if (i > 0 && top.EndsWith("%")) return top.Substring(0, i);
            int j = top.LastIndexOf(' ', Math.Max(0, i - 1));
            return j > 0 ? top.Substring(0, j) : top;
        }

        // ---------------- utilità ----------------

        public static List<ManagementBaseObject> Wmi(string scope, string query, int timeoutSec = 15)
        {
            var res = new List<ManagementBaseObject>();
            var opts = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(timeoutSec), ReturnImmediately = true, Rewindable = false };
            using (var s = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), opts))
            using (var coll = s.Get())
                foreach (ManagementBaseObject o in coll) res.Add(o);
            return res;
        }

        public static string Str(ManagementBaseObject o, string p)
        {
            try { var v = o[p]; return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture).Trim(); } catch { return ""; }
        }
        static int Int(ManagementBaseObject o, string p)
        {
            try { var v = o[p]; return v == null ? -1 : Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return -1; }
        }
        static long Long(ManagementBaseObject o, string p)
        {
            try { var v = o[p]; return v == null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        static DateTime Dmtf(string s)
        {
            if (string.IsNullOrEmpty(s)) return DateTime.MinValue;
            try { return ManagementDateTimeConverter.ToDateTime(s); } catch { return DateTime.MinValue; }
        }

        static DateTime ParseQfeDate(string s)
        {
            if (string.IsNullOrEmpty(s)) return DateTime.MinValue;
            DateTime d;
            if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out d)) return d;
            long hex;   // vecchi sistemi: FILETIME in esadecimale
            if (long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hex)) try { return DateTime.FromFileTime(hex); } catch { }
            return DateTime.MinValue;
        }

        static bool KeyExists(string path)
        {
            try { using (var k = Registry.LocalMachine.OpenSubKey(path)) return k != null; } catch { return false; }
        }

        public static string Days(int d) { return ((long)d * 86400000L).ToString(CultureInfo.InvariantCulture); }

        public static string Short(string s, int max)
        {
            if (s == null) return "";
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        public class Ev
        {
            public DateTime Time; public int Id, Level; public string Provider, Message;
            public List<string> Props = new List<string>();
        }

        // Eventi del registro, più recenti per primi (massimo max). Il messaggio si formatta solo per i primi 40.
        public static List<Ev> Events(string log, string xpath, int max)
        {
            var res = new List<Ev>();
            var q = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
            using (var r = new EventLogReader(q))
            {
                EventRecord e;
                while (res.Count < max && (e = r.ReadEvent()) != null)
                    using (e)
                    {
                        var ev = new Ev { Time = e.TimeCreated ?? DateTime.MinValue, Id = e.Id, Level = e.Level ?? 4, Provider = e.ProviderName ?? "" };
                        try { foreach (var p in e.Properties) ev.Props.Add(Convert.ToString(p.Value, CultureInfo.InvariantCulture)); } catch { }
                        if (res.Count < 40) { try { ev.Message = e.FormatDescription(); } catch { } }
                        if (string.IsNullOrEmpty(ev.Message)) ev.Message = string.Join(", ", ev.Props.Take(4));
                        res.Add(ev);
                    }
            }
            return res;
        }

        // Dimensione di una cartella, fermandosi dopo maxMs (le cartelle enormi danno un valore minimo)
        public static long FolderSize(string dir, int maxMs)
        {
            long total = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0 && sw.ElapsedMilliseconds < maxMs)
            {
                string d = stack.Pop();
                try
                {
                    var di = new DirectoryInfo(d);
                    foreach (var f in di.EnumerateFiles()) { try { total += f.Length; } catch { } }
                    foreach (var s in di.EnumerateDirectories())
                        if ((s.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(s.FullName);
                }
                catch { }
            }
            return total;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SHQUERYRBINFO { public int cbSize; public long i64Size, i64NumItems; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBin(string root, ref SHQUERYRBINFO info);

        static long RecycleBinSize()
        {
            try
            {
                var i = new SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO)) };
                return SHQueryRecycleBin(null, ref i) == 0 ? i.i64Size : -1;
            }
            catch { return -1; }
        }
    }
}
