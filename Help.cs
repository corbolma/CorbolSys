// Guida: apre la wiki su GitHub nella lingua dell'app (italiano, altrimenti inglese), alla pagina o sezione giusta.
// Il manuale è la wiki: qui ci sono solo i collegamenti. Si apre nel browser solo quando l'utente lo chiede
// (voce Guida del menu, pulsante "?" nelle finestre, tasto F1).
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace CorbolSys
{
    static class Help
    {
        public const string Guide = "", Analysis = "analysis", Space = "space", Cleanup = "cleanup", Assistant = "ai", Ports = "ports";
        const string Wiki = "https://github.com/corbolma/CorbolSys/wiki/";

        // Pagina e sezione (ancore generate da GitHub dai titoli della wiki)
        public static string Url(string topic)
        {
            bool it = L.Code == "it";
            switch (topic)
            {
                case Analysis: return Wiki + (it ? "Analisi-approfondita" : "In-depth-analysis");
                case Assistant: return Wiki + (it ? "Assistente-AI" : "AI-assistant");
                case Space: return Wiki + (it ? "Guida-utente#spazio-su-disco" : "User-guide#disk-space");
                case Cleanup: return Wiki + (it ? "Guida-utente#pulizia-disco" : "User-guide#disk-cleanup");
                case Ports: return Wiki + (it ? "Guida-utente#porte-aperte" : "User-guide#open-ports");
                default: return Wiki + (it ? "Guida-utente" : "User-guide");
            }
        }

        public static void Open(string topic)
        {
            try { Process.Start(new ProcessStartInfo(Url(topic)) { UseShellExecute = true }); } catch { }
        }

        // Pulsante "?" (30x30 a 9 pt) e tasto F1 nella finestra
        public static Button Button(Form f, string topic, Point location)
        {
            var b = new Button { Text = "?", Size = new Size(30, 30), Location = location, TabStop = false };
            new ToolTip().SetToolTip(b, L.T("Guida (F1)", "Help (F1)"));
            b.Click += delegate { Open(topic); };
            f.HelpRequested += (s, e) => { Open(topic); e.Handled = true; };
            return b;
        }
    }
}
