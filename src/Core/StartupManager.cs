using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OptiWin
{
    public sealed class StartupItem : Bindable
    {
        public string Name { get; set; }
        public string Command { get; set; }
        public string Source { get; set; }
        public string Publisher { get; set; }
        public bool OftenUseless { get; set; }
        public bool NeedsAdmin { get; set; }
        public bool CanToggle { get { return !NeedsAdmin || Os.IsAdmin; } }
        public string Meta { get { return string.IsNullOrEmpty(Publisher) ? Source : Publisher + " · " + Source; } }
        public string ExePath;
        internal RegistryHive ApprovedHive;
        internal string ApprovedKey, ApprovedName;

        bool enabled;
        public bool IsOn
        {
            get { return enabled; }
            set
            {
                if (value == enabled) return;
                try
                {
                    StartupManager.SetEnabled(this, value);
                    enabled = value;
                }
                catch (Exception e)
                {
                    Logger.Error("Démarrage " + Name, e);
                    if (Failed != null) Failed(this, e);
                }
                Raise("IsOn");
            }
        }
        internal void Init(bool on) { enabled = on; }
        public static Action<StartupItem, Exception> Failed;
        /// <summary>Ouvre l'Explorateur sur le programme concerné (lié au bouton de la ligne).</summary>
        public System.Windows.Input.ICommand RevealCmd { get; set; }
        public bool CanReveal { get { return ExePath != null && File.Exists(ExePath); } }

        /// <summary>Vrai si le nom ou la commande contient le texte cherché.</summary>
        public bool Matches(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            var q = query.Trim();
            return (Name ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0
                || (Command ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0
                || (Publisher ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }
    }

    /// <summary>
    /// Active ou désactive les programmes de démarrage avec le mécanisme du Gestionnaire des tâches
    /// (StartupApproved) : rien n'est supprimé, tout reste réversible et visible dans Windows.
    /// </summary>
    public static class StartupManager
    {
        const string RUN = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string RUN32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        const string APPROVED = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        static readonly Regex Useless = new Regex("onedrive|teams|spotify|discord|steam|epic|adobe|acrobat|creative cloud|skype|itunes|ccleaner|dropbox|autolaunch|updater|update|zoom|battle\\.net|origin|ubisoft|riot|cortana|phone ?link", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<StartupItem> List()
        {
            var r = new List<StartupItem>();
            ReadRun(r, RegistryHive.CurrentUser, RUN, "Run", "Registre (utilisateur)", false);
            ReadRun(r, RegistryHive.LocalMachine, RUN, "Run", "Registre (tous les utilisateurs)", true);
            ReadRun(r, RegistryHive.LocalMachine, RUN32, "Run32", "Registre 32 bits", true);
            ReadFolder(r, Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser, "Dossier Démarrage (utilisateur)", false);
            ReadFolder(r, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine, "Dossier Démarrage (tous les utilisateurs)", true);
            return r.OrderBy(i => i.IsOn ? 0 : 1).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        static void ReadRun(List<StartupItem> list, RegistryHive hive, string key, string approved, string source, bool admin)
        {
            try
            {
                using (var root = Reg.Root(hive))
                using (var k = root.OpenSubKey(key))
                {
                    if (k == null) return;
                    foreach (var name in k.GetValueNames())
                    {
                        if (string.IsNullOrEmpty(name)) continue;
                        var cmd = Convert.ToString(k.GetValue(name));
                        list.Add(Make(name, cmd, source, ExeFrom(cmd), hive, APPROVED + approved, name, admin));
                    }
                }
            }
            catch (Exception e) { Logger.Error("Lecture " + key, e); }
        }

        static void ReadFolder(List<StartupItem> list, string dir, RegistryHive hive, string source, bool admin)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                foreach (var f in Directory.GetFiles(dir))
                {
                    var fn = Path.GetFileName(f);
                    if (fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    var exe = f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveLnk(f) : f;
                    list.Add(Make(Path.GetFileNameWithoutExtension(fn), exe ?? f, source, exe, hive, APPROVED + "StartupFolder", fn, admin));
                }
            }
            catch (Exception e) { Logger.Error("Lecture " + dir, e); }
        }

        static StartupItem Make(string name, string cmd, string source, string exe, RegistryHive hive, string approvedKey, string approvedName, bool admin)
        {
            var it = new StartupItem
            {
                Name = name, Command = cmd, Source = source, ExePath = exe, NeedsAdmin = admin,
                ApprovedHive = hive, ApprovedKey = approvedKey, ApprovedName = approvedName,
                OftenUseless = Useless.IsMatch(name) || (exe != null && Useless.IsMatch(Path.GetFileName(exe)))
            };
            it.Publisher = PublisherOf(exe);
            it.Init(IsEnabled(hive, approvedKey, approvedName));
            return it;
        }

        static bool IsEnabled(RegistryHive hive, string key, string name)
        {
            var b = Reg.Get(hive, key, name) as byte[];
            // Absent = activé. Premier octet impair = désactivé (convention du Gestionnaire des tâches).
            return b == null || b.Length == 0 || (b[0] & 1) == 0;
        }

        internal static void SetEnabled(StartupItem it, bool on)
        {
            var data = new byte[12];
            data[0] = (byte)(on ? 2 : 3);
            if (!on) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            using (var root = Reg.Root(it.ApprovedHive))
            using (var k = root.CreateSubKey(it.ApprovedKey))
                k.SetValue(it.ApprovedName, data, RegistryValueKind.Binary);
            Store.AddHistory("startup:" + it.Name, "Démarrage : " + it.Name, on ? "Activé" : "Désactivé");
        }

        public static string ExeFrom(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
            if (cmd.StartsWith("\""))
            {
                var e = cmd.IndexOf('"', 1);
                return e > 1 ? cmd.Substring(1, e - 1) : null;
            }
            var i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return i > 0 ? cmd.Substring(0, i + 4) : cmd.Split(' ')[0];
        }

        static string ResolveLnk(string lnk)
        {
            try
            {
                dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                dynamic sc = sh.CreateShortcut(lnk);
                string t = sc.TargetPath;
                return string.IsNullOrEmpty(t) ? null : t;
            }
            catch { return null; }
        }

        static string PublisherOf(string exe)
        {
            try
            {
                if (exe == null || !File.Exists(exe)) return "";
                return (FileVersionInfo.GetVersionInfo(exe).CompanyName ?? "").Trim();
            }
            catch { return ""; }
        }

        public static void Reveal(StartupItem it)
        {
            try
            {
                if (it.ExePath != null && File.Exists(it.ExePath)) Process.Start("explorer.exe", "/select,\"" + it.ExePath + "\"");
            }
            catch { }
        }
    }
}
