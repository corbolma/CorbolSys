// Versione condivisa da CorbolSys.exe e dall'installer. Cambiala qui (o con bump-version.ps1) prima di una nuova distribuzione.
using System.Reflection;

[assembly: AssemblyProduct("CorbolSys")]
[assembly: AssemblyCopyright(CorbolSys.AppVersion.Copyright)]
[assembly: AssemblyVersion("1.1.3.0")]
[assembly: AssemblyFileVersion("1.1.3.0")]

namespace CorbolSys
{
    static class AppVersion
    {
        public const string Author = "Mauro Corboli";
        public const string Copyright = "Copyright © 2026 " + Author + " - MIT License";
        public const string RepoUrl = "https://github.com/corbolma/CorbolSys";
        public const string Text = "1.1.3";
    }
}
