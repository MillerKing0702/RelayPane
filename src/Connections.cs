using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace CollaborationChatPreview
{
    internal sealed class Connections : FrameworkElement, IDisposable
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly DispatcherTimer timer;
        private readonly Dictionary<AgentKind, Connection> links = new Dictionary<AgentKind, Connection>();
        private Rect main = Rect.Empty;
        private IDictionary<AgentKind, Rect> endpoints = new Dictionary<AgentKind, Rect>();
        private bool dark;

        private sealed class Connection
        {
            public string Phase;
            public long Started;
        }

        public Action<AgentKind> Completed;

        public Connections()
        {
            IsHitTestVisible = false;
            timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(32) };
            timer.Tick += Animate;
        }

        public void Reposition(Rect origin, IDictionary<AgentKind, Rect> cards)
        {
            main = origin;
            endpoints = cards;
            InvalidateVisual();
        }

        public void Theme(bool darkMode)
        {
            dark = darkMode;
            InvalidateVisual();
        }

        public void Reset()
        {
            links.Clear();
            timer.Stop();
            InvalidateVisual();
        }

        public void Set(AgentKind peer, string phase)
        {
            links[peer] = new Connection { Phase = phase, Started = watch.ElapsedMilliseconds };
            if (IsAnimated(phase)) timer.Start();
            InvalidateVisual();
        }

        private void Animate(object sender, EventArgs args)
        {
            foreach (var pair in links.ToList())
            {
                if (pair.Value.Phase == "returning" && watch.ElapsedMilliseconds - pair.Value.Started >= 820)
                {
                    pair.Value.Phase = "completed";
                    if (Completed != null) Completed(pair.Key);
                }
            }
            if (!links.Values.Any(item => IsAnimated(item.Phase))) timer.Stop();
            InvalidateVisual();
        }

        private static bool IsAnimated(string phase)
        {
            return phase == "sending" || phase == "running" || phase == "returning";
        }

        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            if (main.IsEmpty) return;
            foreach (var pair in endpoints)
            {
                Connection state;
                links.TryGetValue(pair.Key, out state);
                string phase = state == null ? "idle" : state.Phase;
                Point start;
                Point end;
                PathGeometry path = MakePath(main, pair.Value, out start, out end);
                Color color = Shade(phase);
                bool complete = phase == "completed";
                bool moving = IsAnimated(phase);
                var brush = new SolidColorBrush(color);
                if (complete)
                {
                    var halo = new SolidColorBrush(Color.FromArgb(dark ? (byte)45 : (byte)34, color.R, color.G, color.B));
                    drawing.DrawGeometry(null, RoundedPen(halo, 7), path);
                }
                drawing.DrawGeometry(null, RoundedPen(brush, complete ? 2.8 : phase == "idle" ? 1 : 1.4), path);
                if (phase != "idle")
                {
                    Brush background = new SolidColorBrush(dark ? Color.FromRgb(17, 17, 17) : Color.FromRgb(241, 244, 248));
                    Pen port = RoundedPen(brush, complete ? 1.5 : 1.05);
                    double radius = complete ? 3.5 : 2.3;
                    drawing.DrawEllipse(background, port, start, radius, radius);
                    drawing.DrawEllipse(complete ? brush : background, port, end, radius, radius);
                }
                if (!moving || !SystemParameters.ClientAreaAnimation) continue;
                DrawTransfer(drawing, path, phase, watch.ElapsedMilliseconds - state.Started, color);
            }
        }

        private static Pen RoundedPen(Brush brush, double width)
        {
            return new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        }

        private void DrawTransfer(DrawingContext drawing, PathGeometry path, string phase, double age, Color baseColor)
        {
            double elapsed = phase == "returning" ? age : age % (phase == "sending" ? 1340 : 2050);
            double duration = phase == "returning" ? 800 : 740;
            if (elapsed >= duration) return;
            double time = elapsed / duration;
            double eased = time * time * (3 - 2 * time);
            double head = phase == "returning" ? 1.12 - 1.25 * eased : -0.12 + 1.25 * eased;
            double tail = phase == "returning" ? head + 0.30 : head - 0.30;
            double first = Math.Max(0, Math.Min(head, tail));
            double last = Math.Min(1, Math.Max(head, tail));
            if (last <= first) return;

            Point point;
            Point tangent;
            path.GetPointAtFractionLength(first, out point, out tangent);
            var start = point;
            var points = new List<Point>();
            for (int step = 1; step <= 5; step++)
            {
                path.GetPointAtFractionLength(first + (last - first) * step / 5, out point, out tangent);
                points.Add(point);
            }
            var segment = new PathGeometry(new[] { new PathFigure(start, new[] { new PolyLineSegment(points, true) }, false) });
            Color light = dark ? Color.FromRgb(224, 204, 176) : Color.FromRgb(56, 101, 211);
            Brush trail = new SolidColorBrush(Color.FromArgb(34, baseColor.R, baseColor.G, baseColor.B));
            drawing.DrawGeometry(null, RoundedPen(trail, 4), segment);
            drawing.DrawGeometry(null, RoundedPen(new SolidColorBrush(light), 2.05), segment);
        }

        private Color Shade(string phase)
        {
            if (phase == "completed") return dark ? Color.FromRgb(108, 196, 161) : Color.FromRgb(42, 146, 110);
            if (phase == "failed" || phase == "cancelled") return dark ? Color.FromRgb(198, 144, 107) : Color.FromRgb(162, 97, 48);
            if (IsAnimated(phase)) return dark ? Color.FromRgb(167, 147, 120) : Color.FromRgb(85, 124, 207);
            return dark ? Color.FromRgb(81, 77, 71) : Color.FromRgb(184, 198, 219);
        }

        private static PathGeometry MakePath(Rect a, Rect b, out Point start, out Point end)
        {
            bool horizontal = b.Right <= a.Left || b.Left >= a.Right;
            Point control1;
            Point control2;
            if (horizontal)
            {
                bool left = b.Right <= a.Left;
                double y = b.Top + b.Height * 0.5;
                double centerTransfer = Math.Max(a.Top + 13, Math.Min(a.Bottom - 13, y));
                start = new Point(left ? a.Left - 3 : a.Right + 3, centerTransfer);
                end = new Point(left ? b.Right + 3 : b.Left - 3, y);
                double middle = (start.X + end.X) / 2;
                control1 = new Point(middle, start.Y);
                control2 = new Point(middle, end.Y);
            }
            else
            {
                bool above = b.Bottom <= a.Top;
                double x = b.Left + b.Width * 0.5;
                start = new Point(Math.Max(a.Left + 13, Math.Min(a.Right - 13, x)), above ? a.Top - 3 : a.Bottom + 3);
                end = new Point(x, above ? b.Bottom + 3 : b.Top - 3);
                double middle = (start.Y + end.Y) / 2;
                control1 = new Point(start.X, middle);
                control2 = new Point(end.X, middle);
            }
            return new PathGeometry(new[] { new PathFigure(start, new[] { new BezierSegment(control1, control2, end, true) }, false) });
        }

        public void Dispose() { timer.Stop(); }
    }
}
