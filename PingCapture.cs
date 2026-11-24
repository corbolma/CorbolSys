// Cattura dei ping con pktmon (Monitoraggio pacchetti, incluso in Windows 10 2004 e successivi).
// Un'altra strada, accanto al registro del firewall (PingWatch.cs), per sapere chi fa ping al PC: pktmon vede i
// pacchetti sulla scheda di rete, prima del firewall, quindi registra anche i ping che il registro non scrive.
// - "Cattura i ping..." (<app>.exe /pingcapture start <SID>, come amministratore): filtri solo ICMP e ICMPv6, solo
//   sulle schede di rete, file in %ProgramData%\<app>\Pktmon leggibile solo dall'utente e dagli amministratori.
//   pktmon continua anche a CorbolSys chiusa, finché non la si ferma o si riavvia il PC.
// - "Ferma la cattura" (/pingcapture stop <SID>): pktmon stop, conversione in pcapng, filtri tolti. Il pcapng lo
//   legge poi l'app senza permessi: richieste di eco ricevute, con indirizzo e se il PC ha risposto.
// - /pingcapture off: ferma e cancella tutto (disinstallazione).
// I filtri di pktmon sono di tutto il PC: avviando e fermando la cattura si tolgono anche quelli messi da altri.
// Nessun traffico di rete: gli indirizzi non vengono risolti in nomi.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace CorbolSys
{
    static class PingCapture
    {
        const string App = "CorbolSys";
        const int FileSizeMb = 64;

        public static string Dir
        {
            get { return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), App), "Pktmon"); }
        }
        static string EtlFile { get { return Path.Combine(Dir, "ping.etl"); } }
        static string PcapFile { get { return Path.Combine(Dir, "ping.pcapng"); } }
        static string StateFile { get { return Path.Combine(Dir, "state.txt"); } }

        static string PktmonExe
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pktmon.exe"); }
        }
        public static bool Available { get { try { return File.Exists(PktmonExe); } catch { return false; } } }

        // ---------- stato (state.txt, scritto come amministratore e letto dall'utente) ----------
        public enum State { None, Running, Interrupted, Done }

        // Running = in corso; Interrupted = il PC è stato riavviato dopo l'avvio (pktmon si è fermata, il file c'è);
        // Done = fermata, risultati in ping.pcapng
        public static State Current(out DateTime started, out DateTime stopped)
        {
            started = stopped = DateTime.MinValue;
            try
            {
                if (!File.Exists(StateFile)) return State.None;
                var parts = File.ReadAllText(StateFile).Trim().Split(' ');
                long a, b;
                if (parts.Length >= 2 && long.TryParse(parts[1], out a)) started = new DateTime(a, DateTimeKind.Utc).ToLocalTime();
                if (parts.Length >= 3 && long.TryParse(parts[2], out b)) stopped = new DateTime(b, DateTimeKind.Utc).ToLocalTime();
                if (parts[0] == "done") return State.Done;
                if (parts[0] != "running") return State.None;
                DateTime boot = DateTime.Now - TimeSpan.FromMilliseconds(GetTickCount64());
                if (started < boot) return State.Interrupted;
                return State.Running;
            }
            catch { return State.None; }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern ulong GetTickCount64();

        public static State Current()
        {
            DateTime a, b;
            return Current(out a, out b);
        }

        // ---------- comandi come amministratore ----------
        // Chiamare fuori dal thread dell'interfaccia. Ritorna il codice d'uscita: 0 ok, 2 pktmon già in uso da
        // un'altra cattura, -1 conferma annullata, altro = non riuscito.
        public static int RunElevated(string what)
        {
            try
            {
                var psi = new ProcessStartInfo(System.Windows.Forms.Application.ExecutablePath,
                    "/pingcapture " + what + " " + WindowsIdentity.GetCurrent().User.Value)
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(120000)) return 1;
                    return p.ExitCode;
                }
            }
            catch (Win32Exception) { return -1; }   // conferma annullata
            catch { return 1; }
        }

        // Eseguito come amministratore (<app>.exe /pingcapture start|stop|off <SID dell'utente>)
        public static int Command(string what, string userSid)
        {
            try
            {
                switch (what.ToLowerInvariant())
                {
                    case "start": return Start(userSid);
                    case "stop": return Stop();
                    case "off":
                        if (Current() == State.Running) Pktmon("stop");
                        Pktmon("filter remove");
                        for (int i = 0; i < 10 && Directory.Exists(Dir); i++)
                        {
                            try { Directory.Delete(Dir, true); } catch { Thread.Sleep(500); }
                        }
                        return Directory.Exists(Dir) ? 1 : 0;
                }
            }
            catch { }
            return 1;
        }

        static int Start(string userSid)
        {
            if (Current() == State.Running) Pktmon("stop");   // una nostra cattura precedente
            else if (Pktmon("status").IndexOf(".etl", StringComparison.OrdinalIgnoreCase) >= 0) return 2;   // quella di qualcun altro

            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
            Directory.CreateDirectory(Dir);
            // la cartella la scrivono gli amministratori; l'utente che l'ha chiesto la legge e può cancellare i risultati
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            if (!string.IsNullOrEmpty(userSid))
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(userSid), FileSystemRights.Modify, inherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(Dir, sec);

            Pktmon("filter remove");
            int code;
            Pktmon("filter add CorbolSysPing -t ICMP", out code);
            if (code != 0) return 1;
            Pktmon("filter add CorbolSysPing6 -t ICMPv6", out code);
            if (code != 0) { Pktmon("filter remove"); return 1; }
            Pktmon("start --capture --comp nics --pkt-size 128 --file-name \"" + EtlFile + "\" --file-size " + FileSizeMb, out code);
            if (code != 0) { Pktmon("filter remove"); return 1; }
            File.WriteAllText(StateFile, "running " + DateTime.UtcNow.Ticks);
            return 0;
        }

        static int Stop()
        {
            DateTime started, stopped;
            var st = Current(out started, out stopped);
            if (st == State.Running) Pktmon("stop");
            Pktmon("filter remove");
            if (st != State.Running && st != State.Interrupted) return 1;
            // se il PC è stato riavviato la cattura è finita allora: l'ora di fine è quella dell'ultimo pacchetto scritto
            DateTime end = st == State.Running ? DateTime.UtcNow : File.Exists(EtlFile) ? File.GetLastWriteTimeUtc(EtlFile) : DateTime.UtcNow;
            int code = 1;
            if (File.Exists(EtlFile))
            {
                Pktmon("etl2pcap \"" + EtlFile + "\" --out \"" + PcapFile + "\"", out code);
                try { File.Delete(EtlFile); } catch { }
            }
            File.WriteAllText(StateFile, "done " + started.ToUniversalTime().Ticks + " " + end.Ticks);
            return code == 0 && File.Exists(PcapFile) ? 0 : 1;
        }

        static string Pktmon(string args)
        {
            int code;
            return Pktmon(args, out code);
        }

        static string Pktmon(string args, out int code)
        {
            code = 1;
            try
            {
                var psi = new ProcessStartInfo(PktmonExe, args)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(60000);
                    code = p.ExitCode;
                    return o + err.Result;
                }
            }
            catch { return ""; }
        }

        // Dopo una cattura fermata, l'utente può cancellare i risultati senza permessi
        public static bool Discard()
        {
            if (Current() == State.Running) return false;
            try { Directory.Delete(Dir, true); return true; } catch { return false; }
        }

        // ---------- lettura dei risultati (pcapng) ----------
        // Richieste di eco ricevute; Allowed = il PC ha risposto. readable = false se il file non c'è o non si legge.
        public static List<PingEvent> Results(out bool readable)
        {
            return Parse(PcapFile, out readable);
        }

        class Packet
        {
            public DateTime Time;
            public IPAddress Src, Dst;
            public bool Request, V6;
            public int Id, Seq;
            public int Direction;   // 1 ricevuto, 2 inviato, 0 non noto
        }

        public static List<PingEvent> Parse(string file, out bool readable)
        {
            readable = false;
            var packets = new List<Packet>();
            try
            {
                if (!File.Exists(file)) return new List<PingEvent>();
                ReadPcapng(File.ReadAllBytes(file), packets);
                readable = true;
            }
            catch { return new List<PingEvent>(); }

            var mine = new HashSet<IPAddress>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    foreach (var u in ni.GetIPProperties().UnicastAddresses) mine.Add(u.Address);
            }
            catch { }

            // risposte inviate: a chi, con quale id e numero
            var replies = new HashSet<string>(packets.Where(p => !p.Request).Select(p => Key(p.Dst, p.Id, p.Seq)));
            var res = new List<PingEvent>();
            var seen = new Dictionary<string, DateTime>();
            foreach (var p in packets.Where(p => p.Request).OrderBy(p => p.Time))
            {
                if (p.Direction == 2) continue;   // ping fatti da questo PC
                if (IPAddress.IsLoopback(p.Src) || (p.Direction == 0 && mine.Contains(p.Src))) continue;
                // lo stesso pacchetto visto su più schede (per esempio uno switch virtuale)
                string k = Key(p.Src, p.Id, p.Seq);
                DateTime prev;
                if (seen.TryGetValue(k, out prev) && (p.Time - prev).TotalSeconds < 2) continue;
                seen[k] = p.Time;
                res.Add(new PingEvent { Time = p.Time, From = p.Src.ToString(), Allowed = replies.Contains(k) });
            }
            return res;
        }

        static string Key(IPAddress a, int id, int seq)
        {
            return a + "|" + id + "|" + seq;
        }

        // pcapng: Section Header, Interface Description (tipo di collegamento, risoluzione dell'ora), Enhanced/Simple Packet
        static void ReadPcapng(byte[] b, List<Packet> res)
        {
            int pos = 0;
            bool le = true;
            var links = new List<int>();
            var units = new List<double>();   // secondi per unità di tempo, per interfaccia
            Func<int, uint> u32 = o => le ? (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24)
                                          : (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
            Func<int, int> u16 = o => le ? b[o] | b[o + 1] << 8 : b[o] << 8 | b[o + 1];
            while (pos + 12 <= b.Length)
            {
                uint type = u32(pos);
                if (type == 0x0A0D0D0A)
                {
                    uint magic = (uint)(b[pos + 8] | b[pos + 9] << 8 | b[pos + 10] << 16 | b[pos + 11] << 24);
                    if (magic == 0x1A2B3C4D) le = true;
                    else if (magic == 0x4D3C2B1A) le = false;
                    else throw new InvalidDataException();
                    links.Clear(); units.Clear();
                }
                int len = (int)u32(pos + 4);
                if (len < 12 || pos + len > b.Length) break;
                int body = pos + 8, end = pos + len - 4;
                if (type == 1 && body + 8 <= end)   // Interface Description Block
                {
                    links.Add(u16(body));
                    double unit = 1e-6;
                    ForOptions(b, body + 8, end, u16, (code, o, l) =>
                    {
                        if (code == 9 && l >= 1)
                        {
                            int v = b[o] & 0x7F;
                            unit = (b[o] & 0x80) != 0 ? Math.Pow(2, -v) : Math.Pow(10, -v);
                        }
                    });
                    units.Add(unit);
                }
                else if (type == 6 && body + 20 <= end)   // Enhanced Packet Block
                {
                    int ifc = (int)u32(body);
                    ulong ts = (ulong)u32(body + 4) << 32 | u32(body + 8);
                    int cap = (int)u32(body + 12);
                    int data = body + 20;
                    if (cap >= 0 && data + cap <= end && ifc < links.Count)
                    {
                        var p = Decode(b, data, cap, links[ifc]);
                        if (p != null)
                        {
                            p.Time = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ts * units[ifc]).ToLocalTime();
                            int opts = data + ((cap + 3) & ~3);
                            ForOptions(b, opts, end, u16, (code, o, l) =>
                            {
                                if (code == 2 && l >= 4) p.Direction = (int)(u32(o) & 3);   // epb_flags: 1 in arrivo, 2 in uscita
                            });
                            res.Add(p);
                        }
                    }
                }
                pos += len;
            }
        }

        static void ForOptions(byte[] b, int o, int end, Func<int, int> u16, Action<int, int, int> each)
        {
            while (o + 4 <= end)
            {
                int code = u16(o), l = u16(o + 2);
                if (code == 0 || o + 4 + l > end) break;
                each(code, o + 4, l);
                o += 4 + ((l + 3) & ~3);
            }
        }

        // Una richiesta o risposta di eco ICMP/ICMPv6 dentro un pacchetto Ethernet o IP; null se è altro
        static Packet Decode(byte[] b, int o, int len, int link)
        {
            int end = o + len;
            if (link == 1)   // Ethernet, anche con VLAN
            {
                if (len < 14) return null;
                int eth = b[o + 12] << 8 | b[o + 13];
                o += 14;
                while ((eth == 0x8100 || eth == 0x88A8) && o + 4 <= end) { eth = b[o + 2] << 8 | b[o + 3]; o += 4; }
                if (eth != 0x0800 && eth != 0x86DD) return null;
            }
            else if (link == 0 && len >= 4) o += 4;   // loopback BSD
            else if (link != 101 && link != 228 && link != 229 && link != 12 && link != 14) return null;   // IP grezzo
            if (o >= end) return null;

            var p = new Packet();
            int ver = b[o] >> 4, icmp;
            if (ver == 4)
            {
                int ihl = (b[o] & 15) * 4;
                if (ihl < 20 || o + ihl + 8 > end || b[o + 9] != 1) return null;
                if ((b[o + 6] & 0x1F) != 0 || b[o + 7] != 0) return null;   // frammenti successivi
                p.Src = new IPAddress(Sub(b, o + 12, 4));
                p.Dst = new IPAddress(Sub(b, o + 16, 4));
                icmp = o + ihl;
                if (b[icmp] == 8) p.Request = true;
                else if (b[icmp] != 0) return null;
            }
            else if (ver == 6)
            {
                if (o + 40 + 8 > end) return null;
                int next = b[o + 6];
                p.Src = new IPAddress(Sub(b, o + 8, 16));
                p.Dst = new IPAddress(Sub(b, o + 24, 16));
                p.V6 = true;
                icmp = o + 40;
                // intestazioni di estensione (hop-by-hop, routing, destinazione)
                while ((next == 0 || next == 43 || next == 60) && icmp + 8 <= end)
                {
                    next = b[icmp];
                    icmp += (b[icmp + 1] + 1) * 8;
                }
                if (next != 58 || icmp + 8 > end) return null;
                if (b[icmp] == 128) p.Request = true;
                else if (b[icmp] != 129) return null;
            }
            else return null;
            p.Id = b[icmp + 4] << 8 | b[icmp + 5];
            p.Seq = b[icmp + 6] << 8 | b[icmp + 7];
            return p;
        }

        static byte[] Sub(byte[] b, int o, int n)
        {
            var r = new byte[n];
            Buffer.BlockCopy(b, o, r, 0, n);
            return r;
        }

        // ---------- riepilogo (analisi) ----------
        // Una riga se c'è una cattura in corso o fermata; null altrimenti
        public static string Summary()
        {
            DateTime started, stopped;
            switch (Current(out started, out stopped))
            {
                case State.Running:
                    return L.F("Cattura dei ping con pktmon in corso dalle {0}: fermala da Porte aperte > Ping ricevuti",
                               "Ping capture with pktmon running since {0}: stop it from Open ports > Pings received", started.ToString("g"));
                case State.Interrupted:
                    return L.T("Cattura dei ping con pktmon interrotta dal riavvio del PC: fermala da Porte aperte > Ping ricevuti per vedere i risultati",
                               "Ping capture with pktmon stopped by a restart: stop it from Open ports > Pings received to see the results");
                case State.Done:
                    bool readable;
                    var ev = Results(out readable);
                    if (!readable) return null;
                    return L.F("Cattura dei ping con pktmon ({0} - {1}): {2} ricevuti da {3} indirizzi",
                               "Ping capture with pktmon ({0} - {1}): {2} received from {3} addresses",
                               started.ToString("g"), stopped.ToString("t"), ev.Count, ev.Select(e => e.From).Distinct().Count());
            }
            return null;
        }
    }
}
