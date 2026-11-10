using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace OptiWin
{
    public enum IsoKind { Unknown, Windows, Hybrid }

    public sealed class IsoInfo
    {
        public string Path, Label = "";
        public long Size;
        public bool HasUdf, HasElTorito, HasIso9660, HasMbrCode;
        public IsoKind Kind;
        public string Describe()
        {
            var b = new StringBuilder();
            b.Append(Kind == IsoKind.Windows ? "Image Windows probable" : Kind == IsoKind.Hybrid ? "Image hybride (Linux ou outil de secours)" : "Type d'image non reconnu");
            if (Label.Length > 0) b.Append(" — « " + Label + " »");
            b.Append(" — " + Fmt.Size(Size));
            return b.ToString();
        }
    }

    /// <summary>Identifie une image en lisant directement ses en-têtes, sans la monter.</summary>
    public static class IsoProbe
    {
        const int Sector = 2048;

        public static IsoInfo Inspect(string path)
        {
            var i = new IsoInfo { Path = path, Size = new FileInfo(path).Length };
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var head = new byte[512];
                f.Read(head, 0, 512);
                i.HasMbrCode = head[510] == 0x55 && head[511] == 0xAA && head.Take(440).Any(b => b != 0);

                var buf = new byte[Sector];
                for (int s = 16; s < 40; s++)
                {
                    f.Position = s * (long)Sector;
                    if (f.Read(buf, 0, Sector) < Sector) break;
                    var id = Encoding.ASCII.GetString(buf, 1, 5);
                    if (id == "CD001")
                    {
                        i.HasIso9660 = true;
                        if (buf[0] == 1 && i.Label.Length == 0) i.Label = Encoding.ASCII.GetString(buf, 40, 32).Trim();
                        if (buf[0] == 0 && Encoding.ASCII.GetString(buf, 7, 23).StartsWith("EL TORITO")) i.HasElTorito = true;
                    }
                    if (id == "NSR02" || id == "NSR03") i.HasUdf = true;
                }
            }
            i.Kind = i.HasMbrCode ? IsoKind.Hybrid : i.HasUdf ? IsoKind.Windows : IsoKind.Unknown;
            return i;
        }
    }

    /// <summary>Monte une image ISO le temps d'une opération, et la démonte ensuite.</summary>
    public sealed class MountedIso : IDisposable
    {
        readonly string iso; readonly bool wasAttached;
        public readonly string Root;

        public MountedIso(string iso, Job job)
        {
            this.iso = iso;
            job.Status("Montage de l'image");
            string err;
            var attached = PS.Run("(Get-DiskImage -ImagePath " + PS.Q(iso) + ").Attached", out err);
            wasAttached = attached.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
            var o = PS.Run((wasAttached
                    ? "$i = Get-DiskImage -ImagePath " + PS.Q(iso) + ";"
                    : "$i = Mount-DiskImage -ImagePath " + PS.Q(iso) + " -StorageType ISO -PassThru -ErrorAction Stop; Start-Sleep -Milliseconds 800;")
                + "($i | Get-Volume).DriveLetter", out err);
            var letter = o.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (string.IsNullOrEmpty(letter) || !char.IsLetter(letter[0]))
                throw new InvalidOperationException("Impossible de monter l'image. " + err.Trim());
            Root = letter.Substring(0, 1) + @":\";
        }

        public void Dispose()
        {
            if (wasAttached) return;
            try { PS.Run("Dismount-DiskImage -ImagePath " + PS.Q(iso) + " | Out-Null"); }
            catch (Exception e) { Logger.Error("Démontage", e); }
        }
    }

    /// <summary>Écriture brute d'une image sur un périphérique, secteur par secteur, avec relecture.</summary>
    public static class RawImage
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, IntPtr buf, int n, out int done, IntPtr ov);
        [DllImport("kernel32", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int n, out int done, IntPtr ov);
        [DllImport("kernel32", SetLastError = true)] static extern bool SetFilePointerEx(SafeFileHandle h, long dist, out long pos, uint method);
        [DllImport("kernel32", SetLastError = true)] static extern bool FlushFileBuffers(SafeFileHandle h);
        [DllImport("kernel32", SetLastError = true)] static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint protect);
        [DllImport("kernel32", SetLastError = true)] static extern bool VirtualFree(IntPtr addr, UIntPtr size, uint type);
        [DllImport("kernel32", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr i, int isz, IntPtr o, int osz, out int ret, IntPtr ov);

        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint NO_BUFFERING = 0x20000000, WRITE_THROUGH = 0x80000000;
        const int Chunk = 1 << 20;
        const int Align = 4096;

        /// <summary>Écrit l'image, puis relit le périphérique pour comparer les empreintes si verify est vrai.</summary>
        public static void Write(string src, string device, Job job, bool verify)
        {
            var len = new FileInfo(src).Length;
            var mem = VirtualAlloc(IntPtr.Zero, (UIntPtr)(uint)Chunk, 0x3000, 4); // aligné sur une page, requis sans tampon
            if (mem == IntPtr.Zero) throw new OutOfMemoryException();
            var managed = new byte[Chunk];
            try
            {
                using (var h = CreateFile(device, GENERIC_READ | GENERIC_WRITE, 3, IntPtr.Zero, 3, NO_BUFFERING | WRITE_THROUGH, IntPtr.Zero))
                {
                    if (h.IsInvalid) throw new IOException("Accès au périphérique refusé (erreur " + Marshal.GetLastWin32Error() + ").");
                    byte[] expected;
                    var started = DateTime.UtcNow;
                    using (var sha = SHA256.Create())
                    using (var f = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
                    {
                        long written = 0;
                        job.Status("Écriture de l'image");
                        while (written < len)
                        {
                            job.Token.ThrowIfCancellationRequested();
                            int n = 0, r;
                            while (n < Chunk && (r = f.Read(managed, n, Chunk - n)) > 0) n += r;
                            if (n == 0) break;
                            sha.TransformBlock(managed, 0, n, null, 0);
                            var rounded = (n + Align - 1) / Align * Align;
                            if (rounded > n) Array.Clear(managed, n, rounded - n);
                            Marshal.Copy(managed, 0, mem, rounded);
                            int done;
                            if (!WriteFile(h, mem, rounded, out done, IntPtr.Zero) || done != rounded)
                                throw new IOException("Échec d'écriture à " + Fmt.Size(written) + " (erreur " + Marshal.GetLastWin32Error() + "). La clé est peut-être défectueuse ou protégée.");
                            written += n;
                            Report(job, written, len, started, verify ? 0.7 : 1.0, 0);
                        }
                        sha.TransformFinalBlock(new byte[0], 0, 0);
                        expected = sha.Hash;
                    }
                    FlushFileBuffers(h);
                    if (!verify) return;

                    job.Status("Relecture et vérification");
                    long pos;
                    if (!SetFilePointerEx(h, 0, out pos, 0)) throw new IOException("Relecture impossible.");
                    started = DateTime.UtcNow;
                    using (var sha = SHA256.Create())
                    {
                        long read = 0;
                        while (read < len)
                        {
                            job.Token.ThrowIfCancellationRequested();
                            var want = (int)Math.Min(Chunk, (len - read + Align - 1) / Align * Align);
                            int done;
                            if (!ReadFile(h, mem, want, out done, IntPtr.Zero) || done == 0)
                                throw new IOException("Échec de relecture à " + Fmt.Size(read) + " (erreur " + Marshal.GetLastWin32Error() + ").");
                            Marshal.Copy(mem, managed, 0, done);
                            var useful = (int)Math.Min(done, len - read);
                            sha.TransformBlock(managed, 0, useful, null, 0);
                            read += useful;
                            Report(job, read, len, started, 0.3, 0.7);
                        }
                        sha.TransformFinalBlock(new byte[0], 0, 0);
                        if (!sha.Hash.SequenceEqual(expected))
                            throw new IOException("La vérification a échoué : ce qui a été relu diffère de l'image. La clé USB n'est pas fiable.");
                    }
                    job.Log("Vérification réussie : les données relues sont identiques à l'image.");
                }
            }
            finally { VirtualFree(mem, UIntPtr.Zero, 0x8000); }
        }

        static void Report(Job j, long done, long total, DateTime start, double weight, double offset)
        {
            j.Progress((offset + weight * done / Math.Max(1, total)) * 100);
            var sec = (DateTime.UtcNow - start).TotalSeconds;
            if (sec > 0.5)
            {
                var speed = done / sec;
                j.Status2(Fmt.Size((long)speed) + "/s — reste " + Fmt.Span(TimeSpan.FromSeconds((total - done) / Math.Max(1, speed))));
            }
        }

        /// <summary>Demande à Windows de relire la table de partitions après l'écriture.</summary>
        public static void RefreshDisk(string device)
        {
            using (var h = CreateFile(device, GENERIC_READ | GENERIC_WRITE, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return;
                int r;
                DeviceIoControl(h, 0x70140, IntPtr.Zero, 0, IntPtr.Zero, 0, out r, IntPtr.Zero);
            }
        }

        public static void FlushVolume(string letter)
        {
            using (var h = CreateFile(@"\\.\" + letter.TrimEnd(':', '\\') + ":", GENERIC_READ | GENERIC_WRITE, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                if (!h.IsInvalid) FlushFileBuffers(h);
        }
    }

    /// <summary>
    /// Crée une image ISO avec IMAPI2, le moteur de gravure intégré à Windows : ni oscdimg ni ADK.
    /// Si le dossier contient les fichiers de démarrage de Windows, l'ISO démarre en BIOS et en UEFI.
    /// </summary>
    public static class IsoBuilder
    {
        [DllImport("shlwapi", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateStreamOnFileEx(string file, uint mode, uint attrs, bool create, IStream template, out IStream stream);

        static readonly string[] SkipAtRoot = { "System Volume Information", "$RECYCLE.BIN", "RECYCLER" };

        public static void Create(string srcDir, string isoPath, string label, bool noPrompt, Job job)
        {
            Exception err = null;
            // Les objets COM d'IMAPI2 sont le plus sûrs dans un thread STA dédié.
            var t = new Thread(delegate()
            {
                try { CreateCore(srcDir, isoPath, label, noPrompt, job); }
                catch (Exception e) { err = e; }
                // IMAPI garde un flux ouvert sur chaque fichier ajouté tant que ses objets COM vivent :
                // on les libère ici, sinon le dossier source reste verrouillé et impossible à supprimer.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join();
            if (err != null)
            {
                try { if (File.Exists(isoPath)) File.Delete(isoPath); } catch { }
                if (err is OperationCanceledException) throw new OperationCanceledException();
                throw new InvalidOperationException(err.GetBaseException().Message, err);
            }
        }

        static IStream OpenStream(string path)
        {
            IStream s;
            SHCreateStreamOnFileEx(path, 0x20 /* lecture, partage en écriture interdit */, 0, false, null, out s);
            return s;
        }

        static dynamic BootOption(string path, int platform)
        {
            dynamic b = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2FS.BootOptions"));
            b.AssignBootImage(new UnknownWrapper(OpenStream(path)));
            b.PlatformId = platform;
            b.Emulation = 0; // sans émulation
            b.Manufacturer = "OptiWin";
            return b;
        }

        static void CreateCore(string srcDir, string isoPath, string label, bool noPrompt, Job job)
        {
            job.Status("Analyse du dossier source");
            job.Progress(-1);
            var src = srcDir.TrimEnd('\\') + "\\";
            dynamic fs = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage"));
            fs.FileSystemsToCreate = 4; // UDF seul, seul format accepté pour les fichiers de plus de 4 Go
            fs.UDFRevision = 0x102;
            fs.FreeMediaBlocks = 0;     // pas de limite de taille
            fs.VolumeName = SafeLabel(label);

            var bios = Path.Combine(src, @"boot\etfsboot.com");
            var uefi = Path.Combine(src, noPrompt ? @"efi\microsoft\boot\efisys_noprompt.bin" : @"efi\microsoft\boot\efisys.bin");
            if (!File.Exists(uefi)) uefi = Path.Combine(src, @"efi\microsoft\boot\efisys.bin");
            var opts = new List<object>();
            if (File.Exists(bios)) opts.Add(BootOption(bios, 0));
            if (File.Exists(uefi)) opts.Add(BootOption(uefi, 0xEF));

            if (opts.Count > 0)
            {
                job.Log("Fichiers de démarrage trouvés : " + (File.Exists(bios) ? "BIOS " : "") + (File.Exists(uefi) ? "UEFI" : ""));
                SetBootOptions(fs, opts, job);
            }
            else job.Log("Aucun fichier de démarrage Windows : l'ISO sera une simple image de données.");

            job.Status("Préparation de l'arborescence");
            dynamic root = fs.Root;
            foreach (var d in Directory.GetDirectories(src))
            {
                job.Token.ThrowIfCancellationRequested();
                if (SkipAtRoot.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)) continue;
                root.AddTree(d, true);
            }
            foreach (var f in Directory.GetFiles(src))
            {
                job.Token.ThrowIfCancellationRequested();
                root.AddFile(Path.GetFileName(f), new UnknownWrapper(OpenStream(f)));
            }

            job.Status("Génération de l'image");
            dynamic result = fs.CreateResultImage();
            var image = (IStream)result.ImageStream;
            long total = (long)result.TotalBlocks * (long)result.BlockSize;
            WriteStream(image, isoPath, total, job);
            job.Log("ISO créée : " + isoPath + " (" + Fmt.Size(new FileInfo(isoPath).Length) + ")");
        }

        static void SetBootOptions(dynamic fs, List<object> opts, Job job)
        {
            if (opts.Count == 1) { fs.BootImageOptions = opts[0]; return; }
            try
            {
                fs.BootImageOptionsArray = opts.ToArray();
            }
            catch (Exception e)
            {
                // Windows 7 ne connaît pas le démarrage multiple : on garde au moins le BIOS.
                job.Log("Démarrage double indisponible (" + e.GetBaseException().Message + "), BIOS seul.");
                fs.BootImageOptions = opts[0];
            }
        }

        static string SafeLabel(string s)
        {
            s = new string((s ?? "").Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());
            if (s.Length == 0) s = "OPTIWIN";
            return s.Length > 32 ? s.Substring(0, 32) : s;
        }

        static void WriteStream(IStream image, string path, long total, Job job)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var buf = new byte[1 << 20];
            var read = Marshal.AllocHGlobal(4);
            try
            {
                using (var o = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    long done = 0;
                    var start = DateTime.UtcNow;
                    while (true)
                    {
                        job.Token.ThrowIfCancellationRequested();
                        image.Read(buf, buf.Length, read);
                        var n = Marshal.ReadInt32(read);
                        if (n <= 0) break;
                        o.Write(buf, 0, n);
                        done += n;
                        if (total > 0) job.Progress(100.0 * done / total);
                        var sec = (DateTime.UtcNow - start).TotalSeconds;
                        if (sec > 0.5) job.Status2(Fmt.Size((long)(done / sec)) + "/s — " + Fmt.Size(done) + " sur " + Fmt.Size(total));
                    }
                }
            }
            finally { Marshal.FreeHGlobal(read); }
        }
    }

    public static class Hashing
    {
        public static string Sha256(string path, Job job)
        {
            using (var sha = SHA256.Create())
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            {
                var buf = new byte[1 << 20];
                long done = 0; int n;
                while ((n = f.Read(buf, 0, buf.Length)) > 0)
                {
                    if (job != null) job.Token.ThrowIfCancellationRequested();
                    sha.TransformBlock(buf, 0, n, null, 0);
                    done += n;
                    if (job != null) job.Progress(100.0 * done / Math.Max(1, f.Length));
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }
    }
}
