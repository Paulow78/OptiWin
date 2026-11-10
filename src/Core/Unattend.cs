using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Text;

namespace OptiWin
{
    public sealed class UnattendOptions
    {
        /// <summary>Retire les blocages TPM, Secure Boot, RAM et processeur de l'installateur de Windows 11.</summary>
        public bool BypassHardware;
        /// <summary>Passe l'obligation de compte Microsoft et de connexion Internet.</summary>
        public bool BypassOnlineAccount;
        /// <summary>Nom du compte local à créer ; vide : Windows le demandera.</summary>
        public string LocalAccount = "";
        /// <summary>Empêche le chiffrement automatique du disque, coûteux sur un vieux PC.</summary>
        public bool NoBitLocker;
        /// <summary>Répond « non » aux questions de confidentialité de l'écran d'accueil.</summary>
        public bool SkipPrivacy;
        /// <summary>Reprend la langue et la région de ce PC.</summary>
        public bool UseCurrentLocale = true;
        public bool IsEmpty { get { return !BypassHardware && !BypassOnlineAccount && !NoBitLocker && !SkipPrivacy && string.IsNullOrWhiteSpace(LocalAccount); } }
    }

    /// <summary>
    /// Génère autounattend.xml. Aucune section de partitionnement n'y figure :
    /// l'installateur demandera toujours sur quel disque installer. Une clé ne peut donc pas
    /// effacer un disque toute seule.
    /// </summary>
    public static class Unattend
    {
        const string Ns = "urn:schemas-microsoft-com:unattend";
        const string Key = "31bf3856ad364e35";

        public static bool IsValidAccountName(string n)
        {
            if (string.IsNullOrWhiteSpace(n) || n.Length > 20) return false;
            return n.IndexOfAny("/\\[]:;|=,+*?<>\"%@".ToCharArray()) < 0 && !n.EndsWith(".");
        }

        static string Comp(string name, string arch, string inner)
        {
            return "    <component name=\"" + name + "\" processorArchitecture=\"" + arch + "\" publicKeyToken=\"" + Key + "\" language=\"neutral\" versionScope=\"nonSxS\">\n" + inner + "    </component>\n";
        }

        static string RunList(string element, string item, IEnumerable<string> cmds, string desc)
        {
            var b = new StringBuilder("      <" + element + ">\n");
            int i = 1;
            foreach (var c in cmds)
            {
                b.Append("        <" + item + " wcm:action=\"add\"><Order>" + i++ + "</Order><Path>" + SecurityElement.Escape(c) + "</Path></" + item + ">\n");
            }
            b.Append("      </" + element + ">\n");
            return b.ToString();
        }

        public static string Build(UnattendOptions o, string arch)
        {
            var x = new StringBuilder();
            x.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<unattend xmlns=\"" + Ns + "\" xmlns:wcm=\"http://schemas.microsoft.com/WMIConfig/2002/State\">\n");

            if (o.BypassHardware)
            {
                var cmds = new[] { "BypassTPMCheck", "BypassSecureBootCheck", "BypassRAMCheck", "BypassCPUCheck", "BypassStorageCheck" }
                    .Select(v => "reg add HKLM\\SYSTEM\\Setup\\LabConfig /v " + v + " /t REG_DWORD /d 1 /f");
                x.Append("  <settings pass=\"windowsPE\">\n");
                x.Append(Comp("Microsoft-Windows-Setup", arch, RunList("RunSynchronous", "RunSynchronousCommand", cmds, "")));
                x.Append("  </settings>\n");
            }

            var special = new List<string>();
            if (o.NoBitLocker) special.Add("reg add HKLM\\SYSTEM\\CurrentControlSet\\Control\\BitLocker /v PreventDeviceEncryption /t REG_DWORD /d 1 /f");
            if (o.BypassOnlineAccount) special.Add("reg add HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\OOBE /v BypassNRO /t REG_DWORD /d 1 /f");
            if (special.Count > 0)
            {
                x.Append("  <settings pass=\"specialize\">\n");
                x.Append(Comp("Microsoft-Windows-Deployment", arch, RunList("RunSynchronous", "RunSynchronousCommand", special, "")));
                x.Append("  </settings>\n");
            }

            var hasAccount = !string.IsNullOrWhiteSpace(o.LocalAccount);
            if (o.BypassOnlineAccount || o.SkipPrivacy || hasAccount || o.UseCurrentLocale)
            {
                x.Append("  <settings pass=\"oobeSystem\">\n");
                if (o.UseCurrentLocale)
                {
                    var c = CultureInfo.CurrentCulture.Name;
                    var ui = CultureInfo.CurrentUICulture.Name;
                    x.Append(Comp("Microsoft-Windows-International-Core", arch,
                        "      <InputLocale>" + SecurityElement.Escape(c) + "</InputLocale>\n      <SystemLocale>" + SecurityElement.Escape(c) + "</SystemLocale>\n" +
                        "      <UILanguage>" + SecurityElement.Escape(ui) + "</UILanguage>\n      <UserLocale>" + SecurityElement.Escape(c) + "</UserLocale>\n"));
                }
                var shell = new StringBuilder();
                shell.Append("      <OOBE>\n");
                if (o.BypassOnlineAccount || hasAccount) shell.Append("        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>\n");
                if (o.SkipPrivacy) shell.Append("        <ProtectYourPC>3</ProtectYourPC>\n");
                shell.Append("      </OOBE>\n");
                if (hasAccount)
                {
                    if (!IsValidAccountName(o.LocalAccount)) throw new ArgumentException("Nom de compte invalide.");
                    var n = SecurityElement.Escape(o.LocalAccount.Trim());
                    shell.Append("      <UserAccounts>\n        <LocalAccounts>\n          <LocalAccount wcm:action=\"add\">\n" +
                                 "            <Name>" + n + "</Name>\n            <DisplayName>" + n + "</DisplayName>\n            <Group>Administrators</Group>\n" +
                                 "            <Password><Value></Value><PlainText>true</PlainText></Password>\n" +
                                 "          </LocalAccount>\n        </LocalAccounts>\n      </UserAccounts>\n");
                }
                x.Append(Comp("Microsoft-Windows-Shell-Setup", arch, shell.ToString()));
                x.Append("  </settings>\n");
            }
            x.Append("</unattend>\n");
            return x.ToString();
        }

        /// <summary>Architecture de l'image, déduite de son chargeur UEFI.</summary>
        public static string ArchOf(string isoRoot)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(isoRoot, @"efi\boot\bootaa64.efi"))) return "arm64";
            if (System.IO.File.Exists(System.IO.Path.Combine(isoRoot, @"efi\boot\bootx64.efi"))) return "amd64";
            if (System.IO.File.Exists(System.IO.Path.Combine(isoRoot, @"efi\boot\bootia32.efi"))) return "x86";
            return "amd64";
        }
    }
}
