using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OptiTech
{
    public static class Ui
    {
        public static object Load(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("OptiTech.ui." + name))
            {
                if (s == null) throw new FileNotFoundException("Ressource absente : " + name);
                return XamlReader.Load(s);
            }
        }

        /// <summary>
        /// Mode léger : coupe les animations, le flou et les halos translucides pour garder une interface
        /// instantanée sur les vieux PC ou sans accélération matérielle.
        /// </summary>
        public static bool Lightweight
        {
            get
            {
                var v = Store.Setting("ui.lightweight");
                if (v == "1") return true;
                if (v == "0") return false;
                try { return !SystemParameters.ClientAreaAnimation || (RenderCapability.Tier >> 16) == 0; }
                catch { return false; }
            }
            set { Store.SetSetting("ui.lightweight", value ? "1" : "0"); }
        }

        /// <summary>Déclenche une recherche une fois la frappe terminée, pour éviter de reconstruire la liste à chaque lettre.</summary>
        public static System.Windows.Threading.DispatcherTimer Debounce(System.Windows.Controls.TextBox box, int ms, Action action)
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += delegate { t.Stop(); action(); };
            box.TextChanged += delegate { t.Stop(); t.Start(); };
            return t;
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
        /// <summary>F5 : recharge ce que la page affiche.</summary>
        public virtual void OnRefresh() { }
        /// <summary>Ctrl+F : donne le focus au champ de recherche de la page, s'il y en a un.</summary>
        public virtual bool OnFind() { return false; }
    }

    /// <summary>Taille, position et état de la fenêtre, retrouvés d'une session à l'autre.</summary>
    public static class WindowMemory
    {
        const string Key = "win.bounds";

        public static void Restore(Window w)
        {
            var v = Store.Setting(Key);
            if (v == null) return;
            var p = v.Split(',');
            double x, y, cw, ch;
            if (p.Length < 5 || !TryD(p[0], out x) || !TryD(p[1], out y) || !TryD(p[2], out cw) || !TryD(p[3], out ch)) return;
            if (cw < w.MinWidth || ch < w.MinHeight) return;
            // La fenêtre doit rester attrapable : un écran débranché depuis ne doit pas la faire disparaître.
            var left = SystemParameters.VirtualScreenLeft; var top = SystemParameters.VirtualScreenTop;
            var right = left + SystemParameters.VirtualScreenWidth; var bottom = top + SystemParameters.VirtualScreenHeight;
            if (x + cw < left + 120 || x > right - 120 || y < top - 10 || y > bottom - 120) return;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = x; w.Top = y; w.Width = cw; w.Height = ch;
            if (p[4] == "1") w.WindowState = WindowState.Maximized;
        }

        public static void Save(Window w)
        {
            try
            {
                var b = w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.Width, w.Height) : w.RestoreBounds;
                if (b.IsEmpty || b.Width <= 0 || b.Height <= 0) return;
                Store.SetSetting(Key, string.Join(",", new[] { S(b.X), S(b.Y), S(b.Width), S(b.Height), w.WindowState == WindowState.Maximized ? "1" : "0" }));
            }
            catch (Exception e) { Logger.Error("Mémorisation de la fenêtre", e); }
        }

        static string S(double d) { return Math.Round(d).ToString(CultureInfo.InvariantCulture); }
        static bool TryD(string s, out double d) { return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d); }
    }

    /// <summary>Jauge circulaire du score de santé. Les stylos sont figés et mis en cache pour animer à 60 i/s sans allocation.</summary>
    public sealed class RingGauge : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register("Value", typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register("Stroke", typeof(Brush), typeof(RingGauge), new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender, OnPenPropChanged));
        public static readonly DependencyProperty TrackProperty = DependencyProperty.Register("Track", typeof(Brush), typeof(RingGauge), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender, OnPenPropChanged));
        public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register("Thickness", typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender, OnPenPropChanged));
        public double Value { get { return (double)GetValue(ValueProperty); } set { SetValue(ValueProperty, value); } }
        public Brush Stroke { get { return (Brush)GetValue(StrokeProperty); } set { SetValue(StrokeProperty, value); } }
        public Brush Track { get { return (Brush)GetValue(TrackProperty); } set { SetValue(TrackProperty, value); } }
        public double Thickness { get { return (double)GetValue(ThicknessProperty); } set { SetValue(ThicknessProperty, value); } }

        Pen trackPen, strokePen;
        static void OnPenPropChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var g = (RingGauge)d;
            g.trackPen = null; g.strokePen = null;
        }

        public void AnimateTo(double v)
        {
            if (Ui.Lightweight) { BeginAnimation(ValueProperty, null); Value = v; return; }
            BeginAnimation(ValueProperty, new DoubleAnimation(v, new Duration(TimeSpan.FromMilliseconds(650))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        protected override void OnRender(DrawingContext dc)
        {
            var t = Thickness;
            var size = Math.Min(ActualWidth, ActualHeight);
            if (size <= t) return;
            var r = (size - t) / 2;
            var c = new Point(ActualWidth / 2, ActualHeight / 2);
            if (trackPen == null && Track != null)
            {
                trackPen = new Pen(Track, t);
                if (trackPen.CanFreeze) trackPen.Freeze();
            }
            if (trackPen != null) dc.DrawEllipse(null, trackPen, c, r, r);
            var v = Math.Max(0, Math.Min(100, Value));
            if (v <= 0) return;
            if (strokePen == null && Stroke != null)
            {
                strokePen = new Pen(Stroke, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                if (strokePen.CanFreeze) strokePen.Freeze();
            }
            if (strokePen == null) return;
            if (v >= 99.9) { dc.DrawEllipse(null, strokePen, c, r, r); return; }
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
            dc.DrawGeometry(null, strokePen, g);
        }
    }

    /// <summary>Courbe lissée en direct, dessinée sans graphe d'objets et avec pinceaux mis en cache pour rester très légère.</summary>
    public sealed class Sparkline : FrameworkElement
    {
        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register("Stroke", typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender, OnStrokeChanged));
        public Brush Stroke { get { return (Brush)GetValue(StrokeProperty); } set { SetValue(StrokeProperty, value); } }
        const int N = 60;
        readonly double[] data = new double[N];
        readonly Point[] pts = new Point[N];
        int count;

        Pen linePen, gridPen;
        Brush areaBrush, haloBrush;
        static void OnStrokeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var s = (Sparkline)d;
            s.linePen = null; s.areaBrush = null; s.haloBrush = null; s.gridPen = null;
        }

        public void Push(double v)
        {
            v = Math.Max(0, Math.Min(100, v));
            if (count < N) data[count++] = v;
            else { Array.Copy(data, 1, data, 0, N - 1); data[N - 1] = v; }
            InvalidateVisual();
        }

        void EnsureBrushes()
        {
            if (linePen != null || Stroke == null) return;
            linePen = new Pen(Stroke, 1.8) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (linePen.CanFreeze) linePen.Freeze();
            var solid = Stroke as SolidColorBrush;
            if (solid != null)
            {
                var col = solid.Color;
                var g = new LinearGradientBrush(Color.FromArgb(68, col.R, col.G, col.B), Color.FromArgb(0, col.R, col.G, col.B), 90);
                g.Freeze();
                areaBrush = g;
                var h = new SolidColorBrush(Color.FromArgb(72, col.R, col.G, col.B));
                h.Freeze();
                haloBrush = h;
                var gp = new Pen(new SolidColorBrush(Color.FromArgb(24, col.R, col.G, col.B)), 1) { DashStyle = DashStyles.Dot };
                gp.Freeze();
                gridPen = gp;
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            var w = ActualWidth; var h = ActualHeight;
            if (w <= 2 || h <= 2) return;
            EnsureBrushes();
            if (gridPen != null) dc.DrawLine(gridPen, new Point(0, h * 0.5), new Point(w, h * 0.5));
            if (count < 2) return;

            // Étale proprement la courbe dès les premiers échantillons, puis défile en continu une fois pleine.
            var padRight = 3.5;
            var usableW = Math.Max(1, w - padRight);
            var span = Math.Max(count - 1, 14);
            var offset = span - (count - 1);
            for (int i = 0; i < count; i++)
            {
                var x = usableW * (offset + i) / span;
                var y = h - 2.5 - (h - 5.5) * data[i] / 100.0;
                pts[i] = new Point(x, y);
            }

            var line = new StreamGeometry();
            var area = new StreamGeometry();
            using (var lx = line.Open())
            using (var ax = area.Open())
            {
                lx.BeginFigure(pts[0], false, false);
                ax.BeginFigure(new Point(pts[0].X, h), true, true);
                ax.LineTo(pts[0], false, false);
                if (count == 2)
                {
                    lx.LineTo(pts[1], true, true);
                    ax.LineTo(pts[1], false, false);
                }
                else
                {
                    for (int i = 1; i < count - 1; i++)
                    {
                        var mid = new Point((pts[i].X + pts[i + 1].X) * 0.5, (pts[i].Y + pts[i + 1].Y) * 0.5);
                        lx.QuadraticBezierTo(pts[i], mid, true, true);
                        ax.QuadraticBezierTo(pts[i], mid, false, false);
                    }
                    var last = pts[count - 1];
                    lx.LineTo(last, true, true);
                    ax.LineTo(last, false, false);
                }
                ax.LineTo(new Point(pts[count - 1].X, h), false, false);
            }
            line.Freeze();
            area.Freeze();

            if (areaBrush != null) dc.DrawGeometry(areaBrush, null, area);
            if (linePen != null) dc.DrawGeometry(null, linePen, line);
            var tip = pts[count - 1];
            if (haloBrush != null) dc.DrawEllipse(haloBrush, null, tip, 4.8, 4.8);
            if (Stroke != null) dc.DrawEllipse(Stroke, null, tip, 2.3, 2.3);
        }
    }

    public static class Themes
    {
        // Verre dépoli : les surfaces sont translucides et laissent voir les halos de couleur du fond,
        // ou le fond acrylique de Windows 11 quand il est disponible.
        static readonly Dictionary<string, string> Dark = new Dictionary<string, string>
        {
            {"WinBg","#090C13"},{"SidebarBg","#00000000"},{"ContentBg","#0EFFFFFF"},{"SurfaceHover","#26FFFFFF"},{"SurfaceAlt","#38000000"},
            {"Control","#1CFFFFFF"},{"DialogBg","#F4151A25"},{"Stroke","#1FFFFFFF"},{"StrokeStrong","#42FFFFFF"},{"Text","#F3F5FA"},{"TextMuted","#B0B8C9"},{"TextFaint","#7E889C"},
            {"Accent","#4FE3C1"},{"AccentSoft","#264FE3C1"},{"OnAccent","#04201A"},{"HoverLayer","#16FFFFFF"},{"SelectedLayer","#22FFFFFF"},
            {"Warn","#FFB84D"},{"WarnSoft","#26FFB84D"},{"Danger","#FF6B6B"},{"DangerSoft","#26FF6B6B"},{"OnDanger","#2B0505"},
            {"Info","#6CB6FF"},{"InfoSoft","#226CB6FF"},{"Ok","#4FE3C1"},{"OkSoft","#224FE3C1"},{"ThumbBg","#3DFFFFFF"},{"ScrimBg","#78000000"},
            {"KbdBg","#18FFFFFF"},
        };
        static readonly Dictionary<string, string> Light = new Dictionary<string, string>
        {
            {"WinBg","#E6EBF5"},{"SidebarBg","#00FFFFFF"},{"ContentBg","#66FFFFFF"},{"SurfaceHover","#1F1B2A4A"},{"SurfaceAlt","#94FFFFFF"},
            {"Control","#D6FFFFFF"},{"DialogBg","#FAFFFFFF"},{"Stroke","#221B2A4A"},{"StrokeStrong","#4A1B2A4A"},{"Text","#111522"},{"TextMuted","#4C5568"},{"TextFaint","#788296"},
            {"Accent","#00846E"},{"AccentSoft","#1F00846E"},{"OnAccent","#FFFFFF"},{"HoverLayer","#0E000000"},{"SelectedLayer","#CCFFFFFF"},
            {"Warn","#A85F00"},{"WarnSoft","#1FE08A00"},{"Danger","#C42B2B"},{"DangerSoft","#1AC42B2B"},{"OnDanger","#FFFFFF"},
            {"Info","#1565C0"},{"InfoSoft","#1A1565C0"},{"Ok","#00846E"},{"OkSoft","#1A00846E"},{"ThumbBg","#4D1B2A4A"},{"ScrimBg","#4D101828"},
            {"KbdBg","#121B2A4A"},
        };

        public static bool IsDark { get; private set; }
        /// <summary>Le fond de la fenêtre est fourni par Windows (acrylique ou Mica) : le nôtre devient transparent.</summary>
        public static bool Mica;

        public static void Apply(bool dark)
        {
            IsDark = dark;
            var light = Ui.Lightweight;
            var res = Application.Current.Resources;
            var map = dark ? Dark : Light;
            foreach (var kv in map) res[kv.Key] = Make(kv.Value);
            if (Mica && !light)
            {
                res["WinBg"] = Brushes.Transparent;
                res["ContentBg"] = Make(dark ? "#730C0F17" : "#80F4F6FB");
            }
            var accent = Hex(map["Accent"]);
            var blue = Hex(dark ? "#5B9DFF" : "#2F6FE0");
            var violet = Hex(dark ? "#9B7BFF" : "#7A55E0");
            res["AccentColor"] = accent;

            if (light)
            {
                // En mode léger : surfaces opaques et nettes, sans calcul de transparence ni halos.
                res["ContentBg"] = Make(dark ? "#111521" : "#F3F6FB");
                res["Surface"] = Make(dark ? "#171C2B" : "#FFFFFF");
                res["SurfaceAlt"] = Make(dark ? "#121623" : "#EDF1F8");
                res["GlassBorder"] = Make(dark ? "#283047" : "#D4DCEB");
                res["Control"] = Make(dark ? "#1E2538" : "#FFFFFF");
            }
            else
            {
                // Surfaces de verre : un dégradé de blanc translucide, plus dense en haut à gauche, d'où vient la lumière.
                res["Surface"] = Linear(new Point(0, 0), new Point(1, 1),
                    White((byte)(dark ? 0x1F : 0xD6)), White((byte)(dark ? 0x0C : 0x9A)));
                // Liseré : clair sur l'arête éclairée, presque éteint à l'opposé.
                res["GlassBorder"] = Linear(new Point(0, 0), new Point(1, 1),
                    White((byte)(dark ? 0x5A : 0xFF)), White((byte)(dark ? 0x14 : 0x8C)), Color.FromArgb((byte)(dark ? 0x32 : 0x42), accent.R, accent.G, accent.B));
            }
            res["AccentGradient"] = Linear(new Point(0, 0), new Point(1, 1), accent, blue);
            res["LogoGradient"] = Linear(new Point(0, 0), new Point(1, 1), Hex("#4FE3C1"), Hex("#4F8CFF"), Hex("#9B6BFF"));
            res["GlassSheen"] = Linear(new Point(0, 0), new Point(0, 1), White(0x66), White(0x00));

            // Halos de couleur derrière le verre. Plus discrets quand Windows fournit déjà un fond acrylique.
            var k = light ? 0.0 : Mica ? 0.55 : 1.0;
            res["AmbientA"] = Halo(accent, (byte)((dark ? 0x52 : 0x66) * k));
            res["AmbientB"] = Halo(violet, (byte)((dark ? 0x4A : 0x52) * k));
            res["AmbientC"] = Halo(blue, (byte)((dark ? 0x40 : 0x4D) * k));
            res["HeroGlow"] = Glow(accent, (byte)((dark ? 0x40 : 0x2E) * (light ? 0.4 : 1.0)));
        }

        static Color Hex(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        static Color White(byte a) { return Color.FromArgb(a, 255, 255, 255); }

        static SolidColorBrush Make(string hex)
        {
            var b = new SolidColorBrush(Hex(hex));
            b.Freeze();
            return b;
        }

        static LinearGradientBrush Linear(Point from, Point to, params Color[] colors)
        {
            var g = new LinearGradientBrush { StartPoint = from, EndPoint = to };
            for (int i = 0; i < colors.Length; i++)
                g.GradientStops.Add(new GradientStop(colors[i], colors.Length == 1 ? 0 : (double)i / (colors.Length - 1)));
            g.Freeze();
            return g;
        }

        /// <summary>Tache de couleur ronde, qui s'éteint vers ses bords.</summary>
        static RadialGradientBrush Halo(Color c, byte alpha)
        {
            var g = new RadialGradientBrush();
            g.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, c.R, c.G, c.B), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(alpha / 3), c.R, c.G, c.B), 0.55));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
            g.Freeze();
            return g;
        }

        /// <summary>Halo radial discret, ancré dans le coin gauche d'une carte.</summary>
        static RadialGradientBrush Glow(Color c, byte alpha)
        {
            var g = new RadialGradientBrush
            {
                Center = new Point(0.1, 0.5), GradientOrigin = new Point(0.1, 0.5), RadiusX = 0.55, RadiusY = 1.8,
                MappingMode = BrushMappingMode.RelativeToBoundingBox
            };
            g.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, c.R, c.G, c.B), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
            g.Freeze();
            return g;
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
                int type = 3; // DWMSBT_TRANSIENTWINDOW : acrylique, le vrai verre dépoli de Windows 11
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
