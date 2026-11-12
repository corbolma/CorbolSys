// Versione condivisa da AppSys.exe e dall'installer. Cambiala qui (o con bump-version.ps1) prima di una nuova distribuzione.
using System.Reflection;

[assembly: AssemblyProduct("AppSys")]
[assembly: AssemblyCopyright(AppSys.AppVersion.Copyright)]
[assembly: AssemblyVersion("1.0.6.0")]
[assembly: AssemblyFileVersion("1.0.6.0")]

namespace AppSys
{
    static class AppVersion
    {
        public const string Author = "Mauro Corboli";
        public const string Copyright = "Copyright © 2026 " + Author + " - MIT License";
        public const string RepoUrl = "https://github.com/corbolma/AppSys";
        public const string Text = "1.0.6";
    }
}
