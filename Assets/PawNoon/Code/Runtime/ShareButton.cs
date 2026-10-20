using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// The small SHARE icon button (btn_icon + ico_share, 30x30) on the round card and the match result screen.
    /// A click renders the share card (ShareService) and opens the share sheet; while the sheet is likely up the
    /// button reports <see cref="Holding"/> so the round card does not auto-advance under it.
    /// </summary>
    public sealed class ShareButton
    {
        public const int Size = 30;
        /// <summary>Seconds the round card stays after a share tap (the player taps to continue when back).</summary>
        public const float HoldSeconds = 20f;

        private readonly PixelButton button;
        private bool wanted = true;
        private float holdUntil = -1f;

        public ShareButton(UiKit kit, Transform parent, int order)
        {
            if (ShareService.Kit == null) ShareService.Kit = kit;
            button = new PixelButton(kit, parent, "btn_icon", "btn_icon_pressed", null, "ico_share", new RectInt(0, 0, Size, Size), order);
            Apply();
        }

        public RectInt Rect { get { return button.Rect; } }

        public void PlaceAt(int x, int y) { button.SetRect(new RectInt(x, y, Size, Size)); }

        public void SetVisible(bool v)
        {
            wanted = v;
            Apply();
        }

        private void Apply()
        {
            bool v = wanted && FeelConfig.Current.shareEnabled;
            if (button.Visible != v) button.SetVisible(v);
        }

        public bool Holding { get { return holdUntil > 0f && Time.realtimeSinceStartup < holdUntil; } }

        public void ReleaseHold() { holdUntil = -1f; }

        /// <summary>
        /// Call every frame while the screen is up, before any "tap anywhere" handling (the button consumes its
        /// press). Returns true when the card was shared. winner: match share only (0 you, 1 rival, -1 standoff).
        /// </summary>
        public bool Tick(ShareKind kind, int winner)
        {
            if (!button.Visible || !button.Tick()) return false;
            holdUntil = Time.realtimeSinceStartup + HoldSeconds;
            var d = kind == ShareKind.Match ? ShareLog.ForMatch(winner) : ShareLog.ForRound();
            return ShareService.Share(d) != null;
        }
    }
}
