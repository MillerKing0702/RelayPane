using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace CollaborationChatPreview
{
    internal sealed class WorkspaceLayout
    {
        public const double WideThreshold = 800;

        public readonly Dictionary<ChatPane, Rect> Bounds = new Dictionary<ChatPane, Rect>();
        public readonly Dictionary<ChatPane, PanePlacement> Placements = new Dictionary<ChatPane, PanePlacement>();

        public static WorkspaceLayout Arrange(Rect frame, IList<ChatPane> panes, ChatPane main,
            ChatPane relocated = null, PanePlacement destination = PanePlacement.Auto)
        {
            var layout = new WorkspaceLayout();
            if (panes.Count == 1)
            {
                double width = Math.Min(frame.Width, Math.Min(750, Math.Max(390, frame.Width * 0.82)));
                double height = Math.Min(frame.Height, Math.Min(620, Math.Max(300, frame.Height * 0.86)));
                layout.Bounds[main] = Centered(frame, width, height);
                return layout;
            }

            bool wide = frame.Width >= WideThreshold;
            var left = new List<ChatPane>();
            var right = new List<ChatPane>();
            var top = new List<ChatPane>();
            var bottom = new List<ChatPane>();
            var automatic = new List<ChatPane>();
            var regions = new Dictionary<PanePlacement, List<ChatPane>>
            {
                { PanePlacement.Left, left }, { PanePlacement.Right, right },
                { PanePlacement.Top, top }, { PanePlacement.Bottom, bottom }
            };

            foreach (ChatPane pane in panes)
            {
                if (pane == main) continue;
                PanePlacement placement = pane == relocated ? destination : pane.Placement;
                if (!wide && placement == PanePlacement.Left) placement = PanePlacement.Top;
                if (!wide && placement == PanePlacement.Right) placement = PanePlacement.Bottom;
                if (placement == PanePlacement.Auto) automatic.Add(pane);
                else regions[placement].Add(pane);
            }
            foreach (ChatPane pane in automatic)
            {
                List<ChatPane> first = wide ? right : bottom;
                List<ChatPane> second = wide ? left : top;
                (first.Count <= second.Count ? first : second).Add(pane);
            }
            double gap = wide ? 30 : 16;
            double railWidth = left.Count + right.Count > 0 ? Clamp(frame.Width * 0.20, 176, 290) : 0;
            double side = railWidth > 0 ? railWidth + gap : 0;
            double laneWidth = Math.Min(920, frame.Width - side * 2);
            int bandColumns = BandColumns(laneWidth, gap);
            ReflowBand(top, bottom, bandColumns);
            ReflowBand(bottom, top, bandColumns);
            foreach (var region in regions)
                foreach (ChatPane pane in region.Value) layout.Placements[pane] = region.Key;
            double bandHeight = top.Count + bottom.Count > 0 ? Math.Min(185, frame.Height * (wide ? 0.23 : 0.28)) : 0;
            double vertical = bandHeight > 0 ? bandHeight + gap : 0;
            double centerHeight = Math.Min(730, frame.Height - vertical * 2);
            var center = Centered(frame, laneWidth, centerHeight);
            layout.Bounds[main] = center;

            if (left.Count > 0)
                layout.PlaceRail(left, center.Left - gap - railWidth, railWidth, frame, gap);
            if (right.Count > 0)
                layout.PlaceRail(right, center.Right + gap, railWidth, frame, gap);
            if (top.Count > 0)
                layout.PlaceBand(top, new Rect(center.Left, center.Top - gap - bandHeight, center.Width, bandHeight), gap);
            if (bottom.Count > 0)
                layout.PlaceBand(bottom, new Rect(center.Left, center.Bottom + gap, center.Width, bandHeight), gap);
            return layout;
        }

        public static PanePlacement EdgeAt(Rect frame, Point pointer)
        {
            if (!frame.Contains(pointer)) return PanePlacement.Auto;
            double x = (pointer.X - frame.Left) / frame.Width;
            double y = (pointer.Y - frame.Top) / frame.Height;
            if (frame.Width >= WideThreshold)
            {
                if (x < 0.22) return PanePlacement.Left;
                if (x > 0.78) return PanePlacement.Right;
            }
            if (y < 0.26) return PanePlacement.Top;
            if (y > 0.74) return PanePlacement.Bottom;
            return PanePlacement.Auto;
        }

        private void PlaceRail(IList<ChatPane> panes, double left, double width, Rect frame, double gap)
        {
            double extent = Math.Min(frame.Height, panes.Count == 1 ? 540 : 730);
            double height = (extent - gap * (panes.Count - 1)) / panes.Count;
            double top = frame.Top + (frame.Height - extent) / 2;
            for (int index = 0; index < panes.Count; index++)
                Bounds[panes[index]] = new Rect(left, top + (height + gap) * index, width, height);
        }

        private void PlaceBand(IList<ChatPane> panes, Rect band, double gap)
        {
            int columns = Math.Min(panes.Count, BandColumns(band.Width, gap));
            int rows = (panes.Count + columns - 1) / columns;
            double width = (band.Width - gap * (columns - 1)) / columns;
            double height = (band.Height - gap * (rows - 1)) / rows;
            for (int index = 0; index < panes.Count; index++)
            {
                int row = index / columns;
                int inRow = Math.Min(columns, panes.Count - row * columns);
                double rowWidth = inRow * width + (inRow - 1) * gap;
                Bounds[panes[index]] = new Rect(band.Left + (band.Width - rowWidth) / 2 + (width + gap) * (index % columns),
                    band.Top + row * (height + gap), width, height);
            }
        }

        private static int BandColumns(double width, double gap)
        {
            return Math.Max(1, (int)((width + gap) / (164 + gap)));
        }

        private static void ReflowBand(List<ChatPane> from, List<ChatPane> to, int columns)
        {
            while (from.Count > columns && to.Count < columns)
            {
                int trailing = from.Count - 1;
                to.Add(from[trailing]);
                from.RemoveAt(trailing);
            }
        }

        private static Rect Centered(Rect area, double width, double height)
        {
            return new Rect(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
        }

        private static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(value, max));
        }
    }
}
