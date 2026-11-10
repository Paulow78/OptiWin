using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OptiWin
{
    public sealed class UsbDisk
    {
        public int Index;
        public string Model = "Disque USB", Pnp = "", Bus = "";
        public long Size;
        public int SectorSize = 512;
        public bool IsExternalDrive;
        public List<string> Letters = new List<string>();
        public string Device { get { return @"\\.\PhysicalDrive" + Index; } }
        public override string ToString()
        {
            var l = Letters.Count > 0 ? " (" + string.Join(", ", Letters) + ")" : "";
            return Model + l + " — " + Fmt.Size(Size) + (IsExternalDrive ? " — disque externe" : "");
        }
    }

    /// <summary>
    /// Inventaire des disques amovibles. Liste blanche : un disque n'est proposé que s'il est
    /// prouvé USB/SD et qu'il ne porte ni le système ni une autre installation de Windows.
    /// </summary>
    public static class Disks
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, out StorageDeviceNumber outBuf, int outSize, out int ret, IntPtr ov);
        [StructLayout(LayoutKind.Sequential)] struct StorageDeviceNumber { public int DeviceType, DeviceNumber, PartitionNumber; }
        const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080;

        /// <summary>Numéro du disque physique qui porte ce lecteur, ou -1.</summary>
        public static int DiskNumberOf(char letter)
        {
            using (var h = CreateFile(@"\\.\" + letter + ":", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return -1;
                StorageDeviceNumber n; int r;
                return DeviceIoControl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0, out n, Marshal.SizeOf(typeof(StorageDeviceNumber)), out r, IntPtr.Zero) ? n.DeviceNumber : -1;
            }
        }

        static HashSet<int> ProtectedDisks()
        {
            var set = new HashSet<int>();
            foreach (var path in new[] { Environment.GetEnvironmentVariable("SystemDrive"), Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Paths.Exe })
            {
                if (string.IsNullOrEmpty(path)) continue;
                var n = DiskNumberOf(path[0]);
                if (n >= 0) set.Add(n);
            }
            return set;
        }

        static long N(ManagementBaseObject o, string p) { try { var v = o[p]; return v == null ? 0 : Convert.ToInt64(v); } catch { return 0; } }
        static string S(ManagementBaseObject o, string p) { try { var v = o[p]; return v == null ? "" : Convert.ToString(v).Trim(); } catch { return ""; } }

        public static List<UsbDisk> List(bool includeExternal)
        {
            var res = new List<UsbDisk>();
            var prot = ProtectedDisks();
            // Pas de disque système identifié : on préfère ne rien proposer que risquer une erreur.
            if (prot.Count == 0) { Logger.Error("Disque système non identifié : liste USB vide par sécurité."); return res; }

            var msft = new Dictionary<int, ManagementBaseObject>();
            foreach (var d in SysInfo.Wmi(@"root\Microsoft\Windows\Storage", "SELECT Number,BusType,IsBoot,IsSystem FROM MSFT_Disk"))
                msft[(int)N(d, "Number")] = d;

            foreach (var d in SysInfo.Wmi(@"root\cimv2", "SELECT Index,Model,Size,InterfaceType,MediaType,PNPDeviceID,BytesPerSector,DeviceID FROM Win32_DiskDrive"))
            {
                var idx = (int)N(d, "Index");
                var size = N(d, "Size");
                var iface = S(d, "InterfaceType");
                var media = S(d, "MediaType");
                var removable = media.IndexOf("Removable", StringComparison.OrdinalIgnoreCase) >= 0;
                long bus = -1;
                ManagementBaseObject m;
                if (msft.TryGetValue(idx, out m))
                {
                    bus = N(m, "BusType");
                    try { if ((bool)m["IsBoot"] || (bool)m["IsSystem"]) continue; } catch { }
                }
                // 7 = USB, 12 = SD, 13 = MMC. Un disque SATA « amovible » (échange à chaud) est exclu.
                var isUsbBus = bus == 7 || bus == 12 || bus == 13 || iface.Equals("USB", StringComparison.OrdinalIgnoreCase);
                var ok = isUsbBus || (bus == -1 && removable);
                if (!ok || prot.Contains(idx) || size <= 0) continue;

                var u = new UsbDisk
                {
                    Index = idx, Size = size, Model = S(d, "Model"), Pnp = S(d, "PNPDeviceID"),
                    SectorSize = Math.Max(512, (int)N(d, "BytesPerSector")), Bus = bus == 12 ? "SD" : bus == 13 ? "MMC" : "USB",
                    IsExternalDrive = !removable
                };
                u.Letters = LettersOf(S(d, "DeviceID"));
                if (u.IsExternalDrive && !includeExternal) continue;
                // Une clé Windows To Go ou un disque avec un Windows installé ne se formate pas d'ici.
                if (u.Letters.Any(HasWindows)) continue;
                if (u.Letters.Any(l => l.Length > 0 && prot.Contains(DiskNumberOf(l[0])))) continue;
                res.Add(u);
            }
            return res.OrderBy(x => x.IsExternalDrive).ThenBy(x => x.Index).ToList();
        }

        static bool HasWindows(string letter)
        {
            // ntoskrnl.exe plutôt que config\SYSTEM : ce dossier est illisible sans droits administrateur.
            try { return File.Exists(letter + @"\Windows\System32\ntoskrnl.exe"); } catch { return false; }
        }

        public static List<string> LettersOf(string deviceId)
        {
            var r = new List<string>();
            try
            {
                // Le chemin s'écrit tel quel (\\.\PHYSICALDRIVE2) : doubler les barres le rend introuvable.
                using (var ps = new ManagementObjectSearcher("ASSOCIATORS OF {Win32_DiskDrive.DeviceID='" + deviceId.Replace("'", "") + "'} WHERE AssocClass=Win32_DiskDriveToDiskPartition"))
                    foreach (var p in ps.Get())
                        using (var ls = new ManagementObjectSearcher("ASSOCIATORS OF {Win32_DiskPartition.DeviceID='" + p["DeviceID"] + "'} WHERE AssocClass=Win32_LogicalDiskToPartition"))
                            foreach (var l in ls.Get()) r.Add(Convert.ToString(l["DeviceID"]));
            }
            catch (Exception e) { Logger.Info("Lecteurs de " + deviceId + " : " + e.Message); }
            return r;
        }

        /// <summary>
        /// À appeler juste avant d'effacer : les numéros de disques changent quand on branche ou
        /// débranche un périphérique. Lève une exception si ce n'est plus exactement le même disque.
        /// </summary>
        public static void Revalidate(UsbDisk d)
        {
            var now = List(true).FirstOrDefault(x => x.Index == d.Index);
            if (now == null || now.Size != d.Size || !string.Equals(now.Pnp, d.Pnp, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Le périphérique a changé depuis sa sélection. Rien n'a été effacé. Actualisez la liste et recommencez.");
        }

        public static string FreeLetter()
        {
            var used = new HashSet<char>(DriveInfo.GetDrives().Select(x => char.ToUpperInvariant(x.Name[0])));
            for (var c = 'Z'; c >= 'H'; c--) if (!used.Contains(c)) return c.ToString();
            throw new InvalidOperationException("Aucune lettre de lecteur libre.");
        }

        public static string DiskPartScript(int index, bool gpt, long diskSize, string letter, string label)
        {
            // Windows ne formate pas de volume FAT32 de plus de 32 Go : on limite la partition.
            var size = diskSize > 32000L * 1000 * 1000 ? " size=32000" : "";
            var active = gpt ? "" : "active\r\n";
            return "select disk " + index + "\r\nclean\r\nconvert " + (gpt ? "gpt" : "mbr") + "\r\n" +
                   "create partition primary" + size + "\r\nselect partition 1\r\n" + active +
                   "format fs=fat32 quick label=\"" + label + "\"\r\nassign letter=" + letter + "\r\nexit\r\n";
        }

        public static string WipeScript(int index) { return "select disk " + index + "\r\nclean\r\nexit\r\n"; }

        public static void RunDiskPart(string script, Job job)
        {
            var path = Path.Combine(Path.GetTempPath(), "optiwin-dp-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, script, System.Text.Encoding.ASCII);
            try
            {
                Exception last = null;
                // Windows ou un antivirus retient parfois le volume un instant après l'insertion.
                for (int i = 0; i < 3; i++)
                {
                    try { Proc.RunChecked("diskpart.exe", "/s \"" + path + "\"", job.Log, job.Token); return; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) { last = e; job.Log("Nouvel essai de préparation du disque..."); System.Threading.Thread.Sleep(2500); }
                }
                throw new InvalidOperationException("Préparation du disque impossible. La clé est peut-être protégée en écriture. " + last.Message);
            }
            finally { try { File.Delete(path); } catch { } }
        }
    }
}
