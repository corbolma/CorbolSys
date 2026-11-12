// Controlli che l'assistente AI può chiedere durante l'analisi, da una lista chiusa e tutti in sola lettura:
// processi, dettagli di un processo, registro eventi, cronologia delle risorse, cartelle più grandi,
// servizi automatici fermi, programmi installati di recente, programmi all'avvio.
// Niente comandi di sistema: WMI, registro, file system e API di .NET con argomenti controllati e tempi limitati.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace CorbolSys
{
    static class SysTools
    {
        public const int MaxCalls = 8;   // controlli al massimo per ogni risposta dell'assistente

        static Dictionary<string, object> Prop(string type, string desc)
        {
            return new Dictionary<string, object> { { "type", type }, { "description", desc } };
        }

        static Dictionary<string, object> EnumProp(string desc, params string[] values)
        {
            return new Dictionary<string, object> { { "type", "string" }, { "description", desc }, { "enum", values } };
        }

        static Dictionary<string, object> Schema(Dictionary<string, object> props, params string[] required)
        {
            return new Dictionary<string, object>
            {
                { "type", "object" }, { "properties", props }, { "required", required }, { "additionalProperties", false }
            };
        }

        public static List<AiTool> Definitions()
        {
            return new List<AiTool>
            {
                new AiTool { Name = "top_processes", Description = "Lists the running processes (grouped by name) sorted by current CPU, private memory or I/O, with CPU %, memory and read/write bytes per second.",
                             Schema = Schema(new Dictionary<string, object> { { "sort_by", EnumProp("Sort order", "cpu", "memory", "io") }, { "count", Prop("integer", "How many, 1-25") } }, "sort_by", "count") },
                new AiTool { Name = "process_details", Description = "Details of the processes with a given name: executable path, publisher and description from the file, version, start time, current CPU and memory. Useful to identify an unknown process.",
                             Schema = Schema(new Dictionary<string, object> { { "name", Prop("string", "Process name without .exe, e.g. msedge") } }, "name") },
                new AiTool { Name = "event_log", Description = "Reads recent entries from the Windows System or Application event log, newest first, filtered by minimum severity and optionally by provider (source) name.",
                             Schema = Schema(new Dictionary<string, object>
                             {
                                 { "log", EnumProp("Which log", "System", "Application") },
                                 { "level", EnumProp("Minimum severity", "critical", "error", "warning") },
                                 { "hours", Prop("integer", "How many hours back, 1-720") },
                                 { "provider", Prop("string", "Optional provider/source name, e.g. disk, Microsoft-Windows-Kernel-Power; empty for all") }
                             }, "log", "level", "hours", "provider") },
                new AiTool { Name = "resource_history", Description = "Resource usage recorded by CorbolSys on this PC (CPU, RAM, GPU, disk activity, temperature, network, heaviest process) summarized in time buckets. Up to 24 hours from memory, up to 168 hours if the history file is on.",
                             Schema = Schema(new Dictionary<string, object> { { "hours", Prop("integer", "How many hours back, 1-168") }, { "bucket_minutes", Prop("integer", "Minutes per row: 5, 15, 30 or 60") } }, "hours", "bucket_minutes") },
                new AiTool { Name = "largest_folders", Description = "Sizes of the subfolders of a local folder (largest first), to find what fills a disk. Takes up to 20 seconds; very large folders may be partially counted.",
                             Schema = Schema(new Dictionary<string, object> { { "path", Prop("string", "Absolute local folder, e.g. C:\\ or C:\\Users") } }, "path") },
                new AiTool { Name = "stopped_services", Description = "Lists Windows services set to start automatically that are not running (some are normal, e.g. delayed or trigger-start services).",
                             Schema = Schema(new Dictionary<string, object>()) },
                new AiTool { Name = "installed_programs", Description = "Lists installed programs with publisher, version and install date; with days > 0 only those installed in the last N days.",
                             Schema = Schema(new Dictionary<string, object> { { "days", Prop("integer", "0 for all, or the last N days (1-365)") } }, "days") },
                new AiTool { Name = "startup_programs", Description = "Lists the programs that start with Windows (Run registry keys and Startup folders), including disabled ones.",
                             Schema = Schema(new Dictionary<string, object>()) }
            };
        }

        static string Arg(AiToolCall c, string key)
        {
            object v;
            return c.Args != null && c.Args.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture).Trim() : "";
        }

        static int IntArg(AiToolCall c, string key, int def, int min, int max)
        {
            int v;
            if (!int.TryParse(Arg(c, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) v = def;
            return Math.Max(min, Math.Min(max, v));
        }

        // Descrizione breve per l'utente, mostrata mentre il controllo è in corso
        public static string Describe(AiToolCall c)
        {
            switch (c.Name)
            {
                case "top_processes": return L.F("processi per {0}", "processes by {0}", Arg(c, "sort_by"));
                case "process_details": return L.F("dettagli di {0}", "details of {0}", Arg(c, "name"));
                case "event_log": return L.F("registro {0} ({1}, {2} ore){3}", "{0} log ({1}, {2} hours){3}", Arg(c, "log"), Arg(c, "level"), Arg(c, "hours"), Arg(c, "provider").Length > 0 ? " " + Arg(c, "provider") : "");
                case "resource_history": return L.F("cronologia delle ultime {0} ore", "history of the last {0} hours", Arg(c, "hours"));
                case "largest_folders": return L.F("cartelle più grandi in {0}", "largest folders in {0}", Arg(c, "path"));
                case "stopped_services": return L.T("servizi automatici fermi", "stopped automatic services");
                case "installed_programs": return L.T("programmi installati", "installed programs");
                case "startup_programs": return L.T("programmi all'avvio", "startup programs");
            }
            return c.Name ?? "?";
        }

        public static string Execute(AiToolCall c, CancellationToken cancel)
        {
            switch (c.Name)
            {
                case "top_processes": return TopProcesses(Arg(c, "sort_by"), IntArg(c, "count", 10, 1, 25));
                case "process_details": return ProcessDetails(Arg(c, "name"));
                case "event_log": return EventLog(Arg(c, "log"), Arg(c, "level"), IntArg(c, "hours", 24, 1, 720), Arg(c, "provider"));
                case "resource_history":
                {
                    int h = IntArg(c, "hours", 24, 1, 168), b = IntArg(c, "bucket_minutes", 60, 5, 60);
                    b = b >= 60 ? 60 : b >= 30 ? 30 : b >= 15 ? 15 : 5;
                    if (h * 60 / b > 120) b = h * 60 / 120 >= 30 ? 60 : 30;   // al massimo ~120 righe
                    string t = Analysis.TrendText(h, b);
                    return t.Length > 0 ? "Resource history, " + b + "-minute buckets, values are averages:\n" + t
                                        : "No history recorded yet (CorbolSys records one summary per minute while running).";
                }
                case "largest_folders": return LargestFolders(Arg(c, "path"), cancel);
                case "stopped_services": return StoppedServices();
                case "installed_programs": return InstalledPrograms(IntArg(c, "days", 0, 0, 365));
                case "startup_programs":
                    return string.Join("\n", Analysis.StartupItems().Select(i => (i.Enabled ? "on " : "off ") + i.Name + " | " + Analysis.Short(i.Command, 150) + " | " + i.Where));
            }
            return "Error: unknown tool " + c.Name + ".";
        }

        static string TopProcesses(string sort, int count)
        {
            var procs = SysMonitor.Instance.Current.Procs;
            IEnumerable<ProcInfo> q = sort == "memory" ? procs.OrderByDescending(p => p.Private)
                                    : sort == "io" ? procs.OrderByDescending(p => p.IoRead + p.IoWrite)
                                    : procs.OrderByDescending(p => p.Cpu);
            var sb = new StringBuilder("name | instances | cpu % of whole PC | private memory | io read/s | io write/s\n");
            foreach (var p in q.Take(count))
                sb.AppendLine(p.Name + " | " + p.Count + " | " + p.Cpu.ToString("0.0", CultureInfo.InvariantCulture) + " | " + Fmt.Bytes(p.Private) + " | " + Fmt.Rate(p.IoRead) + " | " + Fmt.Rate(p.IoWrite));
            return sb.ToString();
        }

        static readonly Regex SafeName = new Regex(@"^[\w .()+\-]{1,80}$");

        static string ProcessDetails(string name)
        {
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            if (!SafeName.IsMatch(name)) return "Error: invalid process name.";
            var sb = new StringBuilder();
            var info = SysMonitor.Instance.Current.Procs.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (info != null) sb.AppendLine(name + ": " + info.Count + " instance(s), CPU " + info.Cpu.ToString("0.0", CultureInfo.InvariantCulture) + "%, private memory " + Fmt.Bytes(info.Private) + ", io " + Fmt.Rate(info.IoRead + info.IoWrite));
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var o in Analysis.Wmi(@"root\cimv2", "SELECT ProcessId, ParentProcessId, ExecutablePath, CreationDate FROM Win32_Process WHERE Name = '" + name.Replace("'", "") + ".exe'", 10))
            {
                if (n++ >= 5) break;
                string path = Analysis.Str(o, "ExecutablePath");
                DateTime start = DateTime.MinValue;
                try { start = System.Management.ManagementDateTimeConverter.ToDateTime(Analysis.Str(o, "CreationDate")); } catch { }
                sb.AppendLine("pid " + Analysis.Str(o, "ProcessId") + ", parent " + Analysis.Str(o, "ParentProcessId") + (start > DateTime.MinValue ? ", started " + start.ToString("yyyy-MM-dd HH:mm") : "") + (path.Length > 0 ? ", " + path : ", path not accessible"));
                if (path.Length > 0) paths.Add(path);
            }
            foreach (var p in paths.Take(3))
            {
                try
                {
                    var v = FileVersionInfo.GetVersionInfo(p);
                    sb.AppendLine("file " + p + ": company \"" + v.CompanyName + "\", description \"" + v.FileDescription + "\", product \"" + v.ProductName + "\", version " + v.FileVersion);
                }
                catch { }
            }
            return sb.Length > 0 ? sb.ToString() : "No running process named " + name + ".";
        }

        static string EventLog(string log, string level, int hours, string provider)
        {
            if (log != "System" && log != "Application") return "Error: log must be System or Application.";
            string lv = level == "critical" ? "Level=1" : level == "error" ? "(Level=1 or Level=2)" : "(Level=1 or Level=2 or Level=3)";
            string prov = "";
            if (provider.Length > 0)
            {
                if (!Regex.IsMatch(provider, @"^[\w .\-]{1,100}$")) return "Error: invalid provider name.";
                prov = " and Provider[@Name='" + provider + "']";
            }
            string xp = "*[System[" + lv + prov + " and TimeCreated[timediff(@SystemTime) <= " + ((long)hours * 3600000L).ToString(CultureInfo.InvariantCulture) + "]]]";
            var evs = Analysis.Events(log, xp, 400);
            if (evs.Count == 0) return "No matching events in the last " + hours + " hours.";
            var sb = new StringBuilder(evs.Count + (evs.Count >= 400 ? "+" : "") + " events. Grouped by source and id, newest first:\n");
            foreach (var g in evs.GroupBy(e => e.Provider + " " + e.Id).Take(25))
            {
                var e = g.First();
                string lvl = e.Level == 1 ? "critical" : e.Level == 2 ? "error" : e.Level == 3 ? "warning" : "info";
                sb.AppendLine("- " + g.Key + " (" + lvl + ") x" + g.Count() + ", last " + e.Time.ToString("yyyy-MM-dd HH:mm") + ": " + Analysis.Short(e.Message, 300));
            }
            return sb.ToString();
        }

        static string LargestFolders(string path, CancellationToken cancel)
        {
            if (path.Length < 2 || !Path.IsPathRooted(path) || path.StartsWith(@"\\")) return "Error: give an absolute local folder, e.g. C:\\Users.";
            if (path.Length == 2 && path[1] == ':') path += "\\";
            if (!Directory.Exists(path)) return "Error: folder not found.";
            var sw = Stopwatch.StartNew();
            var res = new List<KeyValuePair<string, long>>();
            string[] dirs;
            try { dirs = Directory.GetDirectories(path); } catch (Exception ex) { return "Error: " + ex.Message; }
            long files = 0;
            try { files = new DirectoryInfo(path).EnumerateFiles().Sum(f => f.Length); } catch { }
            bool partial = false;
            foreach (var d in dirs)
            {
                if (cancel.IsCancellationRequested) break;
                try { if ((new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                long left = 20000 - sw.ElapsedMilliseconds;
                if (left <= 0) { partial = true; break; }
                res.Add(new KeyValuePair<string, long>(Path.GetFileName(d), Analysis.FolderSize(d, (int)Math.Min(left, 6000))));
            }
            var sb = new StringBuilder("Folder " + path + (partial ? " (time limit reached: some folders not counted, sizes are minimums)" : " (sizes may be minimums for very large or protected folders)") + ":\n");
            foreach (var kv in res.OrderByDescending(k => k.Value).Take(20)) sb.AppendLine("- " + kv.Key + ": " + Fmt.Bytes(kv.Value));
            sb.AppendLine("- (files directly in this folder): " + Fmt.Bytes(files));
            return sb.ToString();
        }

        static string StoppedServices()
        {
            var sb = new StringBuilder();
            foreach (var o in Analysis.Wmi(@"root\cimv2", "SELECT Name, DisplayName, State, DelayedAutoStart FROM Win32_Service WHERE StartMode = 'Auto' AND State <> 'Running'"))
                sb.AppendLine("- " + Analysis.Str(o, "DisplayName") + " (" + Analysis.Str(o, "Name") + "): " + Analysis.Str(o, "State") + (Analysis.Str(o, "DelayedAutoStart") == "True" ? ", delayed start" : ""));
            return sb.Length > 0 ? sb.ToString() : "All automatic services are running.";
        }

        static string InstalledPrograms(int days)
        {
            var list = new List<Tuple<string, string, string, DateTime>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roots = new[]
            {
                Tuple.Create(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                Tuple.Create(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                Tuple.Create(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall")
            };
            foreach (var r in roots)
            {
                try
                {
                    using (var k = r.Item1.OpenSubKey(r.Item2))
                    {
                        if (k == null) continue;
                        foreach (var sub in k.GetSubKeyNames())
                            using (var s = k.OpenSubKey(sub))
                            {
                                if (s == null) continue;
                                string name = s.GetValue("DisplayName") as string;
                                if (string.IsNullOrEmpty(name) || s.GetValue("SystemComponent") is int && (int)s.GetValue("SystemComponent") == 1) continue;
                                if (!seen.Add(name)) continue;
                                DateTime d = DateTime.MinValue;
                                DateTime.TryParseExact(s.GetValue("InstallDate") as string ?? "", "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
                                list.Add(Tuple.Create(name, s.GetValue("Publisher") as string ?? "", s.GetValue("DisplayVersion") as string ?? "", d));
                            }
                    }
                }
                catch { }
            }
            IEnumerable<Tuple<string, string, string, DateTime>> q = list;
            if (days > 0) q = q.Where(t => t.Item4 >= DateTime.Today.AddDays(-days));
            var res = q.OrderByDescending(t => t.Item4).ThenBy(t => t.Item1).Take(150).ToList();
            if (res.Count == 0) return days > 0 ? "No programs installed in the last " + days + " days (by registered install date)." : "No programs found.";
            var sb = new StringBuilder(res.Count + " programs (name | publisher | version | install date):\n");
            foreach (var t in res) sb.AppendLine(t.Item1 + " | " + t.Item2 + " | " + t.Item3 + " | " + (t.Item4 > DateTime.MinValue ? t.Item4.ToString("yyyy-MM-dd") : "?"));
            return sb.ToString();
        }
    }
}
