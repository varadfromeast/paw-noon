namespace PawNoon.Runtime
{
    /// <summary>
    /// Leaderboard list maths (engine-free, tested in PawNoon.Tests.Runtime): fixed 12 px rows, a window of whole
    /// rows (no clipped half rows, so no masks), drag scrolling that moves by whole rows, the scrollbar thumb,
    /// the starting scroll that shows the player's own row.
    /// </summary>
    public static class LeaderboardMath
    {
        public const int RowH = 12;
        /// <summary>Shortest scrollbar thumb (px).</summary>
        public const int MinThumb = 6;

        /// <summary>Whole rows that fit a viewport of <paramref name="viewportH"/> px (at least 1).</summary>
        public static int VisibleRows(int viewportH)
        {
            int n = viewportH / RowH;
            return n < 1 ? 1 : n;
        }

        /// <summary>Largest first-row index that still fills the window (0 when everything fits).</summary>
        public static int MaxFirst(int count, int visible)
        {
            int m = count - visible;
            return m > 0 ? m : 0;
        }

        public static int ClampFirst(int first, int count, int visible)
        {
            int max = MaxFirst(count, visible);
            if (first > max) first = max;
            return first < 0 ? 0 : first;
        }

        /// <summary>
        /// Rows moved by a drag of <paramref name="dragUpPx"/> canvas px (finger moved up = positive = later rows),
        /// rounded to the nearest whole row, half a row away from zero.
        /// </summary>
        public static int RowsForDrag(int dragUpPx)
        {
            int half = RowH / 2;
            return dragUpPx >= 0 ? (dragUpPx + half) / RowH : -((-dragUpPx + half) / RowH);
        }

        public static int FirstAfterDrag(int startFirst, int dragUpPx, int count, int visible)
        {
            return ClampFirst(startFirst + RowsForDrag(dragUpPx), count, visible);
        }

        /// <summary>Canvas y of window slot <paramref name="slot"/> (0 = top row).</summary>
        public static int RowTop(int viewportTop, int slot) { return viewportTop + slot * RowH; }

        /// <summary>Window slot that shows list row <paramref name="index"/>, or -1 when it is scrolled away.</summary>
        public static int SlotOf(int index, int first, int visible)
        {
            int s = index - first;
            return index >= 0 && s >= 0 && s < visible ? s : -1;
        }

        /// <summary>
        /// Opening scroll: the top of the list, or, when the player's own row (<paramref name="youIndex"/>) would be
        /// below the window, the window that centres it.
        /// </summary>
        public static int InitialFirst(int youIndex, int count, int visible)
        {
            if (youIndex < visible) return 0;
            return ClampFirst(youIndex - visible / 2, count, visible);
        }

        /// <summary>Scrollbar thumb in a track of <paramref name="trackH"/> px (whole pixels, never outside the track).</summary>
        public static void Thumb(int trackTop, int trackH, int count, int visible, int first, out int thumbTop, out int thumbH)
        {
            int max = MaxFirst(count, visible);
            if (max == 0 || trackH <= 0)
            {
                thumbTop = trackTop;
                thumbH = trackH;
                return;
            }
            thumbH = trackH * visible / count;
            if (thumbH < MinThumb) thumbH = MinThumb;
            if (thumbH > trackH) thumbH = trackH;
            int f = ClampFirst(first, count, visible);
            thumbTop = trackTop + (trackH - thumbH) * f / max;
        }
    }
}
