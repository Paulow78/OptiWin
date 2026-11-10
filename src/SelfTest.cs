using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using Microsoft.Win32;

namespace OptiWin
{
    /// <summary>
    /// Tests sans effet sur le système : n'écrit que dans un dossier temporaire et dans la clé
    /// HKCU\Software\OptiWinSelfTest, supprimée à la fin. N'applique aucun réglage réel et
    /// n'écrit sur aucun disque physique.
    /// </summary>
    public static partial class SelfTest
    {
        static int pass, fail;
        static StringBuilder log = new StringBuilder();
        const string SandboxKey = @"Software\OptiWinSelfTest";

        static void Line(string s) { Console.WriteLine(s); log.AppendLine(s); }

        static void T(string name, Action a)
        {
            try { a(); pass++; Line("  ok    " + name); }
            catch (Exception e) { fail++; Line("  ÉCHEC " + name + " : " + e.GetBaseException().Message); }
        }
        static void Is(bool c, string m) { if (!c) throw new Exception(m); }

        /// <param name="online">Lance uniquement le contrôle du catalogue réel de Microsoft (seul test qui accède à Internet).</param>
        public static int Run(string logPath, bool big, bool online = false)
        {
            // Garde-fou : ces tests suppriment leur dossier de données à la fin. Hors bac à sable,
            // ils effaceraient les sauvegardes de l'utilisateur : on refuse donc de démarrer.
            if (!Path.GetFileName(Paths.Data).StartsWith("OptiWin-selftest-", StringComparison.Ordinal))
            {
                Line("REFUS : le dossier de données n'est pas isolé (" + Paths.Data + "). Aucun test lancé.");
                if (logPath != null) File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
                return 3;
            }
            var tmp = Path.Combine(Path.GetTempPath(), "OptiWinTest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            Line("OptiWin — auto-tests (build Windows " + Os.Build + ", admin : " + Os.IsAdmin + ")");
            try
            {
                if (online) { OnlineCatalog(); return Finish(logPath); }
                Catalog();
                RegistryOps();
                Engine();
                EngineProtection();
                FileSafety(tmp);
                Startup();
                SystemTests();
                Usb(tmp);
                Iso(tmp, big);
                Raw(tmp);
                UnattendXml();
                Misc();
                Profiles(tmp);
                MetricsAndDisks();
                DownloadTests(tmp);
                MultibootTests(tmp);
                Rescue(tmp);
            }
            finally
            {
                try { using (var r = Reg.Root(RegistryHive.CurrentUser)) r.DeleteSubKeyTree(SandboxKey, false); } catch { }
                try { Directory.Delete(tmp, true); }
                catch (Exception e) { fail++; Line("  ÉCHEC dossier temporaire non supprimé : " + e.Message); }
                try { Directory.Delete(Paths.Data, true); } catch { }
            }
            return Finish(logPath);
        }

        static int Finish(string logPath)
        {
            Line("");
            Line(pass + " réussis, " + fail + " en échec");
            if (logPath != null) File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
            return fail == 0 ? 0 : 1;
        }

        static void Catalog()
        {
            Line("Catalogue");
            T("identifiants uniques", () => { var d = TweakCatalog.All.GroupBy(x => x.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList(); Is(d.Count == 0, string.Join(",", d)); });
            T("catégories connues, textes présents", () =>
            {
                foreach (var t in TweakCatalog.All)
                {
                    Is(TweakCatalog.Categories.Contains(t.Cat), t.Id + " : catégorie");
                    Is(!string.IsNullOrWhiteSpace(t.Title) && !string.IsNullOrWhiteSpace(t.Desc), t.Id + " : texte");
                    Is(t.Ops.Count > 0, t.Id + " : aucune opération");
                }
            });
            T("lecture de l'état de chaque réglage", () =>
            {
                var sup = 0;
                foreach (var t in TweakCatalog.All) if (t.IsSupported) { t.RefreshState(); sup++; }
                Line("        " + sup + " réglages applicables sur " + TweakCatalog.All.Count + " au catalogue");
            });
            T("aucun réglage avancé dans un profil", () =>
            {
                var bad = TweakCatalog.All.Where(t => t.Risk == Risk.Advanced && t.Profiles != Prof.None).ToList();
                Is(bad.Count == 0, string.Join(",", bad.Select(b => b.Id)));
            });
            T("chaque profil propose des réglages", () =>
            {
                foreach (Prof p in new[] { Prof.OldPc, Prof.Fresh, Prof.Gaming, Prof.Laptop, Prof.Privacy })
                {
                    var n = TweakCatalog.ForProfile(p).Count();
                    Line("        " + SysInfo.ProfileName(p) + " : " + n);
                    Is(n >= 3, p + " trop court");
                }
            });
            T("aucun réglage ne touche à Defender ni à Windows Update", () =>
            {
                var bad = TweakCatalog.All.Where(t => t.Details.IndexOf("Windows Defender", StringComparison.OrdinalIgnoreCase) >= 0
                    || t.Details.Contains("wuauserv") || t.Details.Contains("WinDefend") || t.Details.Contains("FeatureSettingsOverride")).ToList();
                Is(bad.Count == 0, string.Join(",", bad.Select(b => b.Id)));
            });
        }

        static RegOp Op(string key, string name, RegistryValueKind k, object v, object def)
        {
            return new RegOp(RegistryHive.CurrentUser, SandboxKey + key, name, k, v, def);
        }

        static void RegistryOps()
        {
            Line("Registre (bac à sable HKCU)");
            T("valeur absente : créée puis retirée, clés créées supprimées", () =>
            {
                var op = Op(@"\A\B", "V", RegistryValueKind.DWord, 1, null);
                var d = new Dictionary<string, string>();
                op.Capture(d); op.Apply();
                Is(op.IsApplied(), "non appliqué");
                op.Restore(d);
                Is(Reg.Get(RegistryHive.CurrentUser, SandboxKey + @"\A\B", "V") == null, "valeur restée");
                Is(!Reg.KeyExists(RegistryHive.CurrentUser, SandboxKey + @"\A\B"), "clé restée");
            });
            T("valeur existante : retrouve exactement son contenu et son type", () =>
            {
                using (var r = Reg.Root(RegistryHive.CurrentUser))
                using (var k = r.CreateSubKey(SandboxKey + @"\C"))
                {
                    k.SetValue("D", 7, RegistryValueKind.DWord);
                    k.SetValue("S", "%TEMP%\\x", RegistryValueKind.ExpandString);
                    k.SetValue("B", new byte[] { 1, 2, 255 }, RegistryValueKind.Binary);
                    k.SetValue("M", new[] { "a", "b" }, RegistryValueKind.MultiString);
                    k.SetValue("Q", 5000000000L, RegistryValueKind.QWord);
                    k.SetValue("N", unchecked((int)0x80000002), RegistryValueKind.DWord);
                }
                foreach (var name in new[] { "D", "S", "B", "M", "Q", "N" })
                {
                    var op = Op(@"\C", name, RegistryValueKind.DWord, 99, 0);
                    var d = new Dictionary<string, string>();
                    op.Capture(d); op.Apply();
                    Is(op.IsApplied(), name + " non appliqué");
                    op.Restore(d);
                }
                using (var r = Reg.Root(RegistryHive.CurrentUser))
                using (var k = r.OpenSubKey(SandboxKey + @"\C"))
                {
                    Is((int)k.GetValue("D") == 7, "D");
                    Is(k.GetValueKind("S") == RegistryValueKind.ExpandString && (string)k.GetValue("S", null, RegistryValueOptions.DoNotExpandEnvironmentNames) == "%TEMP%\\x", "S");
                    Is(((byte[])k.GetValue("B")).SequenceEqual(new byte[] { 1, 2, 255 }), "B");
                    Is(((string[])k.GetValue("M")).SequenceEqual(new[] { "a", "b" }), "M");
                    Is((long)k.GetValue("Q") == 5000000000L, "Q");
                    Is((int)k.GetValue("N") == unchecked((int)0x80000002), "N");
                }
            });
            T("sans sauvegarde : retour à la valeur par défaut de Windows", () =>
            {
                var op = Op(@"\E", "V", RegistryValueKind.DWord, 0, 1);
                op.Apply();
                op.Restore(null);
                Is((int)Reg.Get(RegistryHive.CurrentUser, SandboxKey + @"\E", "V") == 1, "défaut");
            });
            T("valeur par défaut d'une clé (menu contextuel Windows 11)", () =>
            {
                var op = Op(@"\F\G", "", RegistryValueKind.String, "", null).PruneUpTo(SandboxKey + @"\F");
                op.Apply();
                Is(op.IsApplied(), "non appliqué");
                op.Restore(null);
                Is(!Reg.KeyExists(RegistryHive.CurrentUser, SandboxKey + @"\F\G"), "clé restée");
            });
            T("ne supprime pas une clé qui contient autre chose", () =>
            {
                using (var r = Reg.Root(RegistryHive.CurrentUser))
                using (var k = r.CreateSubKey(SandboxKey + @"\H")) k.SetValue("Garde", 1, RegistryValueKind.DWord);
                var op = Op(@"\H\I", "V", RegistryValueKind.DWord, 1, null);
                var d = new Dictionary<string, string>();
                op.Capture(d); op.Apply(); op.Restore(d);
                Is(!Reg.KeyExists(RegistryHive.CurrentUser, SandboxKey + @"\H\I"), "sous-clé restée");
                Is(Reg.Get(RegistryHive.CurrentUser, SandboxKey + @"\H", "Garde") != null, "valeur voisine perdue");
            });
        }

        sealed class FailingOp : TweakOp
        {
            public override bool NeedsAdmin { get { return false; } }
            public override bool IsApplied() { return false; }
            public override void Capture(Dictionary<string, string> d) { }
            public override void Apply() { throw new InvalidOperationException("échec simulé"); }
            public override void Restore(Dictionary<string, string> d) { }
            public override string Describe() { return "échec"; }
        }

        static void Engine()
        {
            Line("Moteur de réglages");
            T("appliquer, réappliquer, annuler : l'origine n'est jamais écrasée", () =>
            {
                using (var r = Reg.Root(RegistryHive.CurrentUser))
                using (var k = r.CreateSubKey(SandboxKey + @"\E2")) k.SetValue("V", 42, RegistryValueKind.DWord);
                var t = new Tweak { Id = "test.a", Title = "Test A", Desc = "d", Cat = TweakCatalog.CatPerf, Ops = new List<TweakOp> { Op(@"\E2", "V", RegistryValueKind.DWord, 1, 0) } };
                TweakEngine.Apply(t);
                Is(t.IsApplied, "non appliqué");
                TweakEngine.Apply(t);
                Is(Store.HasBackup("test.a"), "pas de sauvegarde");
                TweakEngine.Revert(t);
                Is((int)Reg.Get(RegistryHive.CurrentUser, SandboxKey + @"\E2", "V") == 42, "origine perdue");
                Is(!Store.HasBackup("test.a"), "sauvegarde restée");
            });
            T("échec en cours de route : retour arrière complet", () =>
            {
                var t = new Tweak { Id = "test.b", Title = "Test B", Desc = "d", Cat = TweakCatalog.CatPerf, Ops = new List<TweakOp> { Op(@"\E3", "V", RegistryValueKind.DWord, 1, null), new FailingOp() } };
                var threw = false;
                try { TweakEngine.Apply(t); } catch (InvalidOperationException) { threw = true; }
                Is(threw, "aucune erreur remontée");
                Is(Reg.Get(RegistryHive.CurrentUser, SandboxKey + @"\E3", "V") == null, "pas de retour arrière");
                Is(!Store.HasBackup("test.b"), "sauvegarde orpheline");
            });
            T("état persistant relu après écriture", () =>
            {
                Store.SetSetting("x", "é✓");
                Is(Store.Setting("x") == "é✓", "réglage");
                Is(File.Exists(Path.Combine(Paths.Data, "state.json")), "state.json absent");
                Is(Paths.Data.Contains("selftest"), "les tests écrivent hors du bac à sable : " + Paths.Data);
            });
        }

        static void FileSafety(string tmp)
        {
            Line("Suppression de fichiers");
            var ct = CancellationToken.None;
            T("ne traverse pas une jonction", () =>
            {
                var outside = Path.Combine(tmp, "dehors"); var root = Path.Combine(tmp, "racine");
                Directory.CreateDirectory(outside); Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(outside, "precieux.txt"), "ne pas toucher");
                File.WriteAllText(Path.Combine(root, "cache.tmp"), "à supprimer");
                var link = Path.Combine(root, "lien");
                Proc.RunChecked("cmd.exe", "/c mklink /J \"" + link + "\" \"" + outside + "\"", null);
                Is(Directory.Exists(link), "jonction non créée");
                Fs.Delete(new[] { new FsSpec(root) }, ct);
                Is(File.Exists(Path.Combine(outside, "precieux.txt")), "fichier extérieur supprimé !");
                Is(!File.Exists(Path.Combine(root, "cache.tmp")), "fichier visé non supprimé");
                Proc.Run("cmd.exe", "/c rmdir \"" + link + "\"", null);
            });
            T("refuse les racines et dossiers sensibles", () =>
            {
                var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var prof = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (var p in new[] { win, prof, win + "\\", "C:\\", "", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.Desktop) })
                    Is(Fs.Measure(new[] { new FsSpec(p) }, ct) == 0, "accepté : " + p);
            });
            T("respecte l'âge minimal et le motif", () =>
            {
                var d = Path.Combine(tmp, "age"); Directory.CreateDirectory(d);
                var recent = Path.Combine(d, "recent.tmp"); var old = Path.Combine(d, "vieux.tmp"); var other = Path.Combine(d, "autre.dat");
                File.WriteAllText(recent, "1"); File.WriteAllText(old, "22"); File.WriteAllText(other, "333");
                File.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-3)); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
                var freed = Fs.Delete(new[] { new FsSpec(d, 24, "*.tmp", false) }, ct);
                Is(freed == 2, "octets : " + freed);
                Is(File.Exists(recent) && !File.Exists(old) && File.Exists(other), "mauvais fichiers");
            });
            T("mesure sans rien supprimer", () =>
            {
                var d = Path.Combine(tmp, "mes"); Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "a"), "12345");
                Is(Fs.Measure(new[] { new FsSpec(d) }, ct) == 5, "mesure");
                Is(File.Exists(Path.Combine(d, "a")), "fichier supprimé");
            });
            T("catalogue de nettoyage : analyse de tout, sans erreur", () =>
            {
                foreach (var c in Cleaner.BuildCatalog()) c.Scanner(ct);
            });
            T("la corbeille et Windows.old ne sont jamais présélectionnés", () =>
            {
                var cats = Cleaner.BuildCatalog();
                foreach (var id in new[] { "recycle", "winold", "setup" }) Is(!cats.First(c => c.Id == id).Selected, id);
            });
            T("analyseur d'espace", () =>
            {
                var d = Path.Combine(tmp, "ana"); Directory.CreateDirectory(Path.Combine(d, "sous"));
                File.WriteAllBytes(Path.Combine(d, "sous", "f.bin"), new byte[1000]);
                var n = DiskAnalyzer.Scan(d, ct, null);
                Is(n.Size == 1000 && n.Kids.Count == 1 && n.Kids[0].Share == 100, "tailles");
            });
        }

        static void Startup()
        {
            Line("Démarrage");
            T("inventaire", () => Line("        " + StartupManager.List().Count + " programmes"));
            T("extraction du chemin de la commande", () =>
            {
                Is(StartupManager.ExeFrom("\"C:\\Program Files\\A\\a.exe\" -x") == "C:\\Program Files\\A\\a.exe", "guillemets");
                Is(StartupManager.ExeFrom("C:\\Program Files\\A\\a.exe -x") == "C:\\Program Files\\A\\a.exe", "espaces");
                Is(StartupManager.ExeFrom("") == null, "vide");
            });
        }

        static void SystemTests()
        {
            Line("Système");
            T("relevé matériel", () =>
            {
                var s = SysInfo.Current;
                Is(s.Ram > 0, "RAM");
                Is(s.Build > 0, "build");
                Line("        " + s.OsName + " " + s.OsVersion + " | " + s.Cpu + " | " + Fmt.Size(s.Ram) + " | " + s.DiskType + " | portable : " + s.IsLaptop + " | UEFI : " + s.IsUefi + " | SecureBoot : " + s.SecureBoot);
                Line("        profil conseillé : " + SysInfo.ProfileName(SysInfo.RecommendedProfile(s)));
            });
            T("mesures en direct", () => { SysInfo.CpuLoad(); Thread.Sleep(120); var c = SysInfo.CpuLoad(); Is(c >= 0 && c <= 100, "cpu " + c); Is(SysInfo.RamLoad() > 0, "ram"); });
            T("score de santé borné, recommandations produites", () =>
            {
                var low = new SysSnapshot { DiskUnhealthy = true, DiskHealth = "Défaillant" };
                var h = Health.Compute(low, 50L << 30, 30, 40, null);
                Is(h.Score >= 0 && h.Score <= 100, "borne");
                Is(h.Insights.First().Level == "bad", "priorité");
                Is(Health.Compute(new SysSnapshot(), 0, 0, 0, null).Score > h.Score, "ordre");
            });
            T("le profil suit le matériel", () =>
            {
                Is(SysInfo.RecommendedProfile(new SysSnapshot { Ram = 4L << 30, Threads = 8 }) == Prof.OldPc, "peu de RAM");
                Is(SysInfo.RecommendedProfile(new SysSnapshot { Ram = 32L << 30, Threads = 8, SystemDiskIsHdd = true }) == Prof.OldPc, "HDD");
                Is(SysInfo.RecommendedProfile(new SysSnapshot { Ram = 32L << 30, Threads = 16, InstalledOn = DateTime.Now.AddDays(-3) }) == Prof.Fresh, "neuf");
                Is(SysInfo.RecommendedProfile(new SysSnapshot { Ram = 16L << 30, Threads = 12, IsLaptop = true, InstalledOn = DateTime.Now.AddYears(-1) }) == Prof.Laptop, "portable");
            });
            T("compatibilité Windows 11", () => Is(Win11Check.Run(SysInfo.Current).Count >= 5, "liste"));
            T("exécution de programme, code de retour, sortie", () =>
            {
                var lines = new List<string>();
                var c = Proc.Run("cmd.exe", "/c echo bonjour& exit 3", l => lines.Add(l));
                Is(c == 3, "code " + c);
                Is(lines.Contains("bonjour"), "sortie");
            });
            T("annulation d'un programme en cours", () =>
            {
                var cts = new CancellationTokenSource(400);
                var sw = Stopwatch.StartNew();
                var cancelled = false;
                try { Proc.Run("ping.exe", "-n 30 127.0.0.1", null, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
                Is(cancelled && sw.Elapsed.TotalSeconds < 10, "non annulé");
            });
            T("PowerShell : caractères accentués et apostrophes", () =>
            {
                Is(PS.Run("Write-Output " + PS.Q("l'été ✓")) == "l'été ✓", "retour");
            });
        }

        static void Usb(string tmp)
        {
            Line("Périphériques USB (lecture seule)");
            T("le disque système n'est jamais proposé", () =>
            {
                var sysNum = Disks.DiskNumberOf((Environment.GetEnvironmentVariable("SystemDrive") ?? "C:")[0]);
                Is(sysNum >= 0, "disque système non identifié");
                var l = Disks.List(true);
                Line("        disque système : " + sysNum + " ; proposés : " + (l.Count == 0 ? "aucun" : string.Join(" | ", l.Select(x => x.ToString()))));
                Is(l.All(d => d.Index != sysNum), "disque système dans la liste !");
                Is(l.All(d => !d.Letters.Contains(Environment.GetEnvironmentVariable("SystemDrive"))), "lecteur système dans la liste !");
            });
            T("un périphérique changé est refusé avant d'effacer", () =>
            {
                var threw = false;
                try { Disks.Revalidate(new UsbDisk { Index = 99, Size = 123, Pnp = "X" }); } catch (InvalidOperationException) { threw = true; }
                Is(threw, "accepté");
            });
            T("script de partitionnement", () =>
            {
                var a = Disks.DiskPartScript(3, false, 16L << 30, "Z", "OPTIWINUSB");
                Is(a.Contains("select disk 3") && a.Contains("convert mbr") && a.Contains("active") && a.Contains("format fs=fat32 quick") && a.Contains("assign letter=Z") && !a.Contains("size="), "MBR 16 Go");
                var b = Disks.DiskPartScript(4, true, 128L * 1000 * 1000 * 1000, "Y", "X");
                Is(b.Contains("convert gpt") && b.Contains("size=32000") && !b.Contains("active"), "GPT 128 Go");
                var order = a.IndexOf("select disk") < a.IndexOf("clean");
                Is(order, "ordre");
            });
            T("lettre de lecteur libre", () => { var l = Disks.FreeLetter(); Is(!Directory.Exists(l + ":\\"), "occupée"); });
            T("lettres de lecteur d'un disque physique", () =>
            {
                var sys = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
                var letters = Disks.LettersOf(@"\\.\PHYSICALDRIVE" + Disks.DiskNumberOf(sys[0]));
                Is(letters.Contains(sys), "lecteur système absent : " + string.Join(",", letters));
                foreach (var d in Disks.List(true))
                    Line("        clé proposée : " + d);
            });
        }

        static void Iso(string tmp, bool big)
        {
            Line("Création d'ISO (IMAPI2)");
            var job = new Job();
            T("ISO de données : identifiée et contenu présent", () =>
            {
                var src = Path.Combine(tmp, "data"); Directory.CreateDirectory(Path.Combine(src, "sous"));
                File.WriteAllText(Path.Combine(src, "lisezmoi.txt"), "MARQUEUR-OPTIWIN-RACINE");
                File.WriteAllText(Path.Combine(src, "sous", "fichier é.txt"), "MARQUEUR-OPTIWIN-SOUS");
                var iso = Path.Combine(tmp, "data.iso");
                IsoBuilder.Create(src, iso, "TEST", false, job);
                var i = IsoProbe.Inspect(iso);
                Is(i.HasUdf, "UDF absent");
                var bytes = Encoding.ASCII.GetString(File.ReadAllBytes(iso));
                Is(bytes.Contains("MARQUEUR-OPTIWIN-RACINE") && bytes.Contains("MARQUEUR-OPTIWIN-SOUS"), "contenu absent");
                Line("        " + i.Describe());
            });
            T("ISO amorçable : catalogue El Torito BIOS + UEFI", () =>
            {
                var src = Path.Combine(tmp, "boot");
                Directory.CreateDirectory(Path.Combine(src, "boot")); Directory.CreateDirectory(Path.Combine(src, @"efi\microsoft\boot")); Directory.CreateDirectory(Path.Combine(src, "sources"));
                var bios = new byte[4096]; bios[0] = 0xEB; bios[510] = 0x55; bios[511] = 0xAA;
                var uefi = new byte[1474560]; uefi[0] = 0xEB;
                File.WriteAllBytes(Path.Combine(src, @"boot\etfsboot.com"), bios);
                File.WriteAllBytes(Path.Combine(src, @"efi\microsoft\boot\efisys.bin"), uefi);
                File.WriteAllText(Path.Combine(src, @"sources\x.txt"), "x");
                var iso = Path.Combine(tmp, "boot.iso");
                IsoBuilder.Create(src, iso, "WINTEST", false, job);
                var i = IsoProbe.Inspect(iso);
                Is(i.HasElTorito, "pas d'enregistrement El Torito");
                var plats = BootPlatforms(iso);
                Line("        plateformes de démarrage : " + string.Join(", ", plats.Select(p => "0x" + p.ToString("X2"))));
                Is(plats.Contains(0x00) && plats.Contains(0xEF), "BIOS et UEFI requis");
            });
            T("les fichiers source sont libérés après la création", () =>
            {
                var src = Path.Combine(tmp, "rel"); Directory.CreateDirectory(Path.Combine(src, "sous"));
                var a = Path.Combine(src, "racine.txt"); var b = Path.Combine(src, "sous", "b.txt");
                File.WriteAllText(a, "a"); File.WriteAllText(b, "b");
                IsoBuilder.Create(src, Path.Combine(tmp, "rel.iso"), "REL", false, job);
                File.Delete(a); File.Delete(b);
                Directory.Delete(src, true);
            });
            T("nom de volume invalide assaini", () =>
            {
                var src = Path.Combine(tmp, "lab"); Directory.CreateDirectory(src);
                File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                var iso = Path.Combine(tmp, "lab.iso");
                IsoBuilder.Create(src, iso, "mon volume/é?*", false, job);
                Is(File.Exists(iso), "ISO absente");
            });
            T("annulation : aucun fichier partiel", () =>
            {
                var src = Path.Combine(tmp, "can"); Directory.CreateDirectory(src);
                using (var f = File.Create(Path.Combine(src, "gros.bin"))) f.SetLength(200L << 20);
                var iso = Path.Combine(tmp, "can.iso");
                var j = new Job(); j.Cancel();
                var cancelled = false;
                try { IsoBuilder.Create(src, iso, "X", false, j); } catch (OperationCanceledException) { cancelled = true; }
                Is(cancelled, "non annulé");
                Is(!File.Exists(iso), "fichier partiel resté");
            });
            if (big)
            {
                T("fichier de plus de 4 Go dans une ISO", () =>
                {
                    var src = Path.Combine(tmp, "big"); Directory.CreateDirectory(src);
                    using (var f = File.Create(Path.Combine(src, "install.wim"))) f.SetLength((long)(4.2 * (1L << 30)));
                    var iso = Path.Combine(tmp, "big.iso");
                    IsoBuilder.Create(src, iso, "BIG", false, job);
                    var len = new FileInfo(iso).Length;
                    Line("        ISO : " + Fmt.Size(len));
                    Is(len > 4L << 30, "trop petite");
                    Is(IsoProbe.Inspect(iso).HasUdf, "UDF");
                    File.Delete(iso);
                });
            }
        }

        /// <summary>Relit le catalogue de démarrage El Torito pour lister les plateformes déclarées.</summary>
        static List<int> BootPlatforms(string iso)
        {
            var res = new List<int>();
            using (var f = new FileStream(iso, FileMode.Open, FileAccess.Read))
            {
                var s = new byte[2048];
                f.Position = 17 * 2048L;
                f.Read(s, 0, 2048);
                var cat = BitConverter.ToInt32(s, 0x47);
                f.Position = cat * 2048L;
                f.Read(s, 0, 2048);
                Is(s[0] == 1, "entrée de validation absente");
                res.Add(s[1]); // plateforme de l'entrée par défaut
                for (int off = 64; off + 32 <= 2048; off += 32)
                {
                    if (s[off] == 0x90 || s[off] == 0x91) res.Add(s[off + 1]);
                    else if (s[off] == 0) break;
                }
            }
            return res;
        }

        static void Raw(string tmp)
        {
            Line("Écriture brute (sur un fichier temporaire, jamais sur un disque)");
            foreach (var size in new[] { 512, 4096, (1 << 20) + 123, (3 << 20) })
            {
                var sz = size;
                T("image de " + sz + " octets : écriture et relecture", () =>
                {
                    var src = Path.Combine(tmp, "raw-" + sz + ".img"); var dev = Path.Combine(tmp, "raw-" + sz + ".dev");
                    var data = new byte[sz];
                    new Random(sz).NextBytes(data);
                    File.WriteAllBytes(src, data);
                    File.WriteAllBytes(dev, new byte[0]);
                    RawImage.Write(src, dev, new Job(), true);
                    var back = File.ReadAllBytes(dev);
                    Is(back.Length >= sz && back.Length % 4096 == 0, "taille " + back.Length);
                    Is(back.Take(sz).SequenceEqual(data), "contenu différent");
                    Is(back.Skip(sz).All(b => b == 0), "remplissage non nul");
                });
            }
            T("annulation pendant l'écriture", () =>
            {
                var src = Path.Combine(tmp, "rawc.img"); var dev = Path.Combine(tmp, "rawc.dev");
                File.WriteAllBytes(src, new byte[1 << 20]); File.WriteAllBytes(dev, new byte[0]);
                var j = new Job(); j.Cancel();
                var cancelled = false;
                try { RawImage.Write(src, dev, j, false); } catch (OperationCanceledException) { cancelled = true; }
                Is(cancelled, "non annulé");
            });
            T("empreinte SHA-256 connue", () =>
            {
                var p = Path.Combine(tmp, "h.txt");
                File.WriteAllText(p, "abc", new UTF8Encoding(false));
                Is(Hashing.Sha256(p, null) == "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", "abc");
            });
        }

        static void UnattendXml()
        {
            Line("Réponses automatiques (autounattend.xml)");
            T("toutes les options : XML valide et attendu", () =>
            {
                var x = Unattend.Build(new UnattendOptions { BypassHardware = true, BypassOnlineAccount = true, LocalAccount = "Paul & Cie", NoBitLocker = true, SkipPrivacy = true }, "amd64");
                var d = new XmlDocument(); d.LoadXml(x);
                var ns = new XmlNamespaceManager(d.NameTable); ns.AddNamespace("u", "urn:schemas-microsoft-com:unattend");
                Is(d.SelectNodes("//u:settings[@pass='windowsPE']//u:RunSynchronousCommand", ns).Count == 5, "contournements");
                Is(d.SelectSingleNode("//u:settings[@pass='specialize']", ns) != null, "specialize");
                Is(d.SelectSingleNode("//u:LocalAccount/u:Name", ns).InnerText == "Paul & Cie", "échappement");
                Is(d.SelectSingleNode("//u:HideOnlineAccountScreens", ns).InnerText == "true", "compte en ligne");
                Is(x.Contains("processorArchitecture=\"amd64\""), "architecture");
            });
            T("jamais de section qui efface un disque", () =>
            {
                var x = Unattend.Build(new UnattendOptions { BypassHardware = true, BypassOnlineAccount = true, LocalAccount = "A", NoBitLocker = true, SkipPrivacy = true }, "arm64");
                foreach (var w in new[] { "DiskConfiguration", "InstallTo", "WillWipeDisk", "ProductKey" }) Is(!x.Contains(w), w);
            });
            T("aucune option cochée : rien à écrire", () => Is(new UnattendOptions().IsEmpty, "IsEmpty"));
            T("noms de compte invalides refusés", () =>
            {
                foreach (var n in new[] { "", " ", "a/b", "a\\b", "a*", "abcdefghijklmnopqrstuvwxyz", "fin." }) Is(!Unattend.IsValidAccountName(n), n);
                Is(Unattend.IsValidAccountName("Paul"), "Paul");
            });
        }

        static void Misc()
        {
            Line("Divers");
            T("tâche planifiée d'entretien : définition acceptée par Windows", () =>
            {
                dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                svc.Connect();
                dynamic def = svc.NewTask(0);
                def.XmlText = Tools.WeeklyXml(@"C:\Program Files\OptiWin & co\OptiWin.exe");
                Is(def.Settings.StartWhenAvailable == true, "StartWhenAvailable");
            });
            T("requête DNS bien formée", () =>
            {
                var q = Dns.BuildQuery("www.a.fr");
                Is(q[2] == 1 && q[5] == 1, "en-tête");
                Is(q[12] == 3 && q[16] == 1 && q[18] == 2 && q[21] == 0, "nom");
                Is(q.Length == 12 + 10 + 4, "taille " + q.Length);
            });
            T("mesure DNS réelle", () =>
            {
                var ms = Dns.Measure("1.1.1.1", CancellationToken.None);
                Line("        1.1.1.1 : " + (double.IsInfinity(ms) ? "sans réponse" : Fmt.Num(ms, "0") + " ms"));
            });
            T("rapport système", () => Is(Tools.BuildReport().Contains("Processeur"), "contenu"));
            T("liste des outils cohérente", () =>
            {
                var l = Tools.Catalog();
                Is(l.Select(x => x.Id).Distinct().Count() == l.Count, "doublons");
                Is(l.All(x => x.Run != null), "action");
            });
            T("applications : le Store et winget ne sont jamais retirables", () =>
            {
                var pats = typeof(AppsCatalog).GetMethod("Templates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, null) as List<BloatApp>;
                foreach (var keep in new[] { "Microsoft.WindowsStore", "Microsoft.DesktopAppInstaller", "Microsoft.SecHealthUI", "Microsoft.VCLibs", "Microsoft.UI.Xaml", "Microsoft.NET.Native", "Microsoft.WindowsTerminal", "Microsoft.WindowsCalculator", "Microsoft.Windows.Photos", "windows.immersivecontrolpanel" })
                    Is(!pats.Any(p => keep.StartsWith(p.Pattern, StringComparison.OrdinalIgnoreCase)), keep);
                Is(pats.Select(p => p.Pattern).Distinct().Count() == pats.Count, "doublons");
            });
            T("logiciels : identifiants uniques", () =>
            {
                var s = AppsCatalog.Software();
                Is(s.Select(x => x.Id).Distinct().Count() == s.Count, "doublons");
            });
            T("lecture des paquets installés", () =>
            {
                string err;
                var l = AppsCatalog.ListInstalled(out err);
                Line("        " + l.Count + " applications superflues reconnues, " + l.Count(a => a.Recommended) + " conseillées au retrait");
            });
        }
    }
}
