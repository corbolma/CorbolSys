// Raccolta dei dati del sistema in tempo reale, su un thread in background (un campione al secondo):
//   - CPU (GetSystemTimes), RAM e memoria impegnata (GlobalMemoryStatusEx, GetPerformanceInfo)
//   - GPU (contatori "GPU Engine", come Gestione attività), dischi (contatori "PhysicalDisk" e spazio dei volumi)
//   - rete (statistiche delle schede), temperatura (contatori "Thermal Zone Information"), batteria
//   - processi (NtQuerySystemInformation: una sola chiamata per tutti, senza aprire i processi) con CPU,
//     memoria privata e byte letti/scritti su disco, raggruppati per nome
// Ogni minuto si salva un riassunto (media e massimi, processo più pesante) nella cronologia: 24 ore in memoria
// e, se attivo, 7 giorni in %APPDATA%\AppSys\history.csv per l'analisi e le domande all'assistente.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AppSys
{
    class VolumeInfo
    {
        public string Name, Label, Format;
        public long Total, Free;
        public double UsedPct { get { return Total > 0 ? 100.0 * (Total - Free) / Total : 0; } }
    }

    class ProcInfo
    {
        public string Name;
        public int Count;              // processi con questo nome
        public double Cpu;             // % del totale del PC
        public long Private;           // memoria privata (byte)
        public double IoRead, IoWrite; // byte/s (tutto l'I/O del processo: disco, rete, dispositivi)
    }

    // Lo stato del PC in un istante (sostituito per intero a ogni campione: chi lo legge non vede valori a metà)
    class SysSnapshot
    {
        public DateTime Time;
        public double Cpu;                       // %
        public long RamTotal, RamAvail;          // byte
        public long CommitTotal, CommitLimit;    // memoria impegnata (RAM + file di paging)
        public double Gpu = -1;                  // % (-1 = non disponibile)
        public double DiskBusy = -1;             // % di tempo attivo dei dischi fisici
        public double DiskRead, DiskWrite;       // byte/s
        public double NetRx, NetTx;              // byte/s
        public double TempC = -1;                // zona termica più calda (-1 = non disponibile)
        public int Battery = -1;                 // % (-1 = nessuna batteria)
        public bool OnAc = true, Charging;
        public TimeSpan Uptime;
        public int ProcessCount, ThreadCount, HandleCount;
        public List<VolumeInfo> Volumes = new List<VolumeInfo>();
        public List<ProcInfo> Procs = new List<ProcInfo>();   // raggruppati per nome
        public double RamPct { get { return RamTotal > 0 ? 100.0 * (RamTotal - RamAvail) / RamTotal : 0; } }
        public double CommitPct { get { return CommitLimit > 0 ? 100.0 * CommitTotal / CommitLimit : 0; } }
    }

    // Riassunto di un minuto, per la cronologia
    class MinuteStat
    {
        public DateTime Time;
        public double CpuAvg, CpuMax, RamAvg, GpuAvg = -1, DiskBusyAvg = -1, TempMax = -1;
        public double DiskReadAvg, DiskWriteAvg, NetRxAvg, NetTxAvg;
        public string TopCpu = "", TopMem = "";   // "nome 35%" e "nome 1.2 GB"

        public string ToCsv()
        {
            var ci = CultureInfo.InvariantCulture;
            return string.Join(";", new[]
            {
                // valori interi: Excel li legge come numeri con qualunque separatore decimale
                Time.ToString("yyyy-MM-dd HH:mm", ci), CpuAvg.ToString("0", ci), CpuMax.ToString("0", ci), RamAvg.ToString("0", ci),
                GpuAvg.ToString("0", ci), DiskBusyAvg.ToString("0", ci), TempMax.ToString("0", ci),
                (DiskReadAvg / 1024).ToString("0", ci), (DiskWriteAvg / 1024).ToString("0", ci), (NetRxAvg / 1024).ToString("0", ci), (NetTxAvg / 1024).ToString("0", ci),
                TopCpu.Replace(";", ","), TopMem.Replace(";", ",")
            });
        }

        // Intestazione del file, nella lingua dell'interfaccia (chi legge il file la salta: non è una riga di dati)
        public static string CsvHeader()
        {
            return string.Join(";", new[]
            {
                L.T("data e ora", "date and time"), L.T("CPU media %", "CPU average %"), L.T("CPU massima %", "CPU peak %"), "RAM %", "GPU %",
                L.T("attività dischi %", "disk activity %"), L.T("temperatura massima °C", "peak temperature °C"),
                L.T("lettura dischi KB/s", "disk read KB/s"), L.T("scrittura dischi KB/s", "disk write KB/s"),
                "download KB/s", "upload KB/s",
                L.T("processo con più CPU", "top CPU process"), L.T("processo con più memoria", "top memory process")
            });
        }

        public static MinuteStat FromCsv(string line)
        {
            var p = line.Split(';');
            if (p.Length < 13) return null;
            var ci = CultureInfo.InvariantCulture;
            DateTime t;
            if (!DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm", ci, DateTimeStyles.None, out t)) return null;
            Func<int, double> d = i => { double v; return double.TryParse(p[i], NumberStyles.Float, ci, out v) ? v : 0; };
            return new MinuteStat
            {
                Time = t, CpuAvg = d(1), CpuMax = d(2), RamAvg = d(3), GpuAvg = d(4), DiskBusyAvg = d(5), TempMax = d(6),
                DiskReadAvg = d(7) * 1024, DiskWriteAvg = d(8) * 1024, NetRxAvg = d(9) * 1024, NetTxAvg = d(10) * 1024,
                TopCpu = p[11], TopMem = p[12]
            };
        }
    }

    class SysMonitor
    {
        public static readonly SysMonitor Instance = new SysMonitor();

        public const int HistLen = 60;                     // secondi nei grafici del widget
        const int MemoryMinutes = 24 * 60;                 // cronologia in memoria
        const int FileDays = 7;                            // cronologia su file

        public static bool HistoryEnabled = true;          // cronologia su file (impostazione)
        public static string HistoryPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppSys", "history.csv"); }
        }

        volatile SysSnapshot current = new SysSnapshot { Time = DateTime.Now };
        public SysSnapshot Current { get { return current; } }

        // Ultimi HistLen secondi, per i grafici (accesso sotto lock(hist))
        readonly List<SysSnapshot> hist = new List<SysSnapshot>();
        readonly List<MinuteStat> minutes = new List<MinuteStat>();
        readonly List<SysSnapshot> thisMinute = new List<SysSnapshot>();

        public event Action Sampled;   // chiamato dal thread di raccolta

        Thread thread;
        volatile bool stop;

        public static string CpuName { get; private set; }
        public static int Cores { get { return Environment.ProcessorCount; } }

        public void Start()
        {
            if (thread != null) return;
            CpuName = ReadCpuName();
            LoadHistory();
            thread = new Thread(Loop) { IsBackground = true, Name = "AppSys monitor", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        public void Stop()
        {
            stop = true;
            FlushMinute(true);
        }

        public List<SysSnapshot> History()
        {
            lock (hist) return new List<SysSnapshot>(hist);
        }

        public List<MinuteStat> Minutes(double hours)
        {
            var from = DateTime.Now.AddHours(-hours);
            lock (minutes) return minutes.Where(m => m.Time >= from).ToList();
        }

        void Loop()
        {
            var sw = Stopwatch.StartNew();
            int tick = 0;
            while (!stop)
            {
                long t0 = sw.ElapsedMilliseconds;
                try { Sample(tick); } catch { }
                tick++;
                var h = Sampled;
                if (h != null) try { h(); } catch { }
                long wait = 1000 - (sw.ElapsedMilliseconds - t0);
                if (wait > 0) Thread.Sleep((int)wait);
            }
        }

        // ---------------- campionamento ----------------

        long lastIdle, lastKernel, lastUser;
        long lastNetRx = -1, lastNetTx = -1;
        DateTime lastNetTime;
        List<VolumeInfo> volumes = new List<VolumeInfo>();
        List<ProcInfo> procs = new List<ProcInfo>();
        PerformanceCounter pcIdle, pcRead, pcWrite;
        bool diskCountersFailed;
        GpuReader gpu = new GpuReader();
        double temp = -1;
        bool tempFailed;
        int procCount, threadCount, handleCount;

        void Sample(int tick)
        {
            var s = new SysSnapshot { Time = DateTime.Now };

            // CPU
            long idle, kernel, user;
            if (GetSystemTimes(out idle, out kernel, out user))
            {
                long di = idle - lastIdle, dk = kernel - lastKernel, du = user - lastUser;
                long total = dk + du;   // il tempo "kernel" comprende quello inattivo
                if (lastKernel != 0 && total > 0) s.Cpu = Math.Max(0, Math.Min(100, 100.0 * (total - di) / total));
                lastIdle = idle; lastKernel = kernel; lastUser = user;
            }

            // RAM
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (GlobalMemoryStatusEx(ref m)) { s.RamTotal = (long)m.ullTotalPhys; s.RamAvail = (long)m.ullAvailPhys; }
            var pi = new PERFORMANCE_INFORMATION { cb = (uint)Marshal.SizeOf(typeof(PERFORMANCE_INFORMATION)) };
            if (GetPerformanceInfo(out pi, pi.cb))
            {
                long page = (long)pi.PageSize;
                s.CommitTotal = (long)pi.CommitTotal * page;
                s.CommitLimit = (long)pi.CommitLimit * page;
                handleCount = (int)pi.HandleCount;
                threadCount = (int)pi.ThreadCount;
                procCount = (int)pi.ProcessCount;
            }
            s.ProcessCount = procCount; s.ThreadCount = threadCount; s.HandleCount = handleCount;
            s.Uptime = TimeSpan.FromMilliseconds(GetTickCount64());

            // Dischi: attività e velocità
            if (!diskCountersFailed)
            {
                try
                {
                    if (pcIdle == null)
                    {
                        pcIdle = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total", true);
                        pcRead = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", true);
                        pcWrite = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", true);
                        pcIdle.NextValue(); pcRead.NextValue(); pcWrite.NextValue();
                    }
                    else
                    {
                        // % Idle Time è la media dei dischi: con più dischi un disco al 100% risulta al 50%
                        s.DiskBusy = Math.Max(0, Math.Min(100, 100 - pcIdle.NextValue()));
                        s.DiskRead = pcRead.NextValue();
                        s.DiskWrite = pcWrite.NextValue();
                    }
                }
                catch { diskCountersFailed = true; }
            }

            // Volumi: ogni 10 secondi
            if (tick % 10 == 0) volumes = ReadVolumes();
            s.Volumes = volumes;

            // Rete
            long rx = 0, tx = 0;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    var st = ni.GetIPStatistics();
                    rx += st.BytesReceived; tx += st.BytesSent;
                }
            }
            catch { }
            if (lastNetRx >= 0)
            {
                double sec = (s.Time - lastNetTime).TotalSeconds;
                if (sec > 0.2)
                {
                    s.NetRx = Math.Max(0, (rx - lastNetRx) / sec);
                    s.NetTx = Math.Max(0, (tx - lastNetTx) / sec);
                }
            }
            lastNetRx = rx; lastNetTx = tx; lastNetTime = s.Time;

            // GPU: ogni 2 secondi (leggere i contatori di tutti i motori costa qualche millisecondo)
            if (tick % 2 == 0) gpu.Read();
            s.Gpu = gpu.Value;

            // Temperatura: ogni 5 secondi
            if (!tempFailed && tick % 5 == 0) temp = ReadTemp();
            s.TempC = temp;

            // Batteria
            try
            {
                var ps = SystemInformation.PowerStatus;
                if ((ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0 && ps.BatteryChargeStatus != BatteryChargeStatus.Unknown)
                {
                    s.Battery = (int)Math.Round(ps.BatteryLifePercent * 100);
                    s.Charging = (ps.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;
                }
                s.OnAc = ps.PowerLineStatus != PowerLineStatus.Offline;
            }
            catch { }

            // Processi: ogni 2 secondi
            if (tick % 2 == 0) { var p = ProcReader.Read(); if (p != null) procs = p; }
            s.Procs = procs;

            current = s;
            lock (hist)
            {
                hist.Add(s);
                while (hist.Count > HistLen) hist.RemoveAt(0);
            }
            bool flush;
            lock (thisMinute) flush = thisMinute.Count > 0 && (s.Time.Minute != thisMinute[0].Time.Minute || thisMinute.Count >= 70);
            if (flush) FlushMinute(false);
            lock (thisMinute) thisMinute.Add(s);
        }

        // Chiude il minuto in corso: riassunto in memoria e su file
        void FlushMinute(bool final)
        {
            List<SysSnapshot> list;
            lock (thisMinute)
            {
                if (thisMinute.Count == 0) return;
                list = new List<SysSnapshot>(thisMinute);
                thisMinute.Clear();
            }
            if (list.Count < 5 && !final) return;   // minuto quasi vuoto (avvio)
            var first = list[0];
            var ms = new MinuteStat
            {
                Time = new DateTime(first.Time.Year, first.Time.Month, first.Time.Day, first.Time.Hour, first.Time.Minute, 0),
                CpuAvg = list.Average(x => x.Cpu), CpuMax = list.Max(x => x.Cpu), RamAvg = list.Average(x => x.RamPct),
                DiskReadAvg = list.Average(x => x.DiskRead), DiskWriteAvg = list.Average(x => x.DiskWrite),
                NetRxAvg = list.Average(x => x.NetRx), NetTxAvg = list.Average(x => x.NetTx)
            };
            var g = list.Where(x => x.Gpu >= 0).ToList();
            if (g.Count > 0) ms.GpuAvg = g.Average(x => x.Gpu);
            var d = list.Where(x => x.DiskBusy >= 0).ToList();
            if (d.Count > 0) ms.DiskBusyAvg = d.Average(x => x.DiskBusy);
            var t = list.Where(x => x.TempC > 0).ToList();
            if (t.Count > 0) ms.TempMax = t.Max(x => x.TempC);
            // Processo più pesante del minuto: la media della CPU di ogni nome sui campioni
            var cpuBy = new Dictionary<string, double>();
            var memBy = new Dictionary<string, long>();
            foreach (var s in list)
                foreach (var p in s.Procs)
                {
                    double c; cpuBy.TryGetValue(p.Name, out c); cpuBy[p.Name] = c + p.Cpu / list.Count;
                    long mm; memBy.TryGetValue(p.Name, out mm); memBy[p.Name] = Math.Max(mm, p.Private);
                }
            if (cpuBy.Count > 0) { var top = cpuBy.OrderByDescending(kv => kv.Value).First(); ms.TopCpu = top.Key + " " + top.Value.ToString("0") + "%"; }
            if (memBy.Count > 0) { var top = memBy.OrderByDescending(kv => kv.Value).First(); ms.TopMem = top.Key + " " + Fmt.Bytes(top.Value); }

            lock (minutes)
            {
                minutes.Add(ms);
                while (minutes.Count > MemoryMinutes) minutes.RemoveAt(0);
            }
            if (HistoryEnabled)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath));
                    // file nuovo: BOM UTF-8 (Excel legge bene accenti e °) e intestazione
                    if (!File.Exists(HistoryPath) || new FileInfo(HistoryPath).Length == 0)
                        File.WriteAllText(HistoryPath, MinuteStat.CsvHeader() + "\r\n", new UTF8Encoding(true));
                    File.AppendAllText(HistoryPath, ms.ToCsv() + "\r\n", new UTF8Encoding(false));
                }
                catch { }
            }
        }

        // All'avvio: le ultime 24 ore dal file (e il file si accorcia a 7 giorni)
        void LoadHistory()
        {
            try
            {
                if (!File.Exists(HistoryPath)) return;
                var lines = File.ReadAllLines(HistoryPath);
                // senza intestazione: file delle versioni 1.0.0-1.0.1, con i decimali col punto
                bool hasHeader = lines.Length > 0 && MinuteStat.FromCsv(lines[0]) == null;
                var keep = new List<string>();
                var from = DateTime.Now.AddDays(-FileDays);
                var memFrom = DateTime.Now.AddMinutes(-MemoryMinutes);
                foreach (var ln in lines)
                {
                    var ms = MinuteStat.FromCsv(ln);
                    if (ms == null || ms.Time < from) continue;
                    keep.Add(hasHeader ? ln : ms.ToCsv());   // le righe vecchie si riscrivono nel formato nuovo (numeri interi)
                    if (ms.Time >= memFrom) lock (minutes) minutes.Add(ms);
                }
                // si riscrive se ci sono righe vecchie da togliere o se manca l'intestazione; mai con /report o /snapshot
                if (HistoryEnabled && (!hasHeader || keep.Count < lines.Length - 1))
                {
                    keep.Insert(0, hasHeader ? lines[0] : MinuteStat.CsvHeader());
                    File.WriteAllLines(HistoryPath, keep, new UTF8Encoding(true));
                }
            }
            catch { }
        }

        // Cronologia su file (fino a 7 giorni), per l'assistente
        public static List<MinuteStat> FileHistory(double hours)
        {
            var res = new List<MinuteStat>();
            var from = DateTime.Now.AddHours(-hours);
            try
            {
                if (!File.Exists(HistoryPath)) return res;
                foreach (var ln in File.ReadAllLines(HistoryPath))
                {
                    var ms = MinuteStat.FromCsv(ln);
                    if (ms != null && ms.Time >= from) res.Add(ms);
                }
            }
            catch { }
            return res;
        }

        static List<VolumeInfo> ReadVolumes()
        {
            var res = new List<VolumeInfo>();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    res.Add(new VolumeInfo
                    {
                        Name = d.Name.TrimEnd('\\'), Label = d.VolumeLabel, Format = d.DriveFormat,
                        Total = d.TotalSize, Free = d.TotalFreeSpace
                    });
                }
            }
            catch { }
            return res;
        }

        PerformanceCounterCategory thermalCat;

        // Zona termica più calda (contatori ACPI di Windows, senza permessi di amministratore). Non tutti i PC
        // li espongono e su alcuni il valore è fisso: va preso come indicazione.
        double ReadTemp()
        {
            try
            {
                if (thermalCat == null)
                {
                    if (!PerformanceCounterCategory.Exists("Thermal Zone Information")) { tempFailed = true; return -1; }
                    thermalCat = new PerformanceCounterCategory("Thermal Zone Information");
                }
                var data = thermalCat.ReadCategory();
                double best = -1;
                string counter = data.Contains("High Precision Temperature") ? "High Precision Temperature" : "Temperature";
                if (!data.Contains(counter)) { tempFailed = true; return -1; }
                foreach (InstanceData id in data[counter].Values)
                {
                    double k = id.RawValue;
                    if (counter == "High Precision Temperature") k /= 10.0;   // decimi di kelvin
                    double c = k - 273.15;
                    if (c > 5 && c < 130) best = Math.Max(best, c);
                }
                if (best < 0) tempFailed = true;
                return best;
            }
            catch { tempFailed = true; return -1; }
        }

        static string ReadCpuName()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    if (k != null) return ((k.GetValue("ProcessorNameString") as string) ?? "").Trim();
            }
            catch { }
            return "";
        }

        // ---------------- P/Invoke ----------------

        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("kernel32.dll")] static extern ulong GetTickCount64();

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        [StructLayout(LayoutKind.Sequential)]
        struct PERFORMANCE_INFORMATION
        {
            public uint cb;
            public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
                           KernelTotal, KernelPaged, KernelNonpaged, PageSize;
            public uint HandleCount, ProcessCount, ThreadCount;
        }
        [DllImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo")] static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pi, uint size);
    }

    // Utilizzo della GPU come in Gestione attività: per ogni motore (3D, copia, video...) si somma l'uso di tutti
    // i processi, e il valore è quello del motore più impegnato.
    class GpuReader
    {
        PerformanceCounterCategory cat;
        Dictionary<string, CounterSample> last = new Dictionary<string, CounterSample>();
        bool failed;
        public double Value = -1;

        public void Read()
        {
            if (failed) return;
            try
            {
                if (cat == null)
                {
                    if (!PerformanceCounterCategory.Exists("GPU Engine")) { failed = true; return; }
                    cat = new PerformanceCounterCategory("GPU Engine");
                }
                var data = cat.ReadCategory();
                if (!data.Contains("Utilization Percentage")) { failed = true; return; }
                var now = new Dictionary<string, CounterSample>();
                var engines = new Dictionary<string, double>();
                foreach (InstanceData id in data["Utilization Percentage"].Values)
                {
                    string inst = id.InstanceName;
                    now[inst] = id.Sample;
                    CounterSample old;
                    if (!last.TryGetValue(inst, out old)) continue;
                    double v = CounterSample.Calculate(old, id.Sample);
                    int i = inst.IndexOf("luid_", StringComparison.Ordinal);
                    string engine = i >= 0 ? inst.Substring(i) : inst;
                    double sum; engines.TryGetValue(engine, out sum);
                    engines[engine] = sum + v;
                }
                last = now;
                Value = engines.Count > 0 ? Math.Min(100, engines.Values.Max()) : (Value < 0 && last.Count > 0 ? 0 : Value);
            }
            catch { failed = true; Value = -1; }
        }
    }

    // Tutti i processi con una sola chiamata (NtQuerySystemInformation, SystemProcessInformation): niente handle
    // da aprire e nessun "accesso negato" sui processi di sistema. Struttura con gli offset a 64 bit.
    static class ProcReader
    {
        [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int needed);

        class Prev { public long Time, Read, Write; }
        static Dictionary<long, Prev> prev = new Dictionary<long, Prev>();   // chiave: pid ^ ora di creazione
        static DateTime prevTime;
        static int bufSize = 1 << 20;

        public static List<ProcInfo> Read()
        {
            if (IntPtr.Size != 8) return null;
            IntPtr buf = IntPtr.Zero;
            try
            {
                int needed = 0, status = 0;
                for (int i = 0; i < 5; i++)
                {
                    buf = Marshal.AllocHGlobal(bufSize);
                    status = NtQuerySystemInformation(5, buf, bufSize, out needed);
                    if (status != unchecked((int)0xC0000004)) break;   // STATUS_INFO_LENGTH_MISMATCH
                    Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
                    bufSize = Math.Max(bufSize * 2, needed + (64 << 10));
                }
                if (status != 0 || buf == IntPtr.Zero) return null;

                var now = DateTime.UtcNow;
                double sec = (now - prevTime).TotalSeconds;
                bool havePrev = prev.Count > 0 && sec > 0.2 && sec < 30;
                double cpuDen = sec * 1e7 * Environment.ProcessorCount;   // unità di 100 ns
                var next = new Dictionary<long, Prev>();
                var groups = new Dictionary<string, ProcInfo>(StringComparer.OrdinalIgnoreCase);

                int off = 0;
                while (true)
                {
                    IntPtr e = IntPtr.Add(buf, off);
                    int nextOff = Marshal.ReadInt32(e, 0);
                    long pid = Marshal.ReadIntPtr(e, 80).ToInt64();
                    if (pid != 0)
                    {
                        long create = Marshal.ReadInt64(e, 32);
                        long time = Marshal.ReadInt64(e, 40) + Marshal.ReadInt64(e, 48);
                        int nameLen = Marshal.ReadInt16(e, 56) & 0xFFFF;
                        IntPtr namePtr = Marshal.ReadIntPtr(e, 64);
                        string name = nameLen > 0 && namePtr != IntPtr.Zero ? Marshal.PtrToStringUni(namePtr, nameLen / 2) : "System";
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                        long priv = Marshal.ReadIntPtr(e, 184).ToInt64();
                        long rd = Marshal.ReadInt64(e, 232), wr = Marshal.ReadInt64(e, 240);
                        long key = pid ^ create;
                        next[key] = new Prev { Time = time, Read = rd, Write = wr };

                        ProcInfo g;
                        if (!groups.TryGetValue(name, out g)) groups[name] = g = new ProcInfo { Name = name };
                        g.Count++;
                        g.Private += priv;
                        Prev p;
                        if (havePrev && prev.TryGetValue(key, out p))
                        {
                            g.Cpu += Math.Max(0, (time - p.Time) / cpuDen * 100);
                            g.IoRead += Math.Max(0, (rd - p.Read) / sec);
                            g.IoWrite += Math.Max(0, (wr - p.Write) / sec);
                        }
                    }
                    if (nextOff == 0) break;
                    off += nextOff;
                }
                prev = next;
                prevTime = now;
                return groups.Values.ToList();
            }
            catch { return null; }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        }
    }

    static class Fmt
    {
        public static string Bytes(double b)
        {
            if (b >= 1L << 40) return (b / (1L << 40)).ToString("0.0") + " TB";
            if (b >= 1L << 30) return (b / (1L << 30)).ToString(b >= 100.0 * (1L << 30) ? "0" : "0.0") + " GB";
            if (b >= 1L << 20) return (b / (1L << 20)).ToString("0") + " MB";
            return Math.Max(0, b / 1024).ToString("0") + " KB";
        }

        public static string Rate(double b)
        {
            if (b >= 1 << 30) return (b / (1 << 30)).ToString("0.0") + " GB/s";
            if (b >= 1 << 20) return (b / (1 << 20)).ToString("0.0") + " MB/s";
            return (b / 1024).ToString("0") + " KB/s";
        }

        public static string Duration(TimeSpan t)
        {
            if (t.TotalDays >= 1) return L.F("{0} g {1} h", "{0}d {1}h", (int)t.TotalDays, t.Hours);
            if (t.TotalHours >= 1) return L.F("{0} h {1} min", "{0}h {1}m", (int)t.TotalHours, t.Minutes);
            return L.F("{0} min", "{0}m", Math.Max(0, t.Minutes));
        }

        public static string Pct(double v) { return v.ToString("0") + "%"; }
    }
}
