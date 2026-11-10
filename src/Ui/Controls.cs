using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OptiWin
{
    public static class Ui
    {
        public static object Load(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("OptiWin.ui." + name))
            {
                if (s == null) throw new FileNotFoundException("Ressource absente : " + name);
                return XamlReader.Load(s);
            }
        }
    }

    public abstract class Page
    {
        public FrameworkElement View { get; protected set; }
        protected T F<T>(string n) where T : class
        {
            var o = View.FindName(n) as T;
            if (o == null) throw new InvalidOperationException("Élément d'interface manquant : " + n);
            return o;
        }
        public virtual void OnShow() { }
        public virtual void OnHide() { }
    }

    /// <summary>Jauge circulaire du score de santé.</summary>
    public sealed class RingGauge : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register("Value", typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register("Stroke", typeof(Brush), typeof(RingGauge), new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty TrackProperty = DependencyProperty.Register("Track", typeof(Brush), typeof(RingGauge), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register("Thickness", typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public double Value { get { return (double)GetValue(ValueProperty); } set { SetValue(ValueProperty, value); } }
        public Brush Stroke { get { return (Brush)GetValue(StrokeProperty); } set { SetValue(StrokeProperty, value); } }
        public Brush Track { get { return (Brush)GetValue(TrackProperty); } set { SetValue(TrackProperty, value); } }
        public double Thickness { get { return (double)GetValue(ThicknessProperty); } set { SetValue(ThicknessProperty, value); } }

        public void AnimateTo(double v)
        {
            BeginAnimation(ValueProperty, new DoubleAnimation(v, new Duration(TimeSpan.FromMilliseconds(700))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        protected override void OnRender(DrawingContext dc)
        {
            var t = Thickness;
            var size = Math.Min(ActualWidth, ActualHeight);
            if (size <= t) return;
            var r = (size - t) / 2;
            var c = new Point(ActualWidth / 2, ActualHeight / 2);
            dc.DrawEllipse(null, new Pen(Track, t), c, r, r);
            var v = Math.Max(0, Math.Min(100, Value));
            if (v <= 0) return;
            var pen = new Pen(Stroke, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (v >= 99.9) { dc.DrawEllipse(null, pen, c, r, r); return; }
            var a = v / 100 * 2 * Math.PI;
            var start = new Point(c.X, c.Y - r);
            var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
            var g = new StreamGeometry();
            using (var x = g.Open())
            {
                x.BeginFigure(start, false, false);
                x.ArcTo(end, new Size(r, r), 0, v > 50, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
    }

    /// <summary>Courbe en direct, dessinée sans graphe d'objets pour rester très légère.</summary>
    public sealed class Sparkline : FrameworkElement
    {
        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register("Stroke", typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));
        public Brush Stroke { get { return (Brush)GetValue(StrokeProperty); } set { SetValue(StrokeProperty, value); } }
        const int N = 60;
        readonly double[] data = new double[N];
        int count;

        public void Push(double v)
        {
            if (count < N) data[count++] = v;
            else { Array.Copy(data, 1, data, 0, N - 1); data[N - 1] = v; }
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            if (count < 2 || ActualWidth <= 0) return;
            var h = ActualHeight; var w = ActualWidth;
            Func<int, Point> P = i => new Point(w * (N - count + i) / (N - 1.0), h - 1 - (h - 2) * Math.Min(100, data[i]) / 100.0);
            var line = new StreamGeometry();
            using (var x = line.Open())
            {
                x.BeginFigure(P(0), false, false);
                for (int i = 1; i < count; i++) x.LineTo(P(i), true, true);
            }
            line.Freeze();
            var area = new StreamGeometry();
            using (var x = area.Open())
            {
                x.BeginFigure(new Point(P(0).X, h), true, true);
                for (int i = 0; i < count; i++) x.LineTo(P(i), false, false);
                x.LineTo(new Point(P(count - 1).X, h), false, false);
            }
            area.Freeze();
            var solid = Stroke as SolidColorBrush;
            if (solid != null)
            {
                var col = solid.Color;
                var fill = new LinearGradientBrush(Color.FromArgb(70, col.R, col.G, col.B), Color.FromArgb(0, col.R, col.G, col.B), 90);
                fill.Freeze();
                dc.DrawGeometry(fill, null, area);
            }
            dc.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round }, line);
        }
    }

    public static class Themes
    {
        static readonly Dictionary<string, string> Dark = new Dictionary<string, string>
        {
            {"WinBg","#0E1014"},{"SidebarBg","#0E1014"},{"ContentBg","#14171D"},{"Surface","#1A1E26"},{"SurfaceHover","#262C37"},{"SurfaceAlt","#12151B"},
            {"Control","#232833"},{"DialogBg","#1C2029"},{"Stroke","#2A303B"},{"StrokeStrong","#3D4554"},{"Text","#EEF0F4"},{"TextMuted","#A3ACBB"},{"TextFaint","#747D8D"},
            {"Accent","#3DDBB4"},{"AccentSoft","#223DDBB4"},{"OnAccent","#04241B"},{"HoverLayer","#14FFFFFF"},{"SelectedLayer","#1CFFFFFF"},
            {"Warn","#FFB84D"},{"WarnSoft","#26FFB84D"},{"Danger","#FF6B6B"},{"DangerSoft","#26FF6B6B"},{"OnDanger","#2B0505"},
            {"Info","#6CB6FF"},{"InfoSoft","#226CB6FF"},{"Ok","#3DDBB4"},{"OkSoft","#223DDBB4"},{"ThumbBg","#3D4554"},{"ScrimBg","#99000000"},
        };
        static readonly Dictionary<string, string> Light = new Dictionary<string, string>
        {
            {"WinBg","#E9ECF1"},{"SidebarBg","#E9ECF1"},{"ContentBg","#F5F6F9"},{"Surface","#FFFFFF"},{"SurfaceHover","#E6E9EF"},{"SurfaceAlt","#F1F3F6"},
            {"Control","#FFFFFF"},{"DialogBg","#FFFFFF"},{"Stroke","#DCE0E7"},{"StrokeStrong","#BEC5D1"},{"Text","#12161F"},{"TextMuted","#525C6E"},{"TextFaint","#818A9A"},
            {"Accent","#00806A"},{"AccentSoft","#1A00806A"},{"OnAccent","#FFFFFF"},{"HoverLayer","#0E000000"},{"SelectedLayer","#FFFFFFFF"},
            {"Warn","#A85F00"},{"WarnSoft","#1FE08A00"},{"Danger","#C42B2B"},{"DangerSoft","#1AC42B2B"},{"OnDanger","#FFFFFF"},
            {"Info","#1565C0"},{"InfoSoft","#1A1565C0"},{"Ok","#00806A"},{"OkSoft","#1A00806A"},{"ThumbBg","#BEC5D1"},{"ScrimBg","#66101418"},
        };

        public static bool IsDark { get; private set; }
        public static bool Mica;

        public static void Apply(bool dark)
        {
            IsDark = dark;
            var res = Application.Current.Resources;
            foreach (var kv in dark ? Dark : Light) res[kv.Key] = Make(kv.Value);
            if (Mica)
            {
                // Le fond Mica de Windows 11 traverse la fenêtre : on laisse transparaître une partie.
                res["WinBg"] = Brushes.Transparent;
                res["SidebarBg"] = Brushes.Transparent;
                res["ContentBg"] = Make(dark ? "#B0101319" : "#B8F6F7FA");
            }
        }

        static SolidColorBrush Make(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        public static bool SystemPrefersDark()
        {
            var v = Reg.GetInt(Microsoft.Win32.RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme");
            return !v.HasValue || v.Value == 0;
        }
    }

    public static class Backdrop
    {
        [DllImport("dwmapi")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [StructLayout(LayoutKind.Sequential)] struct Margins { public int L, R, T, B; }
        [DllImport("dwmapi")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref Margins m);

        public static void SetDarkFrame(IntPtr hwnd, bool dark)
        {
            int v = dark ? 1 : 0;
            try { DwmSetWindowAttribute(hwnd, 20, ref v, 4); } catch { }
        }

        /// <summary>Active Mica sur Windows 11 22H2 ou plus ; sinon le thème reste un fond uni.</summary>
        public static bool TryEnableMica(IntPtr hwnd, System.Windows.Interop.HwndSource source)
        {
            if (Os.Build < 22621) return false;
            try
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
                var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref m);
                int type = 2; // DWMSBT_MAINWINDOW
                return DwmSetWindowAttribute(hwnd, 38, ref type, 4) == 0;
            }
            catch { return false; }
        }
    }

    /// <summary>Sélecteur de dossier moderne de Windows (WPF n'en fournit pas).</summary>
    public static class Dialogs
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] class FileOpenDialogCom { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint c, IntPtr spec);
            void SetFileTypeIndex(uint i);
            void GetFileTypeIndex(out uint i);
            void Advise(IntPtr sink, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem si);
            void SetFolder(IShellItem si);
            void GetFolder(out IShellItem si);
            void GetCurrentSelection(out IShellItem si);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetResult(out IShellItem si);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem si);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attrs);
            void Compare(IShellItem si, uint hint, out int order);
        }

        public static string PickFolder(string title)
        {
            try
            {
                var d = (IFileDialog)new FileOpenDialogCom();
                uint o; d.GetOptions(out o);
                d.SetOptions(o | 0x20 | 0x40); // choisir un dossier, système de fichiers uniquement
                d.SetTitle(title);
                var owner = new System.Windows.Interop.WindowInteropHelper(Shell.I.Win).Handle;
                if (d.Show(owner) != 0) return null;
                IShellItem si; d.GetResult(out si);
                string path; si.GetDisplayName(0x80058000, out path);
                return path;
            }
            catch (Exception e) { Logger.Error("Sélecteur de dossier", e); return null; }
        }

        public static string PickOpen(string title, string filter)
        {
            var d = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
            return d.ShowDialog(Shell.I.Win) == true ? d.FileName : null;
        }

        public static string PickSave(string title, string filter, string name)
        {
            var d = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = name, OverwritePrompt = true };
            return d.ShowDialog(Shell.I.Win) == true ? d.FileName : null;
        }
    }
}
