using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace OptiTech
{
    public sealed class UpdateInfo
    {
        public Version Version;
        public string Tag, Name, Notes, PageUrl, ExeUrl, SumsUrl, Sha256;
        public long Size;
        public string VersionText { get { return Updater.Text(Version); } }
    }

    /// <summary>
    /// Mise à jour depuis les releases GitHub du projet. Trois garde-fous : tout passe en HTTPS par les
    /// serveurs de GitHub, l'exécutable n'est installé que si son empreinte SHA-256 correspond à celle
    /// publiée avec la release, et l'ancien exécutable est gardé le temps de l'échange pour pouvoir revenir.
    /// </summary>
    public static class Updater
    {
        public const string Repo = "Paulow78/OptiWin";
        public const string ExeName = "OptiTech.exe";
        public const string ReleasesPage = "https://github.com/" + Repo + "/releases";
        const string Api = "https://api.github.com/repos/" + Repo + "/releases/latest";
        const long MaxExeBytes = 64L << 20;

        static Updater()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
        }

        public static Version Current { get { return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version; } }

        public static string Text(Version v)
        {
            return v == null ? "?" : v.Major + "." + v.Minor + (v.Build > 0 ? "." + v.Build : "");
        }

        /// <summary>« v26.11.2 » ou « 26.11 » ; null pour tout ce qui n'est pas un numéro de version.</summary>
        public static Version ParseTag(string tag)
        {
            if (tag == null) return null;
            var m = Regex.Match(tag.Trim(), @"^v?(\d{1,4})\.(\d{1,4})(?:\.(\d{1,5}))?$");
            if (!m.Success) return null;
            return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0, 0);
        }

        public static bool IsNewer(Version candidate, Version current)
        {
            if (candidate == null || current == null) return false;
            var a = new Version(candidate.Major, candidate.Minor, Math.Max(0, candidate.Build));
            var b = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
            return a > b;
        }

        /// <summary>Seuls les serveurs de GitHub, et seulement en HTTPS.</summary>
        public static bool IsTrusted(Uri u)
        {
            if (u == null || u.Scheme != "https") return false;
            var h = u.Host.ToLowerInvariant();
            return h == "github.com" || h == "api.github.com" || h.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
        }

        static string S(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : "";
        }
        static bool B(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v is bool && (bool)v;
        }

        /// <summary>Lit la réponse de l'API GitHub. Renvoie null si la release n'est pas exploitable.</summary>
        public static UpdateInfo ParseRelease(string json)
        {
            Dictionary<string, object> d;
            try { d = new JavaScriptSerializer { MaxJsonLength = 4 << 20 }.DeserializeObject(json) as Dictionary<string, object>; }
            catch (Exception) { return null; }
            if (d == null || B(d, "draft") || B(d, "prerelease")) return null;
            var ver = ParseTag(S(d, "tag_name"));
            if (ver == null) return null;
            var info = new UpdateInfo { Version = ver, Tag = S(d, "tag_name"), Name = S(d, "name"), Notes = S(d, "body"), PageUrl = S(d, "html_url") };
            Uri page;
            if (!Uri.TryCreate(info.PageUrl, UriKind.Absolute, out page) || !IsTrusted(page)) info.PageUrl = ReleasesPage;

            object assets;
            if (d.TryGetValue("assets", out assets) && assets is object[])
                foreach (var x in (object[])assets)
                {
                    var a = x as Dictionary<string, object>;
                    if (a == null) continue;
                    var name = S(a, "name"); var url = S(a, "browser_download_url");
                    Uri u;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out u) || !IsTrusted(u)) continue;
                    if (name.Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                    {
                        info.ExeUrl = url;
                        long size; long.TryParse(S(a, "size"), out size); info.Size = size;
                        var dg = S(a, "digest");
                        if (dg.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) info.Sha256 = CleanHash(dg.Substring(7));
                    }
                    else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) info.SumsUrl = url;
                }
            return info.ExeUrl == null ? null : info;
        }

        static string CleanHash(string h)
        {
            h = (h ?? "").Trim().ToLowerInvariant();
            return Regex.IsMatch(h, "^[0-9a-f]{64}$") ? h : null;
        }

        /// <summary>Empreinte d'un fichier dans un SHA256SUMS.txt (« empreinte  nom »), ou null.</summary>
        public static string ParseSums(string text, string file)
        {
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var p = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2) continue;
                if (p[p.Length - 1].TrimStart('*').Equals(file, StringComparison.OrdinalIgnoreCase)) return CleanHash(p[0]);
            }
            return null;
        }

        /// <summary>Les premières nouveautés des notes de version, débarrassées de leur mise en forme.</summary>
        public static string Highlights(string notes, int max)
        {
            var lines = new List<string>();
            foreach (var raw in (notes ?? "").Split('\n'))
            {
                var l = raw.Trim();
                if (!l.StartsWith("- ")) continue;
                l = Regex.Replace(l.Substring(2), @"\*\*|`", "").Trim();
                if (l.Length > 150) l = l.Substring(0, 147).TrimEnd() + "…";
                lines.Add("•  " + l);
                if (lines.Count >= max) break;
            }
            return string.Join("\n", lines);
        }

        static string Get(string url, int maxBytes, CancellationToken ct)
        {
            var u = new Uri(url);
            if (!IsTrusted(u)) throw new InvalidOperationException("Adresse refusée : " + u.Host);
            var req = (HttpWebRequest)WebRequest.Create(u);
            req.Timeout = 15000; req.ReadWriteTimeout = 15000;
            req.UserAgent = "OptiTech-Updater";
            req.Accept = "application/vnd.github+json, text/plain";
            using (ct.Register(() => { try { req.Abort(); } catch { } }))
            using (var r = (HttpWebResponse)req.GetResponse())
            {
                // Vérifié après les redirections : c'est l'adresse finale qui compte.
                if (!IsTrusted(r.ResponseUri)) throw new InvalidDataException("Réponse venue d'un serveur inattendu : " + r.ResponseUri.Host);
                using (var s = r.GetResponseStream())
                using (var m = new MemoryStream())
                {
                    var buf = new byte[1 << 14];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        m.Write(buf, 0, n);
                        if (m.Length > maxBytes) throw new InvalidDataException("Réponse trop volumineuse.");
                    }
                    return Encoding.UTF8.GetString(m.ToArray());
                }
            }
        }

        /// <summary>Dernière release publiée, ou null si elle n'apporte rien de plus récent.</summary>
        public static UpdateInfo Check(CancellationToken ct)
        {
            Store.SetSetting("update.lastCheck", DateTime.UtcNow.ToString("o"));
            var info = ParseRelease(Get(Api, 4 << 20, ct));
            return info != null && IsNewer(info.Version, Current) ? info : null;
        }

        /// <summary>Recherche automatique : activée par défaut, désactivable depuis la page Outils.</summary>
        public static bool Auto
        {
            get { return Store.Setting("update.auto") != "0"; }
            set { Store.SetSetting("update.auto", value ? null : "0"); }
        }

        /// <summary>Au plus une recherche automatique par jour.</summary>
        public static bool Due()
        {
            DateTime d;
            var last = Store.Setting("update.lastCheck");
            return last == null || !DateTime.TryParse(last, null, System.Globalization.DateTimeStyles.RoundtripKind, out d) || (DateTime.UtcNow - d.ToUniversalTime()).TotalHours >= 20;
        }

        /// <summary>Télécharge le nouvel exécutable et ne le rend que si son empreinte est celle de la release.</summary>
        public static string Download(UpdateInfo u, Job job)
        {
            string expected = u.Sha256;
            if (u.SumsUrl != null)
            {
                job.Status("Lecture de l'empreinte publiée");
                var fromSums = ParseSums(Get(u.SumsUrl, 1 << 16, job.Token), ExeName);
                if (fromSums != null)
                {
                    if (expected != null && expected != fromSums) throw new InvalidDataException("Les deux empreintes publiées pour cette version ne concordent pas : mise à jour refusée.");
                    expected = fromSums;
                }
            }
            if (expected == null) throw new InvalidDataException("Cette version n'a pas d'empreinte SHA-256 publiée : mise à jour refusée.");
            if (u.Size > MaxExeBytes) throw new InvalidDataException("Fichier anormalement volumineux : mise à jour refusée.");

            var dest = Path.Combine(Path.GetTempPath(), "OptiTech-" + Text(u.Version) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
            job.Status("Téléchargement d'OptiTech " + Text(u.Version));
            job.Progress(0);
            WinDownload.Download(u.ExeUrl, dest, u.Size, "", job, IsTrusted);
            try
            {
                job.Status("Contrôle de l'empreinte");
                Verify(dest, expected);
            }
            catch { try { File.Delete(dest); } catch { } throw; }
            job.Log("Empreinte SHA-256 conforme à celle de la release.");
            return dest;
        }

        /// <summary>Contrôle qu'un fichier est bien un exécutable Windows et porte l'empreinte attendue.</summary>
        public static void Verify(string path, string expectedSha256)
        {
            var actual = Hashing.Sha256(path, null).ToLowerInvariant();
            if (!string.Equals(actual, CleanHash(expectedSha256), StringComparison.Ordinal))
                throw new InvalidDataException("L'empreinte du fichier téléchargé ne correspond pas à celle de la release : mise à jour refusée.");
            using (var f = File.OpenRead(path))
                if (f.Length < 1024 || f.ReadByte() != 'M' || f.ReadByte() != 'Z')
                    throw new InvalidDataException("Le fichier téléchargé n'est pas un exécutable Windows.");
        }

        /// <summary>
        /// Échange les exécutables. Windows interdit d'écraser un programme en cours, mais permet de le renommer :
        /// l'ancien devient « .old », supprimé au lancement suivant. En cas d'échec, il reprend sa place.
        /// </summary>
        public static void Swap(string current, string fresh)
        {
            var old = current + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(current, old);
            try { File.Move(fresh, current); }
            catch
            {
                try { File.Move(old, current); } catch { }
                throw;
            }
        }

        public static void InstallAndRestart(string fresh)
        {
            var current = Paths.Exe;
            Swap(current, fresh);
            Store.AddHistory("update", "Mise à jour d'OptiTech", "Depuis la version " + Text(Current));
            Process.Start(new ProcessStartInfo(current, (Os.IsAdmin ? "--elevated" : "--limited") + " --after-update") { UseShellExecute = false });
        }

        /// <summary>Supprime l'exécutable de la version précédente, laissé par la dernière mise à jour.</summary>
        public static void CleanupOld()
        {
            try { var old = Paths.Exe + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
        }
    }
}
