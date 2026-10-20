using NUnit.Framework;
using PawNoon.Runtime;

namespace PawNoon.Tests
{
    /// <summary>M5 leaderboard list: whole 12 px rows, whole-row drag scrolling, clamping, scrollbar thumb, opening scroll.</summary>
    public class LeaderboardMathTests
    {
        private const int Row = LeaderboardMath.RowH;

        [Test]
        public void OnlyWholeRowsFitTheViewport()
        {
            Assert.AreEqual(12, Row);
            Assert.AreEqual(20, LeaderboardMath.VisibleRows(240));
            Assert.AreEqual(19, LeaderboardMath.VisibleRows(239), "a half row is never shown");
            Assert.AreEqual(1, LeaderboardMath.VisibleRows(5), "at least one row");
            Assert.AreEqual(1, LeaderboardMath.VisibleRows(0));
        }

        [Test]
        public void RowsSitOnWholePixelsAndStack()
        {
            Assert.AreEqual(100, LeaderboardMath.RowTop(100, 0));
            Assert.AreEqual(100 + 5 * Row, LeaderboardMath.RowTop(100, 5));
        }

        [Test]
        public void FirstRowIsClampedToTheList()
        {
            Assert.AreEqual(30, LeaderboardMath.MaxFirst(50, 20));
            Assert.AreEqual(0, LeaderboardMath.MaxFirst(12, 20), "everything fits");
            Assert.AreEqual(0, LeaderboardMath.ClampFirst(-3, 50, 20));
            Assert.AreEqual(30, LeaderboardMath.ClampFirst(99, 50, 20));
            Assert.AreEqual(7, LeaderboardMath.ClampFirst(7, 50, 20));
            Assert.AreEqual(0, LeaderboardMath.ClampFirst(5, 0, 20), "empty list");
        }

        [Test]
        public void DragMovesByWholeRowsRoundedToTheNearest()
        {
            Assert.AreEqual(0, LeaderboardMath.RowsForDrag(0));
            Assert.AreEqual(0, LeaderboardMath.RowsForDrag(5));
            Assert.AreEqual(1, LeaderboardMath.RowsForDrag(6), "half a row rounds away from zero");
            Assert.AreEqual(1, LeaderboardMath.RowsForDrag(17));
            Assert.AreEqual(2, LeaderboardMath.RowsForDrag(18));
            Assert.AreEqual(0, LeaderboardMath.RowsForDrag(-5));
            Assert.AreEqual(-1, LeaderboardMath.RowsForDrag(-6), "symmetric for a downward drag");
            Assert.AreEqual(-2, LeaderboardMath.RowsForDrag(-20));
            // finger up 3 rows from the top: rows 3.. are shown; then back down past the top clamps at 0
            Assert.AreEqual(3, LeaderboardMath.FirstAfterDrag(0, 3 * Row, 50, 20));
            Assert.AreEqual(0, LeaderboardMath.FirstAfterDrag(3, -10 * Row, 50, 20));
            Assert.AreEqual(30, LeaderboardMath.FirstAfterDrag(25, 40 * Row, 50, 20), "clamped at the end");
            // monotonic in the drag distance
            int prev = -1;
            for (int d = -100; d <= 800; d++)
            {
                int f = LeaderboardMath.FirstAfterDrag(10, d, 50, 20);
                Assert.GreaterOrEqual(f, prev);
                prev = f;
            }
        }

        [Test]
        public void SlotOfAListRow()
        {
            Assert.AreEqual(0, LeaderboardMath.SlotOf(10, 10, 20));
            Assert.AreEqual(19, LeaderboardMath.SlotOf(29, 10, 20));
            Assert.AreEqual(-1, LeaderboardMath.SlotOf(30, 10, 20), "below the window");
            Assert.AreEqual(-1, LeaderboardMath.SlotOf(9, 10, 20), "above the window");
            Assert.AreEqual(-1, LeaderboardMath.SlotOf(-1, 0, 20), "not in the list");
        }

        [Test]
        public void OpeningScrollShowsTheTopOrCentresYourRow()
        {
            Assert.AreEqual(0, LeaderboardMath.InitialFirst(-1, 50, 20), "not in the top 50");
            Assert.AreEqual(0, LeaderboardMath.InitialFirst(11, 50, 20), "already visible from the top");
            Assert.AreEqual(25, LeaderboardMath.InitialFirst(35, 50, 20), "centred");
            Assert.AreEqual(30, LeaderboardMath.InitialFirst(49, 50, 20), "clamped at the end");
        }

        [Test]
        public void ThumbStaysInTheTrackAndMovesWithTheList()
        {
            int top, h;
            LeaderboardMath.Thumb(100, 240, 12, 20, 0, out top, out h);
            Assert.AreEqual(100, top, "no scrolling: thumb fills the track");
            Assert.AreEqual(240, h);
            LeaderboardMath.Thumb(100, 240, 50, 20, 0, out top, out h);
            Assert.AreEqual(100, top);
            Assert.AreEqual(96, h, "240 * 20 / 50");
            LeaderboardMath.Thumb(100, 240, 50, 20, 30, out top, out h);
            Assert.AreEqual(100 + 240 - 96, top, "at the end the thumb touches the bottom");
            LeaderboardMath.Thumb(100, 240, 5000, 20, 0, out top, out h);
            Assert.AreEqual(LeaderboardMath.MinThumb, h, "never thinner than the minimum");
            int prev = -1;
            for (int f = 0; f <= 30; f++)
            {
                LeaderboardMath.Thumb(100, 240, 50, 20, f, out top, out h);
                Assert.GreaterOrEqual(top, prev);
                Assert.GreaterOrEqual(top, 100);
                Assert.LessOrEqual(top + h, 340);
                prev = top;
            }
        }
    }
}
