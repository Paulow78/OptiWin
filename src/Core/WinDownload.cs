using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace OptiWin
{
    public sealed class WinProduct
    {
        public string Name, CabUrl, IsoLabel;
        public override string ToString() { return Name; }
    }

    public sealed class EsdFile
    {
        public string FileName, LanguageCode, Language, Edition, Arch, Sha1, Url;
        public long Size;
        /// <summary>Numéro de build lu dans le nom du fichier, par exemple 26100.4349.</summary>
        public string Build
        {
            get
            {
                var m = Regex.Match(FileName ?? "", @"^(\d+\.\d+)");
                return m.Success ? m.Groups[1].Value : "?";
            }
        }
    }

    public sealed class LanguageChoice
    {
        public string Code, Name;
        public override string ToString() { return Name; }
    }

    /// <summary>
    /// Télécharge Windows depuis le catalogue public du Media Creation Tool, qui liste les images officielles
    /// et leur empreinte. Microsoft ne signe pas ce fichier : la garantie vient de HTTPS. Le catalogue doit donc
    /// provenir d'un serveur de Microsoft en HTTPS, et l'image n'est gardée que si son empreinte correspond.
    /// </summary>
    public static class WinDownload
    {
        public static readonly WinProduct[] Products =
        {
            new WinProduct { Name = "Windows 11", CabUrl = "https://go.microsoft.com/fwlink/?LinkId=2156292", IsoLabel = "Windows11" },
            new WinProduct { Name = "Windows 10", CabUrl = "https://go.microsoft.com/fwlink/?LinkId=841361", IsoLabel = "Windows10" },
        };

        static WinDownload()
        {
            // Nécessaire pour HTTPS sur les vieilles versions de .NET Framework.
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
        }

        public static bool IsTrustedHost(Uri u)
        {
            if (u == null || (u.Scheme != "https" && u.Scheme != "http")) return false;
            var h = u.Host.ToLowerInvariant();
            return h == "microsoft.com" || h.EndsWith(".microsoft.com", StringComparison.Ordinal) || h.EndsWith(".windowsupdate.com", StringComparison.Ordinal);
        }

        public static List<EsdFile> FetchCatalog(WinProduct p, Job job)
        {
            var dir = Path.Combine(Path.GetTempPath(), "OptiWin-cat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var cab = Path.Combine(dir, "products.cab");
                job.Status("Lecture du catalogue de Microsoft");
                FetchCab(new Uri(p.CabUrl), cab, job);
                job.Token.ThrowIfCancellationRequested();

                var list = ParseCatalog(File.ReadAllText(ExtractCatalog(cab, dir, job.Token), Encoding.UTF8));
                if (list.Count == 0) throw new InvalidDataException("Le catalogue de Microsoft ne contient aucune image.");
                job.Log(list.Count + " images répertoriées, build " + list[0].Build + ".");
                return list;
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>Le catalogue n'étant pas signé, seul HTTPS depuis un serveur de Microsoft en garantit l'origine.</summary>
        public static bool IsCatalogSource(Uri u) { return u != null && u.Scheme == "https" && IsTrustedHost(u); }

        const long MaxCatalogBytes = 16L << 20;

        static void FetchCab(Uri u, string path, Job job)
        {
            var req = (HttpWebRequest)WebRequest.Create(u);
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            req.UserAgent = "OptiWin";
            using (job.Token.Register(() => { try { req.Abort(); } catch { } }))
            using (var r = (HttpWebResponse)req.GetResponse())
            {
                // Vérifié après les redirections : c'est l'adresse finale qui compte.
                if (!IsCatalogSource(r.ResponseUri))
                    throw new InvalidDataException("Le catalogue doit venir d'un serveur de Microsoft en HTTPS. Reçu : " + r.ResponseUri.Scheme + "://" + r.ResponseUri.Host);
                using (var input = r.GetResponseStream())
                using (var output = File.Create(path))
                {
                    var buf = new byte[1 << 16];
                    long total = 0;
                    int n;
                    while ((n = input.Read(buf, 0, buf.Length)) > 0)
                    {
                        total += n;
                        if (total > MaxCatalogBytes) throw new InvalidDataException("Ce fichier est trop gros pour être le catalogue de Microsoft.");
                        output.Write(buf, 0, n);
                    }
                }
            }
        }

        /// <summary>
        /// Extrait products.xml. La sortie doit être nommée : vers un simple dossier, expand.exe
        /// donne au fichier le nom de l'archive et refuse même d'écrire à côté d'elle.
        /// </summary>
        public static string ExtractCatalog(string cab, string dir, CancellationToken ct)
        {
            var xml = Path.Combine(dir, "products.xml");
            Proc.RunChecked("expand.exe", "\"" + cab + "\" -F:products.xml \"" + xml + "\"", null, ct);
            if (!File.Exists(xml)) throw new InvalidDataException("Le catalogue ne contient pas la liste attendue.");
            return xml;
        }

        /// <summary>Espace nécessaire le temps de l'opération : l'image, puis le support décompressé et l'ISO.</summary>
        public static long SpaceNeeded(EsdFile f) { return f.Size * 4 + (2L << 30); }

        public static List<EsdFile> ParseCatalog(string xml)
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var r = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                doc.Load(r);
            var res = new List<EsdFile>();
            foreach (XmlNode n in doc.SelectNodes("//File"))
            {
                Func<string, string> t = name => { var c = n.SelectSingleNode(name); return c == null ? "" : c.InnerText.Trim(); };
                var url = t("FilePath");
                long size;
                long.TryParse(t("Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                if (!url.EndsWith(".esd", StringComparison.OrdinalIgnoreCase)) continue;
                Uri u;
                // Le catalogue est signé, mais on ne suit tout de même que des adresses de Microsoft.
                if (!Uri.TryCreate(url, UriKind.Absolute, out u) || !IsTrustedHost(u)) continue;
                res.Add(new EsdFile
                {
                    FileName = Path.GetFileName(t("FileName")), LanguageCode = t("LanguageCode").ToLowerInvariant(), Language = t("Language"),
                    Edition = t("Edition"), Arch = t("Architecture"), Sha1 = t("Sha1").ToLowerInvariant(), Url = url, Size = size
                });
            }
            return res;
        }

        public static List<LanguageChoice> Languages(IEnumerable<EsdFile> files)
        {
            return files.GroupBy(f => f.LanguageCode).Select(g => new LanguageChoice { Code = g.Key, Name = g.First().Language + " (" + g.Key + ")" })
                        .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static List<string> Architectures(IEnumerable<EsdFile> files, string lang)
        {
            return files.Where(f => f.LanguageCode == lang).Select(f => f.Arch).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(a => a == "x64").ToList();
        }

        /// <summary>L'image grand public « Professional » contient Famille, Pro et Éducation.</summary>
        public static EsdFile Choose(IEnumerable<EsdFile> files, string lang, string arch)
        {
            var c = files.Where(f => f.LanguageCode == lang && string.Equals(f.Arch, arch, StringComparison.OrdinalIgnoreCase)).ToList();
            return c.FirstOrDefault(f => string.Equals(f.Edition, "Professional", StringComparison.OrdinalIgnoreCase)) ?? c.FirstOrDefault();
        }

        /// <summary>
        /// Téléchargement avec reprise, jusqu'à 5 tentatives, puis contrôle de l'empreinte.
        /// Le fichier final n'apparaît sous son vrai nom qu'une fois vérifié.
        /// </summary>
        public static void Download(string url, string dest, long size, string sha1, Job job, Func<Uri, bool> trust = null)
        {
            var u = new Uri(url);
            if (!(trust ?? IsTrustedHost)(u)) throw new InvalidOperationException("Adresse refusée : " + u.Host + " n'appartient pas à Microsoft.");
            var part = dest + ".part";
            int failures = 0;
            while (true)
            {
                job.Token.ThrowIfCancellationRequested();
                var before = File.Exists(part) ? new FileInfo(part).Length : 0;
                Exception last;
                try
                {
                    Fetch(u, part, size, job);
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (WebException e) { last = e; }
                catch (IOException e) { last = e; }
                // Une connexion instable qui avance peut continuer : seuls comptent les essais sans aucun progrès.
                var after = File.Exists(part) ? new FileInfo(part).Length : 0;
                failures = after > before ? 1 : failures + 1;
                if (failures > 5) throw new IOException("Téléchargement interrompu : " + last.Message + " Relancez-le : il reprendra où il s'est arrêté.", last);
                job.Log("Connexion interrompue (" + last.Message + "), nouvelle tentative " + failures + " sur 5.");
                job.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Min(30, failures * 3)));
            }

            var got = new FileInfo(part).Length;
            if (size > 0 && got != size) { File.Delete(part); throw new InvalidDataException("Taille inattendue : " + got + " octets au lieu de " + size + "."); }
            if (!string.IsNullOrEmpty(sha1))
            {
                job.Status("Contrôle de l'empreinte");
                job.Progress(-1);
                var actual = Sha1Of(part, job);
                if (!string.Equals(actual, sha1, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(part);
                    throw new InvalidDataException("L'empreinte ne correspond pas à celle du catalogue : fichier supprimé. Relancez le téléchargement.");
                }
                job.Log("Empreinte conforme au catalogue de Microsoft.");
            }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(part, dest);
        }

        static void Fetch(Uri u, string part, long size, Job job)
        {
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (size > 0 && have == size) return;
            if (size > 0 && have > size) { File.Delete(part); have = 0; }

            var req = (HttpWebRequest)WebRequest.Create(u);
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            req.AllowAutoRedirect = true;
            req.UserAgent = "OptiWin";
            if (have > 0) req.AddRange(have);
            using (job.Token.Register(() => { try { req.Abort(); } catch { } }))
            {
                try
                {
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        // Serveur sans reprise : il renvoie tout depuis le début, on repart de zéro.
                        var resume = resp.StatusCode == HttpStatusCode.PartialContent;
                        if (!resume) have = 0;
                        var total = size > 0 ? size : resp.ContentLength >= 0 ? have + resp.ContentLength : 0;
                        using (var input = resp.GetResponseStream())
                        using (var output = new FileStream(part, resume ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                        {
                            var buf = new byte[1 << 20];
                            var start = DateTime.UtcNow;
                            long session = 0, done = have;
                            int n;
                            while ((n = input.Read(buf, 0, buf.Length)) > 0)
                            {
                                job.Token.ThrowIfCancellationRequested();
                                output.Write(buf, 0, n);
                                done += n; session += n;
                                if (total > 0) job.Progress(100.0 * done / total);
                                var sec = (DateTime.UtcNow - start).TotalSeconds;
                                if (sec > 0.5 && total > 0)
                                {
                                    var speed = session / sec;
                                    job.Status2(Fmt.Size((long)speed) + "/s — " + Fmt.Size(done) + " sur " + Fmt.Size(total) + " — reste " + Fmt.Span(TimeSpan.FromSeconds(Math.Max(0, total - done) / Math.Max(1, speed))));
                                }
                            }
                            // Une connexion coupée en cours de route se termine souvent sans erreur : on le voit à la taille.
                            if (total > 0 && done < total) throw new IOException("connexion coupée à " + Fmt.Size(done));
                        }
                    }
                }
                catch (WebException) { job.Token.ThrowIfCancellationRequested(); throw; }
            }
        }

        static string Sha1Of(string path, Job job)
        {
            using (var sha = SHA1.Create())
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            {
                var buf = new byte[1 << 20];
                int n; long done = 0;
                while ((n = f.Read(buf, 0, buf.Length)) > 0)
                {
                    job.Token.ThrowIfCancellationRequested();
                    sha.TransformBlock(buf, 0, n, null, 0);
                    done += n;
                    job.Progress(100.0 * done / Math.Max(1, f.Length));
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>Index des éditions à garder. Se limiter à Famille et Pro donne une ISO plus légère, plus vite créée.</summary>
        public static List<int> EditionsToKeep(IList<WimEdition> images, bool homeProOnly)
        {
            var editions = images.Where(i => i.Index >= 4 && i.Name.IndexOf("Setup", StringComparison.OrdinalIgnoreCase) < 0 && i.Name.IndexOf("Windows PE", StringComparison.OrdinalIgnoreCase) < 0).ToList();
            if (homeProOnly)
            {
                var sel = editions.Where(e => Regex.IsMatch(e.Name, @"\b(Home|Pro)$", RegexOptions.IgnoreCase)).ToList();
                if (sel.Count > 0) editions = sel;
            }
            return editions.Select(e => e.Index).ToList();
        }

        /// <summary>
        /// Transforme l'image ESD en ISO installable : index 1 pour le support d'installation,
        /// 2 et 3 pour boot.wim, les suivants pour install.wim. L'image de Microsoft fait plus de 4 Go :
        /// un .esd de cette taille ne tiendrait pas sur une clé FAT32 et ne se découpe pas, un .wim si.
        /// </summary>
        public static void BuildIso(string esd, string isoOut, string label, bool homeProOnly, Job job)
        {
            if (!Os.IsAdmin) throw new InvalidOperationException("Droits administrateur requis.");
            var work = Path.Combine(Path.GetDirectoryName(isoOut), "OptiWin-Media-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
            var media = Path.Combine(work, "media");
            Directory.CreateDirectory(media);
            try
            {
                var o = new StringBuilder();
                Proc.RunChecked("DISM.exe", "/English /Get-WimInfo /WimFile:\"" + esd + "\"", l => o.AppendLine(l), job.Token);
                var images = ImageCustomizer.ParseEditions(o.ToString());
                if (images.Count < 4) throw new InvalidDataException("Cette image ne contient pas les éléments attendus.");
                var keep = EditionsToKeep(images, homeProOnly);
                if (keep.Count == 0) throw new InvalidDataException("Aucune édition de Windows trouvée dans cette image.");

                job.Status("Préparation du support d'installation");
                job.Progress(0);
                Check(Tools.Tracked(job, "DISM.exe", "/Apply-Image /ImageFile:\"" + esd + "\" /Index:1 /ApplyDir:\"" + media + "\""), "préparation");

                job.Status("Création de l'environnement de démarrage");
                job.Progress(0);
                var boot = Path.Combine(media, @"sources\boot.wim");
                Check(Tools.Tracked(job, "DISM.exe", "/Export-Image /SourceImageFile:\"" + esd + "\" /SourceIndex:2 /DestinationImageFile:\"" + boot + "\" /Compress:max"), "démarrage");
                job.Progress(0);
                Check(Tools.Tracked(job, "DISM.exe", "/Export-Image /SourceImageFile:\"" + esd + "\" /SourceIndex:3 /DestinationImageFile:\"" + boot + "\" /Compress:max /Bootable"), "démarrage");

                var install = Path.Combine(media, @"sources\install.wim");
                for (int i = 0; i < keep.Count; i++)
                {
                    job.Status("Édition " + (i + 1) + " sur " + keep.Count + " : " + images.First(x => x.Index == keep[i]).Name);
                    job.Progress(0);
                    Check(Tools.Tracked(job, "DISM.exe", "/Export-Image /SourceImageFile:\"" + esd + "\" /SourceIndex:" + keep[i] + " /DestinationImageFile:\"" + install + "\" /Compress:max"), "export d'une édition");
                }

                IsoBuilder.Create(media, isoOut, label, false, job);
            }
            finally
            {
                try { Directory.Delete(work, true); }
                catch (Exception e) { job.Log("Dossier de travail à supprimer à la main : " + work + " (" + e.Message + ")"); }
            }
        }

        static void Check(int code, string step)
        {
            if (code != 0) throw new InvalidOperationException("Étape « " + step + " » en échec (DISM, code " + code + "). Voir le journal.");
        }

        /// <summary>Télécharge l'image choisie puis construit l'ISO, sans laisser de fichier intermédiaire.</summary>
        public static void DownloadAndBuild(EsdFile f, string isoOut, string label, bool homeProOnly, Job job)
        {
            // Le serveur des images n'a pas de certificat HTTPS valide : elles arrivent en HTTP.
            // L'empreinte, lue dans le catalogue obtenu en HTTPS, est donc leur seule protection : elle est obligatoire.
            if (!Regex.IsMatch(f.Sha1 ?? "", "^[0-9a-f]{40}$"))
                throw new InvalidDataException("Cette image n'a pas d'empreinte valide dans le catalogue : téléchargement refusé.");
            var dir = Path.GetDirectoryName(isoOut);
            Directory.CreateDirectory(dir);
            var needed = SpaceNeeded(f);
            var free = new DriveInfo(Path.GetPathRoot(isoOut)).AvailableFreeSpace;
            if (free < needed) throw new IOException("Espace insuffisant : " + Fmt.Size(needed) + " nécessaires le temps de l'opération, " + Fmt.Size(free) + " disponibles.");

            // Nom stable : un téléchargement interrompu reprend au prochain essai.
            var esd = Path.Combine(dir, f.FileName);
            if (File.Exists(esd) && new FileInfo(esd).Length == f.Size)
                job.Log("Image déjà téléchargée et vérifiée lors d'un essai précédent.");
            else
            {
                job.Status("Téléchargement depuis Microsoft (" + Fmt.Size(f.Size) + ")");
                job.Progress(0);
                Download(f.Url, esd, f.Size, f.Sha1, job);
            }
            BuildIso(esd, isoOut, label, homeProOnly, job);
            try { File.Delete(esd); } catch { }
            job.Summary = "ISO prête : " + isoOut + " (" + Fmt.Size(new FileInfo(isoOut).Length) + ").";
        }
    }
}
