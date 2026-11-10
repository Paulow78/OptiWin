using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace OptiWin
{
    public static partial class SelfTest
    {
        /// <summary>Lit le vrai catalogue de Windows 11 avec le code de l'application. L'image elle-même n'est pas téléchargée.</summary>
        static void OnlineCatalog()
        {
            Line("Catalogue réel de Microsoft (Windows 11)");
            List<EsdFile> files = null;
            T("téléchargement en HTTPS, extraction et lecture", () =>
            {
                files = WinDownload.FetchCatalog(WinDownload.Products[0], new Job());
                Is(files.Count > 0, "catalogue vide");
                Line("        " + files.Count + " images, builds : " + string.Join(", ", files.Select(f => f.Build).Distinct()));
                Line("        éditions : " + string.Join(", ", files.Select(f => f.Edition).Distinct()));
                Line("        architectures : " + string.Join(", ", files.Select(f => f.Arch).Distinct()));
                Line("        langues : " + files.Select(f => f.LanguageCode).Distinct().Count());
            });
            if (files == null) return;
            T("chaque entrée est complète et pointe vers Microsoft", () =>
            {
                foreach (var f in files)
                {
                    Is(f.FileName.EndsWith(".esd", StringComparison.OrdinalIgnoreCase), "nom : " + f.FileName);
                    Is(f.Size > 1L << 30, "taille de " + f.FileName + " : " + f.Size);
                    Is(Regex.IsMatch(f.Sha1, "^[0-9a-f]{40}$"), "empreinte de " + f.FileName + " : " + f.Sha1);
                    Is(WinDownload.IsTrustedHost(new Uri(f.Url)), "adresse : " + f.Url);
                    Is(f.LanguageCode.Length > 0 && f.Arch.Length > 0, "langue ou architecture de " + f.FileName);
                }
            });
            EsdFile fr = null;
            T("image française x64 trouvée", () =>
            {
                Is(WinDownload.Languages(files).Any(l => l.Code == "fr-fr"), "français absent");
                Is(WinDownload.Architectures(files, "fr-fr").FirstOrDefault() == "x64", "x64 pas en premier : " + string.Join(",", WinDownload.Architectures(files, "fr-fr")));
                fr = WinDownload.Choose(files, "fr-fr", "x64");
                Is(fr != null, "aucune image");
                Line("        " + fr.FileName + " | " + fr.Edition + " | " + Fmt.Size(fr.Size) + " | " + new Uri(fr.Url).Host);
            });
            if (fr == null) return;
            Action<string> head = url =>
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "HEAD";
                req.Timeout = 30000;
                req.UserAgent = "OptiWin";
                using (var r = (HttpWebResponse)req.GetResponse())
                {
                    Line("        " + new Uri(url).Scheme + " : statut " + (int)r.StatusCode + ", " + r.ContentLength + " octets, hôte final " + r.ResponseUri.Host + ", reprise : " + r.Headers["Accept-Ranges"]);
                    Is(r.StatusCode == HttpStatusCode.OK, "statut " + r.StatusCode);
                    Is(r.ContentLength == fr.Size, "taille annoncée " + r.ContentLength + " au lieu de " + fr.Size);
                }
            };
            T("le lien de l'image répond, et sa taille correspond (sans la télécharger)", () => head(fr.Url));
        }

        static void EngineProtection()
        {
            Line("Réglages protégés par Windows");
            T("échec à la première écriture : la clé créée est retirée", () =>
            {
                var t = new Tweak { Id = "test.c", Title = "Test C", Desc = "d", Cat = TweakCatalog.CatPerf,
                    Ops = new List<TweakOp> { new RegOp(RegistryHive.CurrentUser, SandboxKey + @"\Z1\Z2", "V", RegistryValueKind.DWord, "pas un nombre", null) } };
                Throws<ArgumentException>(() => TweakEngine.Apply(t), "écriture invalide");
                Is(!Reg.KeyExists(RegistryHive.CurrentUser, SandboxKey + @"\Z1"), "clé vide restée");
                Is(!Store.HasBackup("test.c"), "sauvegarde orpheline");
            });
            T("écriture refusée : reconnue comme protection de Windows, mémorisée", () =>
            {
                // Reproduit un refus d'écriture malgré les droits, comme celui du pilote UCPD.
                var path = SandboxKey + @"\Verrou";
                var me = WindowsIdentity.GetCurrent().User;
                var rule = new RegistryAccessRule(me, RegistryRights.SetValue, AccessControlType.Deny);
                using (var r = Reg.Root(RegistryHive.CurrentUser))
                using (var k = r.CreateSubKey(path))
                {
                    var sec = k.GetAccessControl();
                    sec.AddAccessRule(rule);
                    k.SetAccessControl(sec);
                }
                try
                {
                    var t = new Tweak { Id = "test.lock", Title = "Test verrou", Desc = "d", Cat = TweakCatalog.CatPerf, SettingsUri = "ms-settings:taskbar",
                        Ops = new List<TweakOp> { new RegOp(RegistryHive.CurrentUser, path, "V", RegistryValueKind.DWord, 1, null) } };
                    SettingProtectedException ex = null;
                    try { TweakEngine.Apply(t); } catch (SettingProtectedException e) { ex = e; }
                    Is(ex != null, "refus non reconnu");
                    Is(ex.Message.Contains("Paramètres"), ex.Message);
                    Is(t.Blocked, "refus non mémorisé");
                    Is(!Store.HasBackup("test.lock"), "sauvegarde orpheline");
                }
                finally
                {
                    // Le verrou est retiré, sinon la clé de test ne pourrait plus être supprimée.
                    using (var r = Reg.Root(RegistryHive.CurrentUser))
                    using (var k = r.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadKey))
                    {
                        var sec = k.GetAccessControl();
                        sec.RemoveAccessRuleSpecific(rule);
                        k.SetAccessControl(sec);
                    }
                    using (var r = Reg.Root(RegistryHive.CurrentUser)) r.DeleteSubKeyTree(path, false);
                }
                Is(!Reg.KeyExists(RegistryHive.CurrentUser, path), "clé de test restée");
            });
            T("un réglage protégé n'est plus proposé par les profils", () =>
            {
                var w = TweakCatalog.ById("perf.widgets");
                Is(w.SettingsUri == "ms-settings:taskbar", "page des Paramètres absente");
                if (!w.IsSupported || !w.FitsThisPc) return;
                Store.SetSetting("blocked.perf.widgets", "test");
                try { Is(!TweakCatalog.ForProfile(Prof.OldPc).Contains(w), "encore proposé"); }
                finally { Store.SetSetting("blocked.perf.widgets", null); }
                Is(TweakCatalog.ForProfile(Prof.OldPc).Contains(w), "plus jamais proposé");
            });
        }

        static void Throws<TEx>(Action a, string what) where TEx : Exception
        {
            try { a(); }
            catch (TEx) { return; }
            throw new Exception(what + " : aucune erreur levée");
        }

        static void Profiles(string tmp)
        {
            Line("Profil de réinstallation");
            T("export winget lu correctement", () =>
            {
                var l = PcProfiles.ParseWingetExport("{\"Sources\":[{\"Packages\":[{\"PackageIdentifier\":\"VideoLAN.VLC\"},{\"PackageIdentifier\":\"Git.Git\"}],\"SourceDetails\":{\"Name\":\"winget\"}}]}");
                Is(l.Count == 2 && l[0] == "VideoLAN.VLC", "paquets");
                Is(PcProfiles.ParseWingetExport("{}").Count == 0, "vide");
            });
            T("composants système écartés, identifiants dangereux refusés", () =>
            {
                var f = PcProfiles.FilterSoftware(new[] { "VideoLAN.VLC", "Microsoft.VCRedist.2015+.x64", "x\" --force --override \"calc", "a b", "--source", "videolan.vlc" });
                Is(f.Count == 1 && f[0] == "VideoLAN.VLC", string.Join("|", f));
            });
            T("un profil relu ne garde que ce qui est reconnu", () =>
            {
                var path = Path.Combine(tmp, "profil.json");
                var evil = new PcProfile { Machine = "A" };
                evil.Tweaks.AddRange(new[] { "ui.ext", "n.existe.pas" });
                evil.RemoveApps.AddRange(new[] { "Microsoft.BingNews", "Microsoft.WindowsStore", "Microsoft.DesktopAppInstaller" });
                evil.Software.AddRange(new[] { "Git.Git", "x\" & calc & \"" });
                evil.StartupOff.AddRange(new[] { "Discord", " " });
                PcProfiles.Save(evil, path);
                var p = PcProfiles.Load(path);
                Is(p.Tweaks.SequenceEqual(new[] { "ui.ext" }), "réglages");
                Is(p.RemoveApps.SequenceEqual(new[] { "Microsoft.BingNews" }), "le Store ou winget accepté au retrait !");
                Is(p.Software.SequenceEqual(new[] { "Git.Git" }), "logiciels");
                Is(p.StartupOff.SequenceEqual(new[] { "Discord" }), "démarrage");
            });
            T("fichiers invalides refusés", () =>
            {
                var a = Path.Combine(tmp, "pasjson.json"); File.WriteAllText(a, "pas du json");
                Throws<InvalidDataException>(() => PcProfiles.Load(a), "texte");
                var b = Path.Combine(tmp, "format.json"); File.WriteAllText(b, "{\"Format\":99}");
                Throws<InvalidDataException>(() => PcProfiles.Load(b), "format inconnu");
                var c = Path.Combine(tmp, "gros.json"); using (var f = File.Create(c)) f.SetLength(3L << 20);
                Throws<InvalidDataException>(() => PcProfiles.Load(c), "trop gros");
            });
            T("un profil vide ne propose rien", () => Is(PcProfiles.BuildPlan(new PcProfile(), new Job()).Count == 0, "lignes"));
            T("le profil de ce PC, comparé à ce PC : rien à changer", () =>
            {
                var job = new Job();
                var p = PcProfiles.Capture(job);
                Line("        " + p.Summary);
                var path = Path.Combine(tmp, "moi.json");
                PcProfiles.Save(p, path);
                var rows = PcProfiles.BuildPlan(PcProfiles.Load(path), job);
                Is(rows.Count == 0, rows.Count + " changements proposés : " + string.Join(", ", rows.Select(r => r.Label).Take(5)));
            });
            T("un réglage qui réduit la sécurité n'est jamais présélectionné", () =>
            {
                var t = TweakCatalog.ById("game.hvci");
                Is(t.Risk == Risk.Advanced, "risque");
                t.RefreshState();
                if (t.IsSupported && !t.IsApplied)
                {
                    var p = new PcProfile(); p.Tweaks.Add(t.Id);
                    var r = PcProfiles.BuildPlan(p, new Job()).Single();
                    Is(!r.Selected, "présélectionné");
                    Is(r.Note.Contains("sécurité"), "avertissement absent");
                }
            });
            T("un échec n'arrête pas le reste, et tout est résumé", () =>
            {
                var ran = new List<string>();
                var rows = new List<PlanRow>
                {
                    new PlanRow { Group = "G", Label = "A", Available = true, Selected = true, Run = j => ran.Add("A") },
                    new PlanRow { Group = "G", Label = "B", Available = true, Selected = true, Run = j => { throw new InvalidOperationException("x"); } },
                    new PlanRow { Group = "G", Label = "C", Available = true, Selected = true, Run = j => ran.Add("C") },
                    new PlanRow { Group = "G", Label = "D", Available = true, Selected = false, Run = j => ran.Add("D") },
                    new PlanRow { Group = "G", Label = "E", Available = false, Selected = true, Run = j => ran.Add("E") },
                };
                var job = new Job();
                PcProfiles.Apply(rows, false, job);
                Is(ran.SequenceEqual(new[] { "A", "C" }), string.Join("", ran));
                Is(job.Summary.Contains("2 sur 3") && job.Summary.Contains("B"), job.Summary);
            });
        }

        static MetricSnapshot Snap(long ram, int proc, long free, long boot, double up)
        {
            return new MetricSnapshot { Ram = ram, Processes = proc, Services = 100, Startup = 5, Cpu = 2, Free = free, BootMs = boot, UptimeMin = up, When = DateTime.Now.ToString("o"), Label = "T" };
        }

        static void MetricsAndDisks()
        {
            Line("Mesures et santé des disques");
            T("mesure réelle", () =>
            {
                var s = Metrics.Capture("Test", CancellationToken.None);
                Is(s.Ram > 0 && s.Processes > 0 && s.Free > 0 && s.Cpu >= 0 && s.Cpu <= 100, "valeurs");
                Is(s.Services > 0, "services");
                Line("        " + s.Processes + " processus, " + s.Services + " services, " + Fmt.Size(s.Ram) + " utilisés, démarrage : " + (s.BootMs < 0 ? "réservé à l'administrateur" : s.BootMs + " ms"));
                Store.AddMetric(s);
                Is(Store.Metrics().Last().Label == "Test", "non conservée");
            });
            T("lecture de l'événement de démarrage de Windows", () =>
            {
                var xml = "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><EventID>100</EventID></System><EventData><Data Name='BootTsVersion'>2</Data><Data Name='BootTime'>41234</Data><Data Name='MainPathBootTime'>20000</Data></EventData></Event>";
                Is(Metrics.ParseBootEvent(xml) == 41234, "valeur");
                Is(Metrics.ParseBootEvent("<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'/>") == -1, "absent");
            });
            T("un écart sous le bruit de mesure n'est pas présenté comme un gain", () =>
            {
                var r = Metrics.Compare(Snap(1000L << 20, 200, 50L << 30, 30000, 60), Snap(1010L << 20, 201, 50L << 30, 30000, 60));
                Is(r.First(x => x.Name == "Mémoire utilisée").Level == "info", "mémoire");
                Is(r.First(x => x.Name == "Processus").Delta == "Stable", "processus");
            });
            T("gains et pertes dans le bon sens", () =>
            {
                var r = Metrics.Compare(Snap(4000L << 20, 200, 50L << 30, 60000, 60), Snap(3000L << 20, 260, 40L << 30, 30000, 60));
                Is(r.First(x => x.Name == "Mémoire utilisée").Level == "ok", "moins de mémoire");
                Is(r.First(x => x.Name == "Processus").Level == "warn", "plus de processus");
                Is(r.First(x => x.Name == "Espace libre").Level == "warn", "moins d'espace");
                Is(r.First(x => x.Name.StartsWith("Dernier démarrage")).Level == "ok", "démarrage plus court");
            });
            T("un temps de démarrage inconnu reste inconnu", () =>
            {
                var r = Metrics.Compare(Snap(1, 1, 1, -1, 60), Snap(1, 1, 1, 30000, 60)).First(x => x.Name.StartsWith("Dernier démarrage"));
                Is(r.Before == "Admin requis" && r.Delta == "—" && r.Level == "info", r.Before + " " + r.Delta);
            });
            T("une comparaison faussée est signalée", () =>
            {
                Is(Metrics.FairnessNote(Snap(1, 1, 1, 30000, 2), Snap(1, 1, 1, 25000, 60)).Contains("moins de 5 minutes"), "juste après démarrage");
                Is(Metrics.FairnessNote(Snap(1, 1, 1, 30000, 60), Snap(1, 1, 1, 30000, 90)).Contains("Pas de redémarrage"), "sans redémarrage");
                Is(Metrics.FairnessNote(Snap(1, 1, 1, 30000, 600), Snap(1, 1, 1, 25000, 60)).Contains("comparables"), "comparables");
            });
            T("santé réelle des disques", () =>
            {
                string err;
                var l = DiskHealthReader.Read(out err);
                Is(l.Count > 0, "aucun disque lu. " + err);
                foreach (var d in l) Line("        " + d.Name + " (" + d.Sub + ") : " + d.Verdict);
            });
            T("seuils de santé d'un disque", () =>
            {
                Func<string, DiskHealth> one = j => DiskHealthReader.Parse(j).Single();
                Is(one("{\"Name\":\"A\",\"Media\":\"SSD\",\"Bus\":\"NVMe\",\"Health\":\"Healthy\",\"HasCounters\":true,\"Temp\":40,\"Wear\":3,\"Hours\":100}").Level == "ok", "sain");
                Is(one("{\"Name\":\"A\",\"Media\":\"SSD\",\"Health\":\"Healthy\",\"HasCounters\":true,\"Wear\":75}").Level == "warn", "usure 75");
                Is(one("{\"Name\":\"A\",\"Media\":\"SSD\",\"Health\":\"Healthy\",\"HasCounters\":true,\"Wear\":95}").Level == "bad", "usure 95");
                Is(one("{\"Name\":\"A\",\"Media\":\"HDD\",\"Health\":\"Healthy\",\"HasCounters\":true,\"Temp\":60}").Level == "warn", "HDD chaud");
                Is(one("{\"Name\":\"A\",\"Media\":\"SSD\",\"Health\":\"Healthy\",\"HasCounters\":true,\"Temp\":60}").Level == "ok", "SSD tiède");
                Is(one("{\"Name\":\"A\",\"Media\":\"HDD\",\"Health\":\"Unhealthy\",\"HasCounters\":false}").Level == "bad", "défaillant");
                Is(one("{\"Name\":\"A\",\"Media\":\"HDD\",\"Health\":\"Healthy\",\"HasCounters\":true,\"ReadErr\":3}").Level == "warn", "erreurs");
                Is(one("{\"Name\":\"A\",\"Media\":\"HDD\",\"Health\":\"Warning\",\"HasCounters\":true,\"Wear\":99}").Facts.All(f => f.Key != "Usure"), "usure sur un HDD");
                Is(DiskHealthReader.Parse("[{\"Name\":\"A\"},{\"Name\":\"B\"}]").Count == 2, "tableau");
                Is(DiskHealthReader.Parse("").Count == 0, "vide");
            });
        }

        /// <summary>Petit serveur HTTP local, pour éprouver le téléchargement sans toucher à Internet.</summary>
        sealed class TinyServer : IDisposable
        {
            readonly TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            readonly byte[] data;
            readonly Thread t;
            public bool HonorRange = true;
            public int CutFirstAfter = -1;
            public long LastRange = -1;
            public int Requests;
            public int Port { get { return ((IPEndPoint)l.LocalEndpoint).Port; } }
            public string Url { get { return "http://127.0.0.1:" + Port + "/image.esd"; } }

            public TinyServer(byte[] data)
            {
                this.data = data;
                l.Start();
                t = new Thread(Loop) { IsBackground = true };
                t.Start();
            }

            void Loop()
            {
                try
                {
                    while (true)
                    {
                        using (var c = l.AcceptTcpClient())
                        using (var s = c.GetStream())
                        {
                            var req = new StringBuilder();
                            var b = new byte[1];
                            while (!req.ToString().EndsWith("\r\n\r\n") && s.Read(b, 0, 1) == 1) req.Append((char)b[0]);
                            Requests++;
                            long from = 0;
                            var m = Regex.Match(req.ToString(), @"Range: bytes=(\d+)-", RegexOptions.IgnoreCase);
                            if (m.Success) LastRange = long.Parse(m.Groups[1].Value);
                            var partial = m.Success && HonorRange;
                            if (partial) from = LastRange;
                            var head = (partial ? "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes " + from + "-" + (data.Length - 1) + "/" + data.Length + "\r\n" : "HTTP/1.1 200 OK\r\n")
                                + "Content-Length: " + (data.Length - from) + "\r\nConnection: close\r\n\r\n";
                            var hb = Encoding.ASCII.GetBytes(head);
                            s.Write(hb, 0, hb.Length);
                            var n = (int)(data.Length - from);
                            if (CutFirstAfter >= 0) { n = Math.Min(n, CutFirstAfter); CutFirstAfter = -1; }
                            s.Write(data, (int)from, n);
                            s.Flush();
                        }
                    }
                }
                catch { }
            }

            public void Dispose() { try { l.Stop(); } catch { } }
        }

        static string Sha1Hex(byte[] d)
        {
            using (var s = SHA1.Create()) return BitConverter.ToString(s.ComputeHash(d)).Replace("-", "").ToLowerInvariant();
        }

        static void DownloadTests(string tmp)
        {
            Line("Téléchargement de Windows (serveur local, aucun accès à Internet)");
            var data = new byte[3 << 20];
            new Random(7).NextBytes(data);
            var sha = Sha1Hex(data);
            Func<Uri, bool> local = u => true;
            var dir = Path.Combine(tmp, "dl");
            Directory.CreateDirectory(dir);

            T("seules les adresses de Microsoft sont acceptées", () =>
            {
                Is(WinDownload.IsTrustedHost(new Uri("https://dl.delivery.mp.microsoft.com/x.esd")), "delivery");
                Is(WinDownload.IsTrustedHost(new Uri("http://b1.download.windowsupdate.com/x.esd")), "windowsupdate");
                Is(!WinDownload.IsTrustedHost(new Uri("https://microsoft.com.exemple.org/x.esd")), "sous-domaine piégé");
                Is(!WinDownload.IsTrustedHost(new Uri("https://faux-microsoft.com/x.esd")), "nom proche");
                Is(!WinDownload.IsTrustedHost(new Uri("ftp://dl.microsoft.com/x.esd")), "protocole");
                Is(!WinDownload.IsTrustedHost(new Uri("file:///C:/x.esd")), "fichier local");
                Throws<InvalidOperationException>(() => WinDownload.Download("https://exemple.org/x.esd", Path.Combine(dir, "x"), 1, "", new Job()), "hôte refusé");
            });
            T("téléchargement complet et empreinte contrôlée", () =>
            {
                using (var s = new TinyServer(data))
                {
                    var dst = Path.Combine(dir, "a.esd");
                    WinDownload.Download(s.Url, dst, data.Length, sha, new Job(), local);
                    Is(File.ReadAllBytes(dst).SequenceEqual(data), "contenu");
                    Is(!File.Exists(dst + ".part"), "fichier partiel resté");
                }
            });
            T("reprise après une interruption", () =>
            {
                using (var s = new TinyServer(data))
                {
                    var dst = Path.Combine(dir, "b.esd");
                    File.WriteAllBytes(dst + ".part", data.Take(1 << 20).ToArray());
                    WinDownload.Download(s.Url, dst, data.Length, sha, new Job(), local);
                    Is(s.LastRange == 1 << 20, "reprise demandée à " + s.LastRange);
                    Is(File.ReadAllBytes(dst).SequenceEqual(data), "contenu");
                }
            });
            T("serveur sans reprise : repart de zéro sans mélanger", () =>
            {
                using (var s = new TinyServer(data) { HonorRange = false })
                {
                    var dst = Path.Combine(dir, "c.esd");
                    File.WriteAllBytes(dst + ".part", new byte[1 << 20]);
                    WinDownload.Download(s.Url, dst, data.Length, sha, new Job(), local);
                    Is(File.ReadAllBytes(dst).SequenceEqual(data), "contenu mélangé");
                }
            });
            T("connexion coupée : nouvelle tentative et reprise", () =>
            {
                using (var s = new TinyServer(data) { CutFirstAfter = 1 << 20 })
                {
                    var dst = Path.Combine(dir, "d.esd");
                    var logs = new List<string>();
                    var j = new Job(); j.OnLog += x => logs.Add(x);
                    WinDownload.Download(s.Url, dst, data.Length, sha, j, local);
                    Is(s.Requests >= 2, "une seule requête");
                    Is(logs.Any(x => x.Contains("nouvelle tentative")), "pas de nouvel essai");
                    Is(File.ReadAllBytes(dst).SequenceEqual(data), "contenu");
                }
            });
            T("empreinte fausse : rien n'est conservé", () =>
            {
                using (var s = new TinyServer(data))
                {
                    var dst = Path.Combine(dir, "e.esd");
                    Throws<InvalidDataException>(() => WinDownload.Download(s.Url, dst, data.Length, new string('0', 40), new Job(), local), "empreinte");
                    Is(!File.Exists(dst) && !File.Exists(dst + ".part"), "fichier conservé");
                }
            });
            T("taille inattendue : rien n'est conservé", () =>
            {
                using (var s = new TinyServer(data))
                {
                    var dst = Path.Combine(dir, "f.esd");
                    Throws<InvalidDataException>(() => WinDownload.Download(s.Url, dst, data.Length - 10, "", new Job(), local), "taille");
                    Is(!File.Exists(dst), "fichier conservé");
                }
            });
            T("annulation : le fichier partiel reste pour reprendre", () =>
            {
                using (var s = new TinyServer(data))
                {
                    var dst = Path.Combine(dir, "g.esd");
                    var j = new Job(); j.Cancel();
                    Throws<OperationCanceledException>(() => WinDownload.Download(s.Url, dst, data.Length, sha, j, local), "annulation");
                    Is(!File.Exists(dst), "fichier final créé");
                }
            });

            const string Cat = "<MCT><Catalogs><Catalog><PublishedMedia><Files>" +
                "<File><FileName>26100.1.240331_CLIENTCONSUMER_RET_x64FRE_fr-fr.esd</FileName><LanguageCode>fr-fr</LanguageCode><Language>French</Language><Edition>Professional</Edition><Architecture>x64</Architecture><Size>4000000000</Size><Sha1>ABC</Sha1><FilePath>http://dl.delivery.mp.microsoft.com/a/x64.esd</FilePath></File>" +
                "<File><FileName>26100.1.240331_CLIENTBUSINESS_VOL_x64FRE_fr-fr.esd</FileName><LanguageCode>fr-fr</LanguageCode><Language>French</Language><Edition>Enterprise</Edition><Architecture>x64</Architecture><Size>1</Size><Sha1>D</Sha1><FilePath>http://dl.delivery.mp.microsoft.com/a/ent.esd</FilePath></File>" +
                "<File><FileName>a_ARM64FRE_en-us.esd</FileName><LanguageCode>en-us</LanguageCode><Language>English</Language><Edition>Professional</Edition><Architecture>ARM64</Architecture><Size>2</Size><Sha1>E</Sha1><FilePath>http://dl.delivery.mp.microsoft.com/a/arm.esd</FilePath></File>" +
                "<File><FileName>piege.esd</FileName><LanguageCode>fr-fr</LanguageCode><Language>French</Language><Edition>Professional</Edition><Architecture>x86</Architecture><Size>3</Size><Sha1>F</Sha1><FilePath>http://exemple.org/piege.esd</FilePath></File>" +
                "<File><FileName>setup.exe</FileName><LanguageCode>fr-fr</LanguageCode><Edition>Professional</Edition><Architecture>x64</Architecture><Size>4</Size><FilePath>https://dl.delivery.mp.microsoft.com/a/setup.exe</FilePath></File>" +
                "</Files></PublishedMedia></Catalog></Catalogs></MCT>";

            T("catalogue : lecture, adresses étrangères et non-images écartées", () =>
            {
                var l = WinDownload.ParseCatalog(Cat);
                Is(l.Count == 3, "nombre " + l.Count);
                Is(l[0].Build == "26100.1" && l[0].Size == 4000000000L && l[0].Sha1 == "abc", "champs");
                Is(!l.Any(f => f.FileName == "piege.esd"), "adresse étrangère acceptée");
            });
            T("catalogue : langues, architectures, image grand public", () =>
            {
                var l = WinDownload.ParseCatalog(Cat);
                Is(WinDownload.Languages(l).Count == 2, "langues");
                Is(WinDownload.Architectures(l, "fr-fr").SequenceEqual(new[] { "x64" }), "architectures");
                Is(WinDownload.Choose(l, "fr-fr", "x64").Edition == "Professional", "édition");
                Is(WinDownload.Choose(l, "de-de", "x64") == null, "langue absente");
            });
            T("catalogue accepté seulement en HTTPS depuis Microsoft", () =>
            {
                Is(WinDownload.IsCatalogSource(new Uri("https://download.microsoft.com/download/x/products.cab")), "source officielle");
                Is(!WinDownload.IsCatalogSource(new Uri("http://download.microsoft.com/download/x/products.cab")), "sans HTTPS");
                Is(!WinDownload.IsCatalogSource(new Uri("https://exemple.org/products.cab")), "autre site");
                Is(!WinDownload.IsCatalogSource(new Uri("https://download.microsoft.com.exemple.org/products.cab")), "sous-domaine piégé");
            });
            T("extraction d'une vraie archive, à côté de celle-ci", () =>
            {
                var d = Path.Combine(tmp, "cab"); Directory.CreateDirectory(d);
                var src = Path.Combine(d, "source.xml");
                File.WriteAllText(src, Cat, new UTF8Encoding(false));
                var cab = Path.Combine(d, "products.cab");
                // L'archive doit contenir un fichier nommé products.xml, comme celle de Microsoft.
                var inner = Path.Combine(d, "products.xml");
                File.Move(src, inner);
                var code = Proc.Run("makecab.exe", "\"" + inner + "\" \"" + cab + "\"", null, default(CancellationToken), d);
                Is(code == 0 && File.Exists(cab), "makecab, code " + code);
                File.Delete(inner);
                var xml = WinDownload.ExtractCatalog(cab, d, CancellationToken.None);
                Is(Path.GetFileName(xml) == "products.xml" && File.Exists(xml), "sortie");
                Is(WinDownload.ParseCatalog(File.ReadAllText(xml, Encoding.UTF8)).Count == 3, "contenu");
            });
            T("une image sans empreinte valide n'est jamais téléchargée", () =>
            {
                foreach (var bad in new[] { "", null, "abc", new string('z', 40) })
                {
                    var f = new EsdFile { FileName = "x.esd", Size = 1, Sha1 = bad, Url = "http://dl.delivery.mp.microsoft.com/x.esd" };
                    Throws<InvalidDataException>(() => WinDownload.DownloadAndBuild(f, Path.Combine(tmp, "x.iso"), "X", true, new Job()), "empreinte « " + bad + " »");
                }
            });
            T("espace requis suffisant pour l'image, le support et l'ISO", () =>
            {
                var need = WinDownload.SpaceNeeded(new EsdFile { Size = 4700000000L });
                Is(need >= 3 * 4700000000L, "trop juste : " + need);
            });
            T("catalogue piégé (entité externe) refusé", () =>
            {
                Throws<System.Xml.XmlException>(() => WinDownload.ParseCatalog("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///C:/Windows/win.ini\">]><MCT><File><FileName>&e;</FileName></File></MCT>"), "DTD");
            });
            T("éditions à conserver", () =>
            {
                var info = "Index : 1\nName : Windows 11 Setup Media\nIndex : 2\nName : Microsoft Windows PE (amd64)\nIndex : 3\nName : Microsoft Windows Setup (amd64)\nIndex : 4\nName : Windows 11 Home\nIndex : 5\nName : Windows 11 Home N\nIndex : 6\nName : Windows 11 Pro\nIndex : 7\nName : Windows 11 Pro N\nIndex : 8\nName : Windows 11 Education\n";
                var img = ImageCustomizer.ParseEditions(info);
                Is(img.Count == 8, "lecture " + img.Count);
                Is(WinDownload.EditionsToKeep(img, true).SequenceEqual(new[] { 4, 6 }), "Famille et Pro");
                Is(WinDownload.EditionsToKeep(img, false).SequenceEqual(new[] { 4, 5, 6, 7, 8 }), "toutes");
                var only = ImageCustomizer.ParseEditions("Index : 1\nName : a\nIndex : 2\nName : b\nIndex : 3\nName : c\nIndex : 4\nName : Windows 11 Education\n");
                Is(WinDownload.EditionsToKeep(only, true).SequenceEqual(new[] { 4 }), "repli si ni Famille ni Pro");
            });
        }

        static void MultibootTests(string tmp)
        {
            Line("Multiboot");
            T("la vérification USB de Ventoy n'est jamais désactivée", () =>
            {
                Is(Multiboot.InstallArgs(3, false) == "VTOYCLI /I /PhyDrive:3", Multiboot.InstallArgs(3, false));
                Is(Multiboot.InstallArgs(2, true) == "VTOYCLI /I /PhyDrive:2 /GPT", Multiboot.InstallArgs(2, true));
                Is(Multiboot.InstallArgs(1, true).IndexOf("NOUSBCheck", StringComparison.OrdinalIgnoreCase) < 0, "vérification coupée");
            });
            T("recherche de Ventoy et des clés existantes, sans rien installer", () =>
            {
                var exe = Multiboot.FindVentoy();
                var keys = Multiboot.ExistingKeys();
                Line("        Ventoy : " + (exe ?? "absent") + " ; clés : " + keys.Count);
                Is(keys.All(k => k[0] != (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:")[0]), "disque système vu comme clé Ventoy");
            });
            var src = Path.Combine(tmp, "mb-src"); var dst = Path.Combine(tmp, "mb-dst");
            Directory.CreateDirectory(src); Directory.CreateDirectory(dst);
            var a = Path.Combine(src, "a.iso"); var b = Path.Combine(src, "b é.iso");
            var da = new byte[5 << 20]; new Random(1).NextBytes(da);
            File.WriteAllBytes(a, da); File.WriteAllBytes(b, new byte[1234]);
            T("copie de plusieurs images, tailles contrôlées", () =>
            {
                Multiboot.CopyImages(dst, new[] { a, b }, new Job());
                Is(File.ReadAllBytes(Path.Combine(dst, "a.iso")).SequenceEqual(da), "a");
                Is(new FileInfo(Path.Combine(dst, "b é.iso")).Length == 1234, "b");
            });
            T("place insuffisante : refus avant de copier", () =>
            {
                var d = Path.Combine(tmp, "mb-full"); Directory.CreateDirectory(d);
                Throws<IOException>(() => Multiboot.CopyImages(d, new[] { a }, new Job(), 10), "place");
                Is(!File.Exists(Path.Combine(d, "a.iso")), "copie commencée");
            });
            T("annulation : pas d'image tronquée sur la clé", () =>
            {
                var d = Path.Combine(tmp, "mb-can"); Directory.CreateDirectory(d);
                var cts = new Job();
                var big = Path.Combine(src, "c.iso"); File.WriteAllBytes(big, new byte[8 << 20]);
                cts.OnProgress += p => { if (p > 0) cts.Cancel(); };
                Throws<OperationCanceledException>(() => Multiboot.CopyImages(d, new[] { big }, cts), "annulation");
                Is(!File.Exists(Path.Combine(d, "c.iso")), "image tronquée restée");
            });
        }

        static void Rescue(string tmp)
        {
            Line("Kit de secours");
            var text = RescueKit.ScriptText;
            T("script lisible par cmd : sans accent, fins de ligne CRLF", () =>
            {
                Is(text.All(c => c < 128), "caractère non ASCII");
                Is(!Regex.IsMatch(text, "(?<!\r)\n"), "fin de ligne LF");
            });
            T("toutes les destinations existent", () =>
            {
                var labels = new HashSet<string>(Regex.Matches(text, @"(?m)^:(\w+)").Cast<Match>().Select(m => m.Groups[1].Value), StringComparer.OrdinalIgnoreCase);
                var targets = Regex.Matches(text, @"(?i)\b(?:goto|call)\s+:?(\w+)").Cast<Match>().Select(m => m.Groups[1].Value).Where(x => !x.Equals("eof", StringComparison.OrdinalIgnoreCase)).Distinct();
                foreach (var x in targets) Is(labels.Contains(x), "étiquette absente : " + x);
            });
            T("aucune commande destructrice", () =>
            {
                foreach (var bad in new[] { @"\bformat\b", @"\bdel\b", @"\berase\b", @"\brd\b", @"\brmdir\b", @"\bclean\b", @"/MIR\b", @"/PURGE\b", @"/MOVE\b", @"\breg\s+delete\b" })
                    Is(!Regex.IsMatch(text.Replace("/Cleanup-Image", ""), "(?m)^(?!rem|echo| *echo).*" + bad, RegexOptions.IgnoreCase), bad);
            });
            T("chaque réparation demande confirmation", () =>
            {
                foreach (var cmd in new[] { "chkdsk ", "sfc /scannow", "dism /Image", "bcdboot ", "robocopy " })
                {
                    var i = text.IndexOf(Environment.NewLine + cmd, StringComparison.OrdinalIgnoreCase);
                    Is(i > 0, "commande absente : " + cmd);
                    var before = text.Substring(Math.Max(0, i - 200), Math.Min(200, i));
                    Is(before.Contains("call :confirm"), "pas de confirmation avant " + cmd);
                }
            });
            T("écriture du kit, et détection réelle de Windows sans rien modifier", () =>
            {
                var d = Path.Combine(tmp, @"key\OptiWin\Secours");
                RescueKit.WriteTo(d);
                Is(File.Exists(Path.Combine(d, "secours.cmd")) && File.ReadAllText(Path.Combine(d, "LISEZMOI.txt")).Contains("Maj + F10"), "fichiers");
                var o = new List<string>();
                var code = Proc.Run("cmd.exe", "/c \"\"" + Path.Combine(d, "secours.cmd") + "\" --check\"", l => o.Add(l));
                Is(code == 0, "code " + code);
                var sys = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").Substring(0, 1);
                var found = o.FirstOrDefault(x => x.StartsWith("WINDOWS_TROUVES:"));
                Is(found != null && found.Contains(sys), "Windows non détecté : " + string.Join(" | ", o));
                Line("        " + string.Join("  ", o));
            });
        }

    }
}
