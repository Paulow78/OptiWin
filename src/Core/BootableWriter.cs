using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OptiWin
{
    public enum FlashMode { WindowsMbr, WindowsGpt, Raw }

    public sealed class FlashOptions
    {
        public string Image;
        public UsbDisk Disk;
        public FlashMode Mode;
        public bool Verify = true;
        public bool CopyOptiWin;
        public UnattendOptions Unattend = new UnattendOptions();
    }

    /// <summary>
    /// Création de clé bootable. Le mode Windows utilise FAT32, seul format que tous les UEFI
    /// lisent nativement, y compris avec Secure Boot actif. Un install.wim de plus de 4 Go
    /// est découpé avec DISM, comme le fait le Media Creation Tool de Microsoft.
    /// </summary>
    public static class BootableWriter
    {
        const long Fat32MaxFile = 0xFFFFFFFFL;
        const long Fat32MaxPartition = 32000L * 1000 * 1000;
        const long SplitChunkMb = 3800;

        public static void Run(FlashOptions o, Job job)
        {
            if (!Os.IsAdmin) throw new InvalidOperationException("Droits administrateur requis.");
            if (o.Mode == FlashMode.Raw) FlashRaw(o, job);
            else FlashWindows(o, job);
        }

        static void FlashRaw(FlashOptions o, Job job)
        {
            var len = new FileInfo(o.Image).Length;
            if (len > o.Disk.Size) throw new InvalidOperationException("L'image (" + Fmt.Size(len) + ") dépasse la capacité de la clé (" + Fmt.Size(o.Disk.Size) + ").");
            job.Status("Vérification du périphérique");
            Disks.Revalidate(o.Disk);
            job.Status("Effacement de la table de partitions");
            Disks.RunDiskPart(Disks.WipeScript(o.Disk.Index), job);
            RawImage.Write(o.Image, o.Disk.Device, job, o.Verify);
            RawImage.RefreshDisk(o.Disk.Device);
            job.Summary = "Clé prête. Démarrez dessus depuis le menu de démarrage du PC.";
        }

        static void FlashWindows(FlashOptions o, Job job)
        {
            using (var iso = new MountedIso(o.Image, job))
            {
                var src = iso.Root;
                var wim = Path.Combine(src, @"sources\install.wim");
                var esd = Path.Combine(src, @"sources\install.esd");
                var swm = Path.Combine(src, @"sources\install.swm");
                if (!File.Exists(Path.Combine(src, @"sources\boot.wim")) || !(File.Exists(wim) || File.Exists(esd) || File.Exists(swm)))
                    throw new InvalidOperationException("Ce n'est pas une image d'installation de Windows. Pour Linux ou un outil de secours, choisissez le mode « Image brute ».");

                var files = new DirectoryInfo(src).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
                var total = files.Sum(f => f.Length);
                var bigWim = File.Exists(wim) && new FileInfo(wim).Length > Fat32MaxFile;
                var tooBig = files.Where(f => f.Length > Fat32MaxFile && !(bigWim && f.FullName.Equals(wim, StringComparison.OrdinalIgnoreCase))).ToList();
                if (tooBig.Count > 0)
                    throw new InvalidOperationException(tooBig[0].Name + " dépasse 4 Go et ne peut pas être découpé. Créez d'abord une version plus légère, avec une seule édition, dans l'onglet « ISO personnalisée ». Rien n'a été effacé.");

                var capacity = Math.Min(o.Disk.Size, Fat32MaxPartition);
                var needed = total + (total / 20) + (o.CopyOptiWin ? 8L << 20 : 0);
                if (needed > capacity) throw new InvalidOperationException("Espace insuffisant : " + Fmt.Size(needed) + " nécessaires, " + Fmt.Size(capacity) + " utilisables sur la clé.");

                job.Status("Vérification du périphérique");
                Disks.Revalidate(o.Disk);

                var letter = Disks.FreeLetter();
                job.Status("Partitionnement et formatage (" + (o.Mode == FlashMode.WindowsGpt ? "GPT" : "MBR") + ", FAT32)");
                Disks.RunDiskPart(Disks.DiskPartScript(o.Disk.Index, o.Mode == FlashMode.WindowsGpt, o.Disk.Size, letter, "OPTIWINUSB"), job);
                var dst = letter + @":\";
                WaitReady(dst, job);

                // Progression : la copie compte pour l'essentiel, le découpage y est inclus.
                long done = 0;
                var start = DateTime.UtcNow;
                job.Status("Copie des fichiers de Windows");
                foreach (var f in files)
                {
                    job.Token.ThrowIfCancellationRequested();
                    if (bigWim && f.FullName.Equals(wim, StringComparison.OrdinalIgnoreCase)) continue;
                    var rel = f.FullName.Substring(src.Length);
                    var target = Path.Combine(dst, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    CopyFile(f.FullName, target, job, delegate(long n)
                    {
                        done += n;
                        job.Progress(100.0 * done / Math.Max(1, total) * 0.97);
                        var sec = (DateTime.UtcNow - start).TotalSeconds;
                        if (sec > 0.5)
                        {
                            var speed = done / sec;
                            job.Status2(Fmt.Size((long)speed) + "/s — reste " + Fmt.Span(TimeSpan.FromSeconds((total - done) / Math.Max(1, speed))));
                        }
                    });
                }

                if (bigWim)
                {
                    job.Status("Découpage de install.wim (fichier de plus de 4 Go)");
                    var wimLen = new FileInfo(wim).Length;
                    var baseDone = done;
                    var pct = new Regex(@"(\d{1,3}(?:[.,]\d+)?)\s*%");
                    var code = Proc.Run("DISM.exe", "/Split-Image /ImageFile:\"" + wim + "\" /SWMFile:\"" + Path.Combine(dst, @"sources\install.swm") + "\" /FileSize:" + SplitChunkMb,
                        delegate(string l)
                        {
                            var m = pct.Match(l);
                            if (m.Success)
                            {
                                double v;
                                if (double.TryParse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
                                    job.Progress(100.0 * (baseDone + wimLen * v / 100) / total * 0.97);
                            }
                            else job.Log(l.Trim());
                        }, job.Token);
                    if (code != 0) throw new InvalidOperationException("Le découpage de install.wim a échoué (code " + code + "). Voir le journal.");
                }

                var arch = Unattend.ArchOf(dst);
                if (!o.Unattend.IsEmpty)
                {
                    File.WriteAllText(Path.Combine(dst, "autounattend.xml"), Unattend.Build(o.Unattend, arch), new UTF8Encoding(false));
                    job.Log("autounattend.xml ajouté (" + arch + "). L'installateur demandera toujours sur quel disque installer.");
                }
                if (o.CopyOptiWin)
                {
                    Directory.CreateDirectory(Path.Combine(dst, "OptiWin"));
                    File.Copy(Paths.Exe, Path.Combine(dst, @"OptiWin\OptiWin.exe"), true);
                    RescueKit.WriteTo(Path.Combine(dst, @"OptiWin\Secours"));
                    job.Log("OptiWin et le kit de secours copiés sur la clé (mode d'emploi : OptiWin\\Secours\\LISEZMOI.txt).");
                }

                if (o.Mode == FlashMode.WindowsMbr) WriteBiosBootSector(src, dst, letter, job);

                job.Status("Écriture des derniers tampons sur la clé");
                job.Progress(98);
                RawImage.FlushVolume(letter);

                if (o.Verify) VerifyCopy(src, dst, files, bigWim, wim, job);

                job.Progress(100);
                job.Summary = "Clé " + letter + ": prête" + (o.Mode == FlashMode.WindowsMbr ? " (démarre en UEFI et en BIOS)." : " (démarre en UEFI).") + " Retirez-la en passant par « Retirer le périphérique en toute sécurité ».";
            }
        }

        static void WaitReady(string root, Job job)
        {
            for (int i = 0; i < 40; i++)
            {
                job.Token.ThrowIfCancellationRequested();
                try { if (new DriveInfo(root).IsReady) return; } catch { }
                System.Threading.Thread.Sleep(500);
            }
            throw new IOException("Le volume formaté n'apparaît pas : " + root);
        }

        static void CopyFile(string from, string to, Job job, Action<long> progress)
        {
            var buf = new byte[4 << 20];
            using (var i = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
            using (var o = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 12, FileOptions.WriteThrough))
            {
                int n;
                while ((n = i.Read(buf, 0, buf.Length)) > 0)
                {
                    job.Token.ThrowIfCancellationRequested();
                    o.Write(buf, 0, n);
                    progress(n);
                }
            }
        }

        static void WriteBiosBootSector(string src, string dst, string letter, Job job)
        {
            var exe = Path.Combine(src, @"boot\bootsect.exe");
            if (!File.Exists(exe)) { job.Log("bootsect.exe absent de l'image : la clé démarrera seulement en UEFI."); return; }
            job.Status("Écriture du secteur de démarrage BIOS");
            try
            {
                var code = Proc.Run(exe, "/nt60 " + letter + ": /mbr /force", job.Log, job.Token);
                if (code != 0) job.Log("Secteur de démarrage BIOS non écrit (code " + code + ") : la clé démarrera en UEFI.");
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                // Par exemple un bootsect 64 bits lancé depuis un Windows 32 bits.
                job.Log("bootsect.exe ne peut pas s'exécuter ici (" + e.Message + ") : la clé démarrera en UEFI.");
            }
        }

        static void VerifyCopy(string src, string dst, List<FileInfo> files, bool bigWim, string wim, Job job)
        {
            job.Status("Vérification de la copie");
            int missing = 0;
            foreach (var f in files)
            {
                if (bigWim && f.FullName.Equals(wim, StringComparison.OrdinalIgnoreCase)) continue;
                var t = new FileInfo(Path.Combine(dst, f.FullName.Substring(src.Length)));
                if (!t.Exists || t.Length != f.Length) { missing++; job.Log("Différence : " + f.Name); }
            }
            if (missing > 0) throw new IOException(missing + " fichier(s) incomplet(s) sur la clé. Essayez une autre clé ou un autre port USB.");

            // Le contenu des fichiers de démarrage est comparé octet par octet : c'est eux qui décident si la clé démarre.
            foreach (var rel in new[] { "bootmgr", @"efi\boot\bootx64.efi", @"efi\boot\bootaa64.efi", @"sources\boot.wim" })
            {
                var a = Path.Combine(src, rel);
                var b = Path.Combine(dst, rel);
                if (!File.Exists(a)) continue;
                job.Token.ThrowIfCancellationRequested();
                if (!File.Exists(b) || Hashing.Sha256(a, null) != Hashing.Sha256(b, null))
                    throw new IOException("Le fichier " + rel + " est corrompu sur la clé. Cette clé n'est pas fiable.");
            }
            job.Log("Vérification réussie : tailles conformes et fichiers de démarrage identiques.");
        }
    }

    public sealed class CustomIsoOptions
    {
        public string SourceIso, OutIso, WorkDir;
        public int EditionIndex = 1;
        public bool RemoveBloat, InjectDrivers, CopyOptiWin;
        public UnattendOptions Unattend = new UnattendOptions();
    }

    public sealed class WimEdition
    {
        public int Index;
        public string Name;
        public override string ToString() { return Name; }
    }

    /// <summary>
    /// ISO personnalisée : édition unique, applications préinstallées retirées, pilotes de ce PC
    /// intégrés, réponses automatiques. Rien ne touche à l'image d'origine, tout se passe dans une copie.
    /// </summary>
    public static class ImageCustomizer
    {
        public static List<WimEdition> ReadEditions(string iso, Job job)
        {
            using (var m = new MountedIso(iso, job))
            {
                var img = File.Exists(m.Root + @"sources\install.wim") ? m.Root + @"sources\install.wim" : m.Root + @"sources\install.esd";
                if (!File.Exists(img)) throw new InvalidOperationException("Aucune image d'installation trouvée dans cet ISO.");
                var o = new StringBuilder();
                Proc.RunChecked("DISM.exe", "/English /Get-WimInfo /WimFile:\"" + img + "\"", l => o.AppendLine(l), job.Token);
                return ParseEditions(o.ToString());
            }
        }

        /// <summary>Lit la sortie de « DISM /English /Get-WimInfo ».</summary>
        public static List<WimEdition> ParseEditions(string text)
        {
            var res = new List<WimEdition>();
            int idx = 0;
            foreach (var l in text.Split('\n'))
            {
                var line = l.Trim();
                if (line.StartsWith("Index :")) int.TryParse(line.Substring(7).Trim(), out idx);
                else if (line.StartsWith("Name :") && idx > 0) res.Add(new WimEdition { Index = idx, Name = line.Substring(6).Trim() });
            }
            return res;
        }

        public static void Run(CustomIsoOptions o, Job job)
        {
            if (!Os.IsAdmin) throw new InvalidOperationException("Droits administrateur requis.");
            var work = Path.Combine(o.WorkDir, "OptiWin-Image-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
            var isoDir = Path.Combine(work, "iso");
            var mnt = Path.Combine(work, "mnt");
            var drivers = Path.Combine(work, "drivers");
            var isoLen = new FileInfo(o.SourceIso).Length;
            var free = new DriveInfo(Path.GetPathRoot(work)).AvailableFreeSpace;
            if (free < isoLen * 2 + (15L << 30))
                throw new InvalidOperationException("Espace insuffisant dans le dossier de travail : " + Fmt.Size(isoLen * 2 + (15L << 30)) + " nécessaires, " + Fmt.Size(free) + " disponibles.");

            Directory.CreateDirectory(isoDir);
            Directory.CreateDirectory(mnt);
            var mounted = false;
            try
            {
                using (var m = new MountedIso(o.SourceIso, job))
                {
                    job.Status("Copie du contenu de l'image");
                    var files = new DirectoryInfo(m.Root).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
                    long total = files.Sum(f => f.Length), done = 0;
                    foreach (var f in files)
                    {
                        job.Token.ThrowIfCancellationRequested();
                        var to = Path.Combine(isoDir, f.FullName.Substring(m.Root.Length));
                        Directory.CreateDirectory(Path.GetDirectoryName(to));
                        File.Copy(f.FullName, to, true);
                        File.SetAttributes(to, FileAttributes.Normal);
                        done += f.Length;
                        job.Progress(100.0 * done / Math.Max(1, total));
                    }
                }

                var src = File.Exists(Path.Combine(isoDir, @"sources\install.wim")) ? Path.Combine(isoDir, @"sources\install.wim") : Path.Combine(isoDir, @"sources\install.esd");
                var wim = Path.Combine(work, "install.wim");
                job.Status("Extraction de l'édition choisie");
                job.Progress(0);
                Check(Tools.Tracked(job, "DISM.exe", "/Export-Image /SourceImageFile:\"" + src + "\" /SourceIndex:" + o.EditionIndex + " /DestinationImageFile:\"" + wim + "\" /Compress:max"), "extraction");
                File.Delete(src);

                if (o.RemoveBloat || o.InjectDrivers)
                {
                    job.Status("Montage de l'image Windows");
                    job.Progress(0);
                    Check(Tools.Tracked(job, "DISM.exe", "/Mount-Image /ImageFile:\"" + wim + "\" /Index:1 /MountDir:\"" + mnt + "\""), "montage");
                    mounted = true;

                    if (o.RemoveBloat) RemoveProvisioned(mnt, job);
                    if (o.InjectDrivers)
                    {
                        job.Status("Export des pilotes de ce PC");
                        Directory.CreateDirectory(drivers);
                        Check(Tools.Tracked(job, "DISM.exe", "/Online /Export-Driver /Destination:\"" + drivers + "\""), "export des pilotes");
                        job.Status("Intégration des pilotes");
                        job.Progress(-1);
                        var c = Tools.Tracked(job, "DISM.exe", "/Image:\"" + mnt + "\" /Add-Driver /Driver:\"" + drivers + "\" /Recurse");
                        if (c != 0) job.Log("Certains pilotes n'ont pas pu être intégrés (code " + c + "), on continue.");
                    }

                    job.Status("Enregistrement de l'image modifiée");
                    job.Progress(0);
                    Check(Tools.Tracked(job, "DISM.exe", "/Unmount-Image /MountDir:\"" + mnt + "\" /Commit"), "enregistrement");
                    mounted = false;
                }

                job.Status("Compression finale");
                job.Progress(0);
                var final = Path.Combine(isoDir, @"sources\install.wim");
                Check(Tools.Tracked(job, "DISM.exe", "/Export-Image /SourceImageFile:\"" + wim + "\" /SourceIndex:1 /DestinationImageFile:\"" + final + "\" /Compress:max"), "compression");

                if (!o.Unattend.IsEmpty)
                    File.WriteAllText(Path.Combine(isoDir, "autounattend.xml"), Unattend.Build(o.Unattend, Unattend.ArchOf(isoDir)), new UTF8Encoding(false));
                if (o.CopyOptiWin)
                {
                    Directory.CreateDirectory(Path.Combine(isoDir, "OptiWin"));
                    File.Copy(Paths.Exe, Path.Combine(isoDir, @"OptiWin\OptiWin.exe"), true);
                    RescueKit.WriteTo(Path.Combine(isoDir, @"OptiWin\Secours"));
                }

                IsoBuilder.Create(isoDir, o.OutIso, "WINDOWS_OPTIWIN", false, job);
                job.Summary = "ISO personnalisée créée : " + o.OutIso + " (" + Fmt.Size(new FileInfo(o.OutIso).Length) + ").";
            }
            finally
            {
                if (mounted)
                {
                    job.Log("Abandon des modifications de l'image montée...");
                    try { Proc.Run("DISM.exe", "/Unmount-Image /MountDir:\"" + mnt + "\" /Discard", job.Log); } catch (Exception e) { Logger.Error("Démontage WIM", e); }
                    try { Proc.Run("DISM.exe", "/Cleanup-Wim", job.Log); } catch { }
                }
                try { Directory.Delete(work, true); job.Log("Dossier de travail supprimé."); }
                catch (Exception e) { job.Log("Dossier de travail à supprimer à la main : " + work + " (" + e.Message + ")"); }
            }
        }

        static void Check(int code, string step)
        {
            if (code != 0) throw new InvalidOperationException("Étape « " + step + " » en échec (DISM, code " + code + "). Voir le journal.");
        }

        static void RemoveProvisioned(string mnt, Job job)
        {
            job.Status("Retrait des applications préinstallées");
            job.Progress(-1);
            var o = new StringBuilder();
            Proc.RunChecked("DISM.exe", "/English /Image:\"" + mnt + "\" /Get-ProvisionedAppxPackages", l => o.AppendLine(l), job.Token);
            foreach (var l in o.ToString().Split('\n'))
            {
                var line = l.Trim();
                if (!line.StartsWith("PackageName :")) continue;
                var pkg = line.Substring(13).Trim();
                var matched = AppsCatalog.BloatPrefixesForImage.Any(p => pkg.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                if (!matched) continue;
                job.Token.ThrowIfCancellationRequested();
                job.Log("Retrait : " + pkg);
                var c = Proc.Run("DISM.exe", "/Image:\"" + mnt + "\" /Remove-ProvisionedAppxPackage /PackageName:" + pkg, null, job.Token);
                if (c != 0) job.Log("  non retiré (code " + c + ")");
            }
        }
    }
}
