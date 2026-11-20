// Porte aperte: le porte TCP e UDP in ascolto su questo PC, con il programma che le apre, da dove sono
// raggiungibili (solo dal PC, da una rete, da tutte), cosa dice il firewall di Windows e una valutazione a regole
// (normale / da guardare / sospetta) con il motivo. Non è un antivirus: dice cosa il PC espone alla rete.
// Tutto in sola lettura, senza permessi di amministratore e senza traffico di rete (la firma dei programmi si
// verifica senza scaricare elenchi di revoca).
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CorbolSys
{
    enum PortRisk { Normal, Info, Check, Suspicious }
    enum PortReach { ThisPc, OneNetwork, AllNetworks }

    class OpenPort
    {
        public string Proto;              // TCP o UDP
        public int Port;
        public string Address;            // indirizzo su cui ascolta
        public PortReach Reach;
        public int Pid;
        public string Process = "", Path = "", Services = "", Publisher = "";
        public bool Windows;              // componente di Windows (cartella di Windows o processo System)
        public bool Signed;               // firma digitale valida (fuori dalla cartella di Windows)
        public bool? FirewallOpen;        // null = non noto
        public PortRisk Risk = PortRisk.Normal;
        public string Why = "";

        public string ProcessLabel
        {
            get { return (Process.Length > 0 ? Process : "PID " + Pid) + (Services.Length > 0 ? " (" + Services + ")" : ""); }
        }

        public string ReachText
        {
            get
            {
                switch (Reach)
                {
                    case PortReach.ThisPc: return L.T("solo da questo PC", "from this PC only");
                    case PortReach.OneNetwork: return L.F("dalla rete di {0}", "from the network of {0}", Address);
                    default: return L.T("da tutte le reti", "from all networks");
                }
            }
        }

        public string FirewallText
        {
            get
            {
                if (Reach == PortReach.ThisPc) return "-";
                if (!FirewallOpen.HasValue) return L.T("non noto", "unknown");
                if (!Ports.FirewallOn) return L.T("firewall spento", "firewall off");
                return FirewallOpen.Value ? L.T("lascia entrare", "allows in") : L.T("blocca", "blocks");
            }
        }

        public string RiskText
        {
            get
            {
                switch (Risk)
                {
                    case PortRisk.Suspicious: return L.T("sospetta", "suspicious");
                    case PortRisk.Check: return L.T("da guardare", "to check");
                    case PortRisk.Info: return L.T("da sapere", "good to know");
                    default: return L.T("normale", "normal");
                }
            }
        }

        public string SignText
        {
            get
            {
                if (Windows) return L.T("componente di Windows", "Windows component");
                if (Path.Length == 0) return L.T("firma non verificabile", "signature not verifiable");
                return Signed ? L.F("firmato da {0}", "signed by {0}", Publisher.Length > 0 ? Publisher : "?") : L.T("senza firma digitale", "no digital signature");
            }
        }

        // Una riga per il report e per l'assistente
        public string Line()
        {
            var sb = new StringBuilder();
            sb.Append(Proto + " " + Port + " - " + ProcessLabel);
            if (Path.Length > 0) sb.Append(" - " + Path);
            sb.Append(" - " + SignText);
            sb.Append(" - " + L.T("raggiungibile", "reachable") + " " + ReachText);
            if (Reach != PortReach.ThisPc) sb.Append(" - firewall: " + FirewallText);
            sb.Append(" - " + RiskText + (Why.Length > 0 ? ": " + Why : ""));
            return sb.ToString();
        }
    }

    static class Ports
    {
        // ---------- tabelle di Windows ----------
        [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);
        [DllImport("iphlpapi.dll")] static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

        const int AF_INET = 2, AF_INET6 = 23, TCP_LISTENER = 3, UDP_OWNER_PID = 1;

        static int SwapPort(uint p) { return (int)(((p & 0xFF) << 8) | ((p >> 8) & 0xFF)); }

        static IntPtr Table(bool tcp, int af)
        {
            int size = 0;
            if (tcp) GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_LISTENER, 0);
            else GetExtendedUdpTable(IntPtr.Zero, ref size, false, af, UDP_OWNER_PID, 0);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                size += 4096;
                IntPtr buf = Marshal.AllocHGlobal(size);
                uint r = tcp ? GetExtendedTcpTable(buf, ref size, false, af, TCP_LISTENER, 0) : GetExtendedUdpTable(buf, ref size, false, af, UDP_OWNER_PID, 0);
                if (r == 0) return buf;
                Marshal.FreeHGlobal(buf);
            }
            return IntPtr.Zero;
        }

        // Righe: (indirizzo, porta, pid)
        static IEnumerable<Tuple<IPAddress, int, int>> Rows(bool tcp, int af)
        {
            var res = new List<Tuple<IPAddress, int, int>>();
            IntPtr buf = Table(tcp, af);
            if (buf == IntPtr.Zero) return res;
            try
            {
                int n = Marshal.ReadInt32(buf);
                IntPtr row = buf + 4;
                int rowSize = af == AF_INET ? (tcp ? 24 : 12) : (tcp ? 56 : 28);
                for (int i = 0; i < n; i++, row += rowSize)
                {
                    IPAddress addr; uint port; int pid;
                    if (af == AF_INET)
                    {
                        // TCP: stato, indirizzo, porta, ind. remoto, porta remota, pid; UDP: indirizzo, porta, pid
                        int o = tcp ? 4 : 0;
                        addr = new IPAddress((long)(uint)Marshal.ReadInt32(row, o));
                        port = (uint)Marshal.ReadInt32(row, o + 4);
                        pid = Marshal.ReadInt32(row, tcp ? 20 : 8);
                    }
                    else
                    {
                        var b = new byte[16];
                        Marshal.Copy(row, b, 0, 16);
                        addr = new IPAddress(b);
                        port = (uint)Marshal.ReadInt32(row, 20);
                        pid = Marshal.ReadInt32(row, tcp ? 52 : 24);
                    }
                    res.Add(Tuple.Create(addr, SwapPort(port), pid));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return res;
        }

        static string ImagePath(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION: basta per molti processi
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
            }
            finally { CloseHandle(h); }
        }

        // ---------- firma digitale (senza rete: niente controllo di revoca) ----------
        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WinTrustFileInfo { public int cbStruct; public IntPtr path; public IntPtr hFile; public IntPtr known; }

        [StructLayout(LayoutKind.Sequential)]
        struct WinTrustData
        {
            public int cbStruct; public IntPtr policy; public IntPtr sip; public int uiChoice; public int revocation;
            public int unionChoice; public IntPtr file; public int stateAction; public IntPtr state; public IntPtr url;
            public int provFlags; public int uiContext; public IntPtr signatureSettings;
        }

        static readonly Guid VerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        static bool VerifySignature(string path)
        {
            IntPtr pPath = Marshal.StringToHGlobalUni(path), pFile = IntPtr.Zero, pData = IntPtr.Zero;
            try
            {
                var fi = new WinTrustFileInfo { cbStruct = Marshal.SizeOf(typeof(WinTrustFileInfo)), path = pPath };
                pFile = Marshal.AllocHGlobal(fi.cbStruct);
                Marshal.StructureToPtr(fi, pFile, false);
                var d = new WinTrustData
                {
                    cbStruct = Marshal.SizeOf(typeof(WinTrustData)), uiChoice = 2 /* nessuna finestra */, revocation = 0,
                    unionChoice = 1 /* file */, file = pFile,
                    provFlags = 0x10 | 0x1000   // nessun controllo di revoca, nessun download
                };
                pData = Marshal.AllocHGlobal(d.cbStruct);
                Marshal.StructureToPtr(d, pData, false);
                return WinVerifyTrust(IntPtr.Zero, VerifyV2, pData) == 0;
            }
            catch { return false; }
            finally
            {
                Marshal.FreeHGlobal(pPath);
                if (pFile != IntPtr.Zero) Marshal.FreeHGlobal(pFile);
                if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData);
            }
        }

        static string Signer(string path)
        {
            try
            {
                string subj = X509Certificate.CreateFromSignedFile(path).Subject;
                foreach (var part in subj.Split(','))
                {
                    string p = part.Trim();
                    if (p.StartsWith("O=")) return p.Substring(2).Trim('"');
                }
                foreach (var part in subj.Split(','))
                {
                    string p = part.Trim();
                    if (p.StartsWith("CN=")) return p.Substring(3).Trim('"');
                }
            }
            catch { }
            return "";
        }

        // ---------- servizi di Windows per processo (svchost) ----------
        // Senza permessi di amministratore il percorso dei processi di sistema non si legge, ma quello scritto nella
        // configurazione del servizio sì: servePath riceve l'eseguibile del servizio per ogni processo
        static Dictionary<int, List<string>> ServicesByPid(Dictionary<int, string> servicePath)
        {
            var res = new Dictionary<int, List<string>>();
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\cimv2", "SELECT ProcessId, DisplayName, PathName FROM Win32_Service WHERE State = 'Running'"))
                {
                    s.Options.Timeout = TimeSpan.FromSeconds(15);
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        int pid = Convert.ToInt32(o["ProcessId"]);
                        if (pid <= 0) continue;
                        List<string> l;
                        if (!res.TryGetValue(pid, out l)) res[pid] = l = new List<string>();
                        l.Add(Convert.ToString(o["DisplayName"]).Trim());
                        string exe = ExeFromCommand(Convert.ToString(o["PathName"] ?? ""));
                        if (exe.Length > 0 && !servicePath.ContainsKey(pid)) servicePath[pid] = exe;
                    }
                }
            }
            catch { }
            return res;
        }

        // "C:\Program Files\X\x.exe" -arg  oppure  C:\WINDOWS\system32\svchost.exe -k netsvcs
        static string ExeFromCommand(string cmd)
        {
            cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
            if (cmd.StartsWith("\"")) { int q = cmd.IndexOf('"', 1); return q > 1 ? cmd.Substring(1, q - 1) : ""; }
            int i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return i > 0 ? cmd.Substring(0, i + 4) : cmd;
        }

        // Processi di Windows che non sono servizi ma ospitano porte di sistema (RPC)
        static readonly string[] WindowsProcesses = { "lsass", "wininit", "services", "csrss", "smss", "svchost", "spoolsv", "dashost" };

        // ---------- firewall di Windows (lettura delle regole, senza permessi di amministratore) ----------
        class FwRule
        {
            public bool Allow;
            public string App = "", Service = "", Ports = "";
            public int Protocol, Profiles;
        }

        public static bool FirewallKnown, FirewallOn;
        static bool defaultBlock = true;
        static List<FwRule> rules = new List<FwRule>();

        static object Get(object o, string name, params object[] args)
        {
            return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, args.Length > 0 ? args : null);
        }

        static void LoadFirewall()
        {
            FirewallKnown = false;
            rules = new List<FwRule>();
            try
            {
                var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (t == null) return;
                object pol = Activator.CreateInstance(t);
                int profiles = Convert.ToInt32(Get(pol, "CurrentProfileTypes"));
                bool on = false, block = true;
                foreach (int p in new[] { 1, 2, 4 })
                {
                    if ((profiles & p) == 0) continue;
                    if (Convert.ToBoolean(Get(pol, "FirewallEnabled", p))) on = true;
                    if (Convert.ToInt32(Get(pol, "DefaultInboundAction", p)) == 1) block = false;
                }
                FirewallOn = on;
                defaultBlock = block;
                foreach (object r in (IEnumerable)Get(pol, "Rules"))
                {
                    try
                    {
                        if (Convert.ToInt32(Get(r, "Direction")) != 1 || !Convert.ToBoolean(Get(r, "Enabled"))) continue;   // solo in entrata, attive
                        int prof = Convert.ToInt32(Get(r, "Profiles"));
                        if ((prof & profiles) == 0) continue;
                        string app = Convert.ToString(Get(r, "ApplicationName") ?? "");
                        rules.Add(new FwRule
                        {
                            Allow = Convert.ToInt32(Get(r, "Action")) == 1,
                            App = app.Length > 0 ? Environment.ExpandEnvironmentVariables(app) : "",
                            Service = Convert.ToString(Get(r, "ServiceName") ?? ""),
                            Ports = Convert.ToString(Get(r, "LocalPorts") ?? "*"),
                            Protocol = Convert.ToInt32(Get(r, "Protocol")),
                            Profiles = prof
                        });
                    }
                    catch { }
                    finally { if (Marshal.IsComObject(r)) Marshal.ReleaseComObject(r); }
                }
                FirewallKnown = true;
            }
            catch { }
        }

        static bool PortMatches(string spec, int port)
        {
            if (string.IsNullOrEmpty(spec) || spec == "*") return true;
            foreach (var raw in spec.Split(','))
            {
                string s = raw.Trim();
                if (s == "RPC-EPMap") { if (port == 135) return true; continue; }
                if (s == "RPC") { if (port >= 49152) return true; continue; }
                int dash = s.IndexOf('-'), a, b;
                if (dash > 0 && int.TryParse(s.Substring(0, dash), out a) && int.TryParse(s.Substring(dash + 1), out b)) { if (port >= a && port <= b) return true; }
                else if (int.TryParse(s, out a) && a == port) return true;
            }
            return false;
        }

        // Il firewall lascia entrare connessioni dall'esterno su questa porta? (approssimato: le regole più fini,
        // come quelle per indirizzo remoto, non si considerano)
        static bool? FirewallAllows(OpenPort p, List<string> services)
        {
            if (!FirewallKnown) return null;
            if (!FirewallOn) return true;
            int proto = p.Proto == "TCP" ? 6 : 17;
            bool allow = false;
            foreach (var r in rules)
            {
                if (r.Protocol != 256 && r.Protocol != proto) continue;
                if (r.App.Length > 0 && !string.Equals(r.App, p.Path, StringComparison.OrdinalIgnoreCase)) continue;
                if (r.Service.Length > 0 && r.Service != "*" && (services == null || !services.Any(s => s.Equals(r.Service, StringComparison.OrdinalIgnoreCase)))) continue;
                if (!PortMatches(r.Ports, p.Port)) continue;
                if (!r.Allow) return false;   // le regole che bloccano vincono
                allow = true;
            }
            return allow || !defaultBlock;
        }

        // ---------- elenco ----------
        // Porte UDP dinamiche (dal 49152): sono quasi sempre programmi che fanno da client (browser, Teams...): si contano a parte
        public static int HiddenUdp;

        public static List<OpenPort> List()
        {
            var raw = new List<OpenPort>();
            foreach (bool tcp in new[] { true, false })
                foreach (int af in new[] { AF_INET, AF_INET6 })
                    foreach (var r in Rows(tcp, af))
                    {
                        var p = new OpenPort { Proto = tcp ? "TCP" : "UDP", Port = r.Item2, Pid = r.Item3 };
                        var a = r.Item1;
                        if (IPAddress.IsLoopback(a)) p.Reach = PortReach.ThisPc;
                        else if (a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) p.Reach = PortReach.AllNetworks;
                        else p.Reach = PortReach.OneNetwork;
                        p.Address = a.ToString();
                        raw.Add(p);
                    }
            // la stessa porta su IPv4 e IPv6 (o su più indirizzi) diventa una riga sola, con la raggiungibilità più ampia
            var list = new List<OpenPort>();
            foreach (var g in raw.GroupBy(x => x.Proto + "|" + x.Port + "|" + x.Pid))
            {
                var best = g.OrderByDescending(x => (int)x.Reach).First();
                list.Add(best);
            }
            HiddenUdp = list.Count(p => p.Proto == "UDP" && p.Port >= 49152);
            list.RemoveAll(p => p.Proto == "UDP" && p.Port >= 49152);

            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var servicePath = new Dictionary<int, string>();
            var services = ServicesByPid(servicePath);
            LoadFirewall();
            var signCache = new Dictionary<string, Tuple<bool, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in list)
            {
                if (p.Pid == 4 || p.Pid == 0) { p.Process = "System"; p.Windows = true; }
                else
                {
                    try { using (var pr = Process.GetProcessById(p.Pid)) p.Process = pr.ProcessName; } catch { }
                    p.Path = ImagePath(p.Pid);
                    string sp;
                    if (p.Path.Length == 0 && servicePath.TryGetValue(p.Pid, out sp) && File.Exists(sp)) p.Path = sp;
                    if (p.Path.Length > 0 && p.Path.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase)) p.Windows = true;
                    else if (p.Path.Length == 0 && WindowsProcesses.Contains(p.Process.ToLowerInvariant())) p.Windows = true;
                    else if (p.Path.Length > 0)
                    {
                        Tuple<bool, string> s;
                        if (!signCache.TryGetValue(p.Path, out s))
                        {
                            bool ok = VerifySignature(p.Path);
                            signCache[p.Path] = s = Tuple.Create(ok, ok ? Signer(p.Path) : "");
                        }
                        p.Signed = s.Item1;
                        p.Publisher = s.Item2;
                    }
                }
                List<string> sv;
                services.TryGetValue(p.Pid, out sv);
                if (sv != null) p.Services = string.Join(", ", sv.Take(3)) + (sv.Count > 3 ? ", ..." : "");
                if (p.Reach != PortReach.ThisPc) p.FirewallOpen = FirewallAllows(p, sv);
                Judge(p);
            }
            return list.OrderByDescending(p => (int)p.Risk).ThenBy(p => p.Reach == PortReach.ThisPc ? 1 : 0).ThenBy(p => p.Proto).ThenBy(p => p.Port).ToList();
        }

        // ---------- valutazione ----------
        static readonly int[] BackdoorPorts = { 1337, 4444, 5554, 6666, 6667, 6668, 6669, 12345, 12346, 20034, 27374, 31337, 54321 };
        static readonly string[] RemoteTools = { "anydesk", "teamviewer", "teamviewer_service", "rustdesk", "radmin", "rserver3", "screenconnect.clientservice",
                                                 "splashtop", "srservice", "tvnserver", "winvnc", "vncserver", "ultravnc", "remotepc", "dwrcs", "ammyy",
                                                 "cagservice", "aemagent", "ltsvc", "ninjarmmagent", "kaseya", "agentmon" };
        static readonly string[] DevServers = { "node", "python", "pythonw", "java", "javaw", "php", "php-cgi", "ruby", "dotnet", "iisexpress", "httpd", "nginx",
                                                "postgres", "mysqld", "mariadbd", "mongod", "redis-server", "sqlservr", "com.docker.backend", "vpnkit", "wslrelay",
                                                "code", "idea64", "rider64", "devenv", "ollama", "llama-server", "jupyter", "streamlit" };

        static bool InUserFolder(string path)
        {
            if (path.Length == 0) return false;
            foreach (var dir in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), System.IO.Path.GetTempPath().TrimEnd('\\'),
                Environment.GetEnvironmentVariable("PUBLIC") ?? ""
            })
                if (dir.Length > 0 && path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static void Judge(OpenPort p)
        {
            string name = p.Process.ToLowerInvariant();
            bool net = p.Reach != PortReach.ThisPc;
            bool blocked = p.FirewallOpen.HasValue && !p.FirewallOpen.Value;
            Action<PortRisk, string> set = (r, why) => { p.Risk = r; p.Why = why; };

            if (!net) { set(PortRisk.Normal, L.T("raggiungibile solo da questo PC", "reachable only from this PC")); return; }

            if (BackdoorPorts.Contains(p.Port) && !p.Windows)
                set(PortRisk.Suspicious, L.T("porta usata spesso da programmi di controllo nascosto: verifica che il programma sia uno che conosci",
                                              "port often used by hidden remote-control programs: check that the program is one you know"));
            else if (!p.Windows && !p.Signed && InUserFolder(p.Path))
                set(PortRisk.Suspicious, L.T("programma senza firma digitale, in una cartella dell'utente, che accetta connessioni dalla rete",
                                              "program without a digital signature, in a user folder, that accepts connections from the network"));
            else if (RemoteTools.Contains(name))
                set(PortRisk.Check, L.T("programma di controllo remoto: va bene solo se lo usi tu o l'assistenza",
                                        "remote-control program: fine only if you or IT support use it"));
            else if (p.Proto == "TCP" && p.Port == 3389)
                set(PortRisk.Check, L.T("Desktop remoto acceso: chi è nella rete può provare ad accedere al PC; spegnilo se non lo usi",
                                        "Remote Desktop is on: anyone on the network can try to sign in to the PC; turn it off if you don't use it"));
            else if (p.Proto == "TCP" && p.Port >= 5900 && p.Port <= 5903)
                set(PortRisk.Check, L.T("server VNC (controllo remoto, spesso senza cifratura)", "VNC server (remote control, often unencrypted)"));
            else if (p.Proto == "TCP" && p.Port == 22)
                set(PortRisk.Info, L.T("server SSH: accesso remoto cifrato al PC", "SSH server: encrypted remote access to the PC"));
            else if (p.Proto == "TCP" && (p.Port == 21 || p.Port == 23))
                set(PortRisk.Check, L.T("servizio vecchio e non cifrato (FTP o Telnet): le password passano in chiaro", "old unencrypted service (FTP or Telnet): passwords travel in clear text"));
            else if (DevServers.Contains(name) && p.Reach == PortReach.AllNetworks)
                set(PortRisk.Check, L.T("server di sviluppo o database aperto a tutta la rete: se non serve, fallo ascoltare solo su 127.0.0.1",
                                        "development server or database open to the whole network: if not needed, make it listen on 127.0.0.1 only"));
            else if (!p.Windows && p.Path.Length > 0 && !p.Signed)
                set(PortRisk.Check, L.T("programma senza firma digitale che accetta connessioni dalla rete", "program without a digital signature that accepts connections from the network"));
            else if (p.Windows && p.Proto == "TCP" && (p.Port == 445 || p.Port == 139))
                set(PortRisk.Info, L.T("condivisione di file e stampanti di Windows", "Windows file and printer sharing"));
            else if (p.Path.Length == 0 && !p.Windows)
                set(PortRisk.Info, L.T("programma non identificabile senza permessi di amministratore", "program not identifiable without administrator rights"));
            else
                set(PortRisk.Normal, p.Windows ? L.T("servizio di Windows", "Windows service") : L.F("programma firmato da {0}", "program signed by {0}", p.Publisher.Length > 0 ? p.Publisher : "?"));

            // il firewall la blocca: dall'esterno non si entra, il rischio scende di un gradino
            if (blocked && p.Risk > PortRisk.Normal)
            {
                p.Risk = p.Risk == PortRisk.Suspicious ? PortRisk.Check : PortRisk.Info;
                p.Why += " " + L.T("(il firewall però la blocca)", "(but the firewall blocks it)");
            }
        }
    }
}
