using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace OptiWin
{
    /// <summary>
    /// Clé multiboot avec Ventoy (logiciel libre tiers, GPL), récupéré via winget au premier usage :
    /// il suffit ensuite de copier des ISO sur la clé. Le disque est choisi par notre liste sécurisée,
    /// et Ventoy applique en plus sa propre vérification USB, que nous ne désactivons jamais.
    /// </summary>
    public static class Multiboot
    {
        const string PackageId = "Ventoy.Ventoy";

        public static string FindVentoy()
        {
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Packages"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"WinGet\Packages"),
            };
            foreach (var root in roots.Where(Directory.Exists))
            {
                try
                {
                    foreach (var pkg in Directory.GetDirectories(root, PackageId + "*"))
                    {
                        var hit = Directory.GetFiles(pkg, "Ventoy2Disk.exe", SearchOption.AllDirectories).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                        if (hit != null) return hit;
                    }
                }
                catch (Exception e) { Logger.Info("Recherche de Ventoy : " + e.Message); }
            }
            return null;
        }

        public static string EnsureVentoy(Job job)
        {
            var exe = FindVentoy();
            if (exe != null) return exe;
            if (!AppsCatalog.WingetAvailable()) throw new InvalidOperationException("winget est indisponible : impossible de récupérer Ventoy.");
            job.Status("Récupération de Ventoy via winget");
            var code = AppsCatalog.Install(new SoftApp { Id = PackageId, Label = "Ventoy" }, job);
            exe = FindVentoy();
            if (exe == null) throw new InvalidOperationException("Ventoy n'a pas pu être installé (winget, code " + code + ").");
            Store.AddHistory("soft:" + PackageId, "Logiciel : Ventoy", "Installé");
            return exe;
        }

        public static string InstallArgs(int disk, bool gpt)
        {
            return "VTOYCLI /I /PhyDrive:" + disk + (gpt ? " /GPT" : "");
        }

        /// <summary>Volumes déjà préparés par Ventoy : on peut y ajouter des images sans rien effacer.</summary>
        public static List<string> ExistingKeys()
        {
            var res = new List<string>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.IsReady && (d.DriveType == DriveType.Removable || d.DriveType == DriveType.Fixed) && d.VolumeLabel.Equals("Ventoy", StringComparison.OrdinalIgnoreCase))
                        res.Add(d.Name);
                }
                catch { }
            }
            return res;
        }

        /// <summary>Installe Ventoy sur la clé. Toutes les données de la clé sont effacées.</summary>
        public static string Install(UsbDisk disk, bool gpt, string ventoyExe, Job job)
        {
            if (!Os.IsAdmin) throw new InvalidOperationException("Droits administrateur requis.");
            job.Status("Vérification du périphérique");
            Disks.Revalidate(disk);

            var dir = Path.GetDirectoryName(ventoyExe);
            var done = Path.Combine(dir, "cli_done.txt");
            var percent = Path.Combine(dir, "cli_percent.txt");
            foreach (var f in new[] { done, percent }) try { File.Delete(f); } catch { }

            job.Status("Installation de Ventoy sur la clé");
            job.Progress(0);
            using (var p = Process.Start(new ProcessStartInfo(ventoyExe, InstallArgs(disk.Index, gpt)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir }))
            {
                // Pas d'annulation ici : interrompre l'écriture des partitions rendrait la clé inutilisable.
                var limit = DateTime.UtcNow.AddMinutes(15);
                while (!File.Exists(done))
                {
                    if (DateTime.UtcNow > limit) throw new TimeoutException("Ventoy ne répond plus. Voir " + Path.Combine(dir, "cli_log.txt"));
                    if (p.HasExited)
                    {
                        Thread.Sleep(1500);
                        if (!File.Exists(done)) throw new InvalidOperationException("Ventoy s'est arrêté sans résultat (code " + p.ExitCode + "). Voir " + Path.Combine(dir, "cli_log.txt"));
                        break;
                    }
                    double v;
                    try { if (double.TryParse(File.ReadAllText(percent).Trim(), out v)) job.Progress(Math.Min(100, v)); } catch { }
                    Thread.Sleep(500);
                }
            }
            Thread.Sleep(300);
            if (File.ReadAllText(done).Trim() != "0")
                throw new InvalidOperationException("Ventoy a signalé un échec. Voir " + Path.Combine(dir, "cli_log.txt"));
            job.Log("Ventoy installé sur la clé.");

            // La lettre du volume apparaît quelques secondes après le partitionnement.
            for (int i = 0; i < 40; i++)
            {
                var keys = ExistingKeys().Where(k => Disks.DiskNumberOf(k[0]) == disk.Index).ToList();
                if (keys.Count > 0) return keys[0];
                Thread.Sleep(500);
            }
            throw new IOException("Le volume Ventoy n'apparaît pas. Débranchez et rebranchez la clé, puis copiez-y vos images.");
        }

        /// <summary>Copie les images, en vérifiant la place d'abord et la taille de chaque copie ensuite.</summary>
        public static void CopyImages(string root, IList<string> images, Job job, long? freeBytes = null)
        {
            var total = images.Sum(i => new FileInfo(i).Length);
            var free = freeBytes ?? new DriveInfo(Path.GetPathRoot(root)).AvailableFreeSpace;
            if (total > free) throw new IOException("Espace insuffisant sur la clé : " + Fmt.Size(total) + " nécessaires, " + Fmt.Size(free) + " libres.");
            long done = 0;
            var start = DateTime.UtcNow;
            var buf = new byte[4 << 20];
            foreach (var src in images)
            {
                job.Token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(src);
                job.Status("Copie de " + name);
                var dst = Path.Combine(root, name);
                try
                {
                    using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
                    using (var o = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 12, FileOptions.WriteThrough))
                    {
                        int n;
                        while ((n = i.Read(buf, 0, buf.Length)) > 0)
                        {
                            job.Token.ThrowIfCancellationRequested();
                            o.Write(buf, 0, n);
                            done += n;
                            job.Progress(100.0 * done / Math.Max(1, total));
                            var sec = (DateTime.UtcNow - start).TotalSeconds;
                            if (sec > 0.5) job.Status2(Fmt.Size((long)(done / sec)) + "/s");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Une image tronquée apparaîtrait dans le menu de Ventoy et échouerait au démarrage.
                    try { File.Delete(dst); } catch { }
                    throw;
                }
                if (new FileInfo(dst).Length != new FileInfo(src).Length)
                    throw new IOException(name + " est incomplète sur la clé. Essayez un autre port USB.");
            }
        }
    }

    /// <summary>Kit de réparation à lancer depuis l'installateur de Windows, avec Maj+F10.</summary>
    public static class RescueKit
    {
        static string Resource(string name)
        {
            using (var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("OptiWin.assets." + name))
            {
                if (s == null) throw new FileNotFoundException("Ressource absente : " + name);
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        public static string ScriptText { get { return Crlf(Resource("secours.cmd")); } }
        public static string ReadmeText { get { return Crlf(Resource("LISEZMOI-SECOURS.txt")); } }
        static string Crlf(string s) { return s.Replace("\r\n", "\n").Replace("\n", "\r\n"); }

        /// <summary>Écrit le kit dans un dossier. cmd.exe exige des fins de ligne CRLF et du texte sans accent.</summary>
        public static void WriteTo(string folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "secours.cmd"), ScriptText, Encoding.ASCII);
            File.WriteAllText(Path.Combine(folder, "LISEZMOI.txt"), ReadmeText, new UTF8Encoding(true));
        }
    }
}
