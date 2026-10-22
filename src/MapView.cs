using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SquadTools
{
    public sealed class MapView : FrameworkElement
    {
        public double WorldSize = 3000;
        public Vec2 Weapon;
        public Vec2 Target;
        public bool HasMarkers;
        public BitmapSource MapImage;
        public bool PlaceWeapon;
        public event Action<Vec2, bool> PointPlaced;
        private double zoom = 1;
        private Vector pan;
        private Point dragStart;
        private Vector panStart;
        private bool dragging;
        private readonly Typeface font = new Typeface("Microsoft YaHei UI");

        public MapView()
        {
            ClipToBounds = true;
            Focusable = true;
            Cursor = Cursors.Cross;
            MouseWheel += OnWheel;
            MouseDown += OnDown;
            MouseMove += OnMove;
            MouseUp += delegate { dragging = false; ReleaseMouseCapture(); Cursor = Cursors.Cross; };
            SizeChanged += delegate { InvalidateVisual(); };
        }

        private double BaseScale { get { return Math.Max(1, Math.Min(ActualWidth - 90, ActualHeight - 70)) / WorldSize; } }
        private double Scale { get { return BaseScale * zoom; } }
        private Point Origin { get { return new Point((ActualWidth - WorldSize * Scale) / 2 + pan.X, (ActualHeight - WorldSize * Scale) / 2 + pan.Y); } }
        private Point Screen(Vec2 p) { Point o = Origin; return new Point(o.X + p.X * Scale, o.Y + p.Y * Scale); }
        private Vec2 World(Point p) { Point o = Origin; return new Vec2((p.X - o.X) / Scale, (p.Y - o.Y) / Scale); }
        public void ResetView() { zoom = 1; pan = new Vector(); InvalidateVisual(); }
        public void ZoomBy(double factor) { zoom = Math.Max(0.7, Math.Min(12, zoom * factor)); InvalidateVisual(); }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            Point p = e.GetPosition(this);
            Vec2 before = World(p);
            zoom = Math.Max(0.7, Math.Min(12, zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2)));
            Point after = Screen(before);
            pan += p - after;
            InvalidateVisual();
            e.Handled = true;
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            Focus();
            if (e.ChangedButton == MouseButton.Middle)
            {
                dragging = true; dragStart = e.GetPosition(this); panStart = pan;
                CaptureMouse(); Cursor = Cursors.SizeAll; e.Handled = true; return;
            }
            if (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Right) return;
            Vec2 p = World(e.GetPosition(this));
            if (p.X < 0 || p.Y < 0 || p.X >= WorldSize || p.Y >= WorldSize) return;
            if (PointPlaced != null) PointPlaced(p, e.ChangedButton == MouseButton.Right || PlaceWeapon);
            e.Handled = true;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (dragging) { pan = panStart + e.GetPosition(this) - dragStart; InvalidateVisual(); }
        }

        private static SolidColorBrush Brush(string hex) { return (SolidColorBrush)new BrushConverter().ConvertFromString(hex); }
        private void Text(DrawingContext dc, string value, double x, double y, double size, string color)
        {
            dc.DrawText(new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, font, size, Brush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRectangle(Brush("#101B24"), null, new Rect(RenderSize));
            if (ActualWidth < 10 || ActualHeight < 10) return;
            Point origin = Origin;
            double side = WorldSize * Scale;
            Rect bounds = new Rect(origin.X, origin.Y, side, side);
            dc.DrawRectangle(Brush("#182630"), new Pen(Brush("#3C5262"), 1), bounds);
            if (MapImage != null) dc.DrawImage(MapImage, bounds);
            dc.PushClip(new RectangleGeometry(bounds));

            if (HasMarkers)
            {
                double radius = Ballistics.Velocity * Ballistics.Velocity / Ballistics.Gravity * Scale;
                dc.DrawEllipse(Brush("#0EAECE75"), new Pen(Brush("#5075BC92"), 1) { DashStyle = DashStyles.Dash }, Screen(Weapon), radius, radius);
            }
            int cells = (int)Math.Ceiling(WorldSize / 300);
            for (int i = 0; i <= cells; i++)
            {
                double value = Math.Min(i * 300, WorldSize);
                Point a = Screen(new Vec2(value, 0)), b = Screen(new Vec2(value, WorldSize));
                Point c = Screen(new Vec2(0, value)), d = Screen(new Vec2(WorldSize, value));
                var pen = new Pen(Brush(MapImage == null ? "#344653" : "#80CFD9DA"), 1);
                dc.DrawLine(pen, a, b); dc.DrawLine(pen, c, d);
                if (300 * Scale > 22 && i < cells)
                {
                    double mid = (value + Math.Min(value + 300, WorldSize)) / 2;
                    Point label = Screen(new Vec2(mid, 0));
                    Text(dc, ((char)('A' + i)).ToString(), label.X - 5, Math.Max(6, origin.Y + 5), 12, "#C6D3D9");
                    label = Screen(new Vec2(0, mid));
                    Text(dc, (i + 1).ToString("00"), Math.Max(8, origin.X + 6), label.Y - 7, 11, "#C6D3D9");
                }
            }
            if (300 * Scale > 160)
            {
                var minor = new Pen(Brush("#273D4B"), 0.6) { DashStyle = DashStyles.Dot };
                for (double m = 100; m < WorldSize; m += 100)
                {
                    if (Math.Abs(m % 300) < 0.1) continue;
                    dc.DrawLine(minor, Screen(new Vec2(m, 0)), Screen(new Vec2(m, WorldSize)));
                    dc.DrawLine(minor, Screen(new Vec2(0, m)), Screen(new Vec2(WorldSize, m)));
                }
            }
            if (HasMarkers)
            {
                Point a = Screen(Weapon), b = Screen(Target);
                dc.DrawLine(new Pen(Brush("#E2D57C"), 2) { DashStyle = DashStyles.Dash }, a, b);
                Marker(dc, a, "M", "#B8EB7E"); Marker(dc, b, "T", "#FFA88C");
                double distance = Math.Sqrt(Math.Pow(Target.X - Weapon.X, 2) + Math.Pow(Target.Y - Weapon.Y, 2));
                Point mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                dc.DrawRoundedRectangle(Brush("#ED101B24"), null, new Rect(mid.X - 38, mid.Y - 13, 92, 26), 5, 5);
                Text(dc, distance.ToString("F0") + " m", mid.X - 26, mid.Y - 9, 12, "#EFE9C8");
            }
            dc.Pop();
            Text(dc, "N", ActualWidth - 35, 17, 15, "#C4D0D6");
            dc.DrawLine(new Pen(Brush("#C4D0D6"), 2), new Point(ActualWidth - 29, 41), new Point(ActualWidth - 29, 62));
            dc.DrawLine(new Pen(Brush("#C4D0D6"), 2), new Point(ActualWidth - 29, 41), new Point(ActualWidth - 34, 49));
            double scaleLength = 300 * Scale;
            while (scaleLength > ActualWidth / 3) scaleLength /= 3;
            while (scaleLength < 45) scaleLength *= 3;
            dc.DrawRoundedRectangle(Brush("#E8101B24"), null, new Rect(15, ActualHeight - 48, scaleLength + 28, 36), 4, 4);
            dc.DrawLine(new Pen(Brush("#BECDD5"), 2), new Point(28, ActualHeight - 22), new Point(28 + scaleLength, ActualHeight - 22));
            Text(dc, (scaleLength / Scale).ToString("F0") + " m", 29, ActualHeight - 44, 10, "#BECDD5");
            if (MapImage == null) Text(dc, "坐标网格 · 可导入本地地图图片", Math.Max(18, ActualWidth - 258), ActualHeight - 37, 12, "#8196A5");
        }

        private void Marker(DrawingContext dc, Point p, string label, string color)
        {
            dc.DrawEllipse(Brush("#17232B"), new Pen(Brush(color), 2), p, 14, 14);
            dc.DrawLine(new Pen(Brush(color), 1), new Point(p.X - 21, p.Y), new Point(p.X - 15, p.Y));
            dc.DrawLine(new Pen(Brush(color), 1), new Point(p.X + 15, p.Y), new Point(p.X + 21, p.Y));
            dc.DrawLine(new Pen(Brush(color), 1), new Point(p.X, p.Y - 21), new Point(p.X, p.Y - 15));
            Text(dc, label, p.X - 5, p.Y - 9, 12, color);
        }
    }
}
