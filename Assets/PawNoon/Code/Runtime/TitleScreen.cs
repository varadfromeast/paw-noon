using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    public sealed class TitleScreen : ScreenBase
    {
        // EASY / HARD segmented toggle under PLAY (same seg sprites as the settings music-style row)
        private const int SegW = 48;
        private const int SegH = 20;
        private const int SegGap = 2;
        // first-launch choice card (above the title, below settings)
        private const int CO = 270;
        private const int CardW = 196;
        private const int CardH = 136;
        private const int CardBtnW = 164;
        private const int CardBtnH = 30;

        private SpriteRenderer logo;
        private SheetDef logoDef;
        private PixelButton play;
        private PixelButton gear;
        private PixelButton profileBtn;   // M6: profile card (left of the gear)
        private PixelButton trophy;       // M5: leaderboards (left of the profile button)
        // online (M4): PVP under PLAY (same wood sign); FRIEND beside it only when net_config "friendDuel" is on
        private const int PvpH = 30;
        private PixelButton pvp;
        private PixelButton friend;
        private bool friendShown;
        private bool onlineBusy;
        private BitmapText best;
        private float t;

        private Transform diffRoot;
        private SpriteRenderer segEasy, segHard;
        private BitmapText segEasyText, segHardText;
        private RectInt segEasyRect, segHardRect;

        private Transform choiceRoot;
        private SpriteRenderer choiceDim;
        private SpriteRenderer choicePanel;
        private BitmapText choiceTitle, choiceSub, easyDesc, hardDesc;
        private PixelButton chooseEasy, chooseHard;
        private RectInt choiceRect;
        private bool choiceOpen;
        private bool forceChoice;
        private int bountyTop;
        private bool controlsShown;

        /// <summary>Canvas y of the daily bounty strip's top (just under BEST; feel wave 2).</summary>
        public int BountyTop { get { RelayoutIfNeeded(); return bountyTop; } }

        /// <summary>PLAY / PVP / BEST are showing (no first-launch card, no online card over the title).</summary>
        public bool ControlsShown { get { return IsOpen && controlsShown; } }

        /// <summary>True while the one-time EASY / HARD card is up (PLAY waits for it).</summary>
        public bool ChoiceOpen { get { return choiceOpen; } }

        protected override void Build()
        {
            // prefer the generated logo_v3 when it has been dropped into Art/UI
            logoDef = kit.Ui.TryGet("logo_v3") ?? kit.Ui.Get("logo");
            logo = PawGfx.NewSprite("Logo", transform, 240);
            logo.sprite = logoDef.First;
            // wooden sign button; one sprite for both states, the label sinks 1 px when pressed
            play = new PixelButton(kit, transform, "panel_wood", "panel_wood", "PLAY", null, new RectInt(0, 0, 152, 36), 240);
            gear = new PixelButton(kit, transform, "btn_icon", "btn_icon_pressed", null, "ico_gear", new RectInt(0, 0, 30, 30), 240);
            profileBtn = new PixelButton(kit, transform, "btn_icon", "btn_icon_pressed", null, "ico_profile", new RectInt(0, 0, 30, 30), 240);
            trophy = new PixelButton(kit, transform, "btn_icon", "btn_icon_pressed", null, "ico_trophy", new RectInt(0, 0, 30, 30), 240);
            pvp = new PixelButton(kit, transform, "panel_wood", "panel_wood", "PVP", null, new RectInt(0, 0, 152, PvpH), 240);
            friend = new PixelButton(kit, transform, "panel_wood", "panel_wood", "FRIEND", null, new RectInt(0, 0, 56, PvpH), 240);
            best = LightText("Best", kit.Medium, 242, TextAlign.Center);
            best.SetColors(Pal.Sun1, Pal.Ink, Pal.Ink2);

            diffRoot = new GameObject("Difficulty").transform;
            diffRoot.SetParent(transform, false);
            segEasy = PawGfx.NewSprite("SegEasy", diffRoot, 240);
            segHard = PawGfx.NewSprite("SegHard", diffRoot, 240);
            segEasyText = BitmapText.Create("SegEasyT", diffRoot, kit.Small, 242, TextAlign.Center);
            segEasyText.SetColors(Pal.Cream0, Pal.Ink, Pal.Ink2);
            segEasyText.SetText("EASY");
            segHardText = BitmapText.Create("SegHardT", diffRoot, kit.Small, 242, TextAlign.Center);
            segHardText.SetColors(Pal.Cream0, Pal.Ink, Pal.Ink2);
            segHardText.SetText("HARD");

            choiceRoot = new GameObject("DifficultyChoice").transform;
            choiceRoot.SetParent(transform, false);
            choiceDim = PawGfx.NewSprite("Dim", choiceRoot, CO - 2);
            choiceDim.sprite = ArtLib.WhitePixel;
            choiceDim.color = new Color32(Pal.Ink.r, Pal.Ink.g, Pal.Ink.b, 150);
            choicePanel = kit.Panel("panel_wood", choiceRoot, CO, new RectInt(0, 0, CardW, CardH));
            choiceTitle = BitmapText.Create("ChoiceTitle", choiceRoot, kit.Medium, CO + 3, TextAlign.Center);
            choiceTitle.SetColors(Pal.Brass0, Pal.Ink, Pal.Ink2);
            choiceTitle.SetText("PICK YOUR DUEL");
            choiceSub = BitmapText.Create("ChoiceSub", choiceRoot, kit.Small, CO + 3, TextAlign.Center);
            choiceSub.SetColors(Pal.Sand0, Pal.Ink, Pal.Ink2);
            choiceSub.SetText("Switch it any time on this screen.");
            chooseEasy = new PixelButton(kit, choiceRoot, "btn_large_alt", "btn_large_alt_pressed", "EASY", null, new RectInt(0, 0, CardBtnW, CardBtnH), CO + 2);
            chooseHard = new PixelButton(kit, choiceRoot, "btn_large", "btn_large_pressed", "HARD", null, new RectInt(0, 0, CardBtnW, CardBtnH), CO + 2);
            easyDesc = BitmapText.Create("EasyDesc", choiceRoot, kit.Small, CO + 3, TextAlign.Center);
            easyDesc.SetColors(Pal.Cream1, Pal.Ink, Pal.Ink2);
            easyDesc.SetText("Slower outlaw. Kinder timing.");
            hardDesc = BitmapText.Create("HardDesc", choiceRoot, kit.Small, CO + 3, TextAlign.Center);
            hardDesc.SetColors(Pal.Cream1, Pal.Ink, Pal.Ink2);
            hardDesc.SetText("The real high noon.");
            choiceRoot.gameObject.SetActive(false);
        }

        private int LogoY()
        {
            // upper third, clear of the Dynamic Island / status bar
            return Mathf.Max(layout.Logo.y, pipe.Safe.y + 40 + logoDef.Cell.y / 2);
        }

        protected override void Layout()
        {
            var safe = pipe.Safe;
            int cx = CenterX;
            PlaceCentered(logo, logoDef, cx, LogoY() + Mathf.RoundToInt(Mathf.Sin(t * 1.6f) * 1.5f));
            // PLAY lives in the thumb zone (bottom quarter of the safe area); PVP right under it, then the
            // difficulty toggle, BEST under the toggle, the daily bounty strip under BEST
            // (36 + 4 + 30 + 5 + 20 + 5 + 10 + 3 + 14 = 127 px, fits the 132 px reserved below playTop)
            int playTop = Mathf.Min(layout.PlayButton.y - 18, safe.yMax - 132);
            play.SetRect(new RectInt(cx - 76, playTop, 152, 36));
            int pvpY = playTop + 36 + 4;
            if (friendShown)
            {
                pvp.SetRect(new RectInt(cx - 76, pvpY, 92, PvpH));
                friend.SetRect(new RectInt(cx + 20, pvpY, 56, PvpH));
            }
            else pvp.SetRect(new RectInt(cx - 76, pvpY, 152, PvpH));
            int segY = pvpY + PvpH + 5;
            segEasyRect = new RectInt(cx - SegGap / 2 - SegW, segY, SegW, SegH);
            segHardRect = new RectInt(segEasyRect.xMax + SegGap, segY, SegW, SegH);
            int ty = segY + (SegH - kit.Small.LineHeight) / 2 + 1;
            segEasyText.At(segEasyRect.x + SegW / 2, ty);
            segHardText.At(segHardRect.x + SegW / 2, ty);
            best.At(cx, segY + SegH + 5);
            bountyTop = segY + SegH + 5 + kit.Medium.LineHeight + 3;
            gear.SetRect(new RectInt(safe.xMax - 4 - 30, safe.y + 2, 30, 30));
            profileBtn.SetRect(new RectInt(safe.xMax - 4 - 30 - 4 - 30, safe.y + 2, 30, 30));
            // M5 trophy left of the M6 profile button: [trophy][profile][gear], 30 px each, 4 px apart
            trophy.SetRect(new RectInt(safe.xMax - 4 - 30 - 4 - 30 - 4 - 30, safe.y + 2, 30, 30));
            RefreshDifficulty();
            LayoutChoice();
        }

        private void LayoutChoice()
        {
            var vis = pipe.Visible;
            var safe = pipe.Safe;
            choiceDim.transform.position = Px.World(vis.x, vis.yMax);
            choiceDim.transform.localScale = new Vector3(vis.width, vis.height, 1f);
            int w = Mathf.Min(CardW, vis.width - 16);
            int x = CenterX - w / 2;
            // thumb zone, where PLAY sits; never up over the logo
            int logoBottom = LogoY() + logoDef.Cell.y / 2 + 6;
            int y = Mathf.Max(logoBottom, safe.yMax - 12 - CardH);
            choiceRect = new RectInt(x, y, w, CardH);
            UiKit.SetRect(choicePanel, kit.Ui.Get("panel_wood"), choiceRect);
            int cx = x + w / 2;
            choiceTitle.At(cx, y + 11);
            choiceSub.At(cx, y + 24);
            int bw = Mathf.Min(CardBtnW, w - 20);
            chooseEasy.SetRect(new RectInt(cx - bw / 2, y + 38, bw, CardBtnH));
            easyDesc.At(cx, y + 38 + CardBtnH + 3);
            chooseHard.SetRect(new RectInt(cx - bw / 2, y + 84, bw, CardBtnH));
            hardDesc.At(cx, y + 84 + CardBtnH + 3);
        }

        private void PlaceSeg(SpriteRenderer sr, bool on, RectInt r)
        {
            var d = kit.Ui.TryGet(on ? "seg_on" : "seg_off");
            if (d == null) { sr.sprite = null; return; }
            sr.sprite = d.First;
            sr.drawMode = SpriteDrawMode.Sliced;
            UiKit.SetRect(sr, d, r);
        }

        /// <summary>Re-reads PawSettings.Difficulty into the toggle.</summary>
        public void RefreshDifficulty()
        {
            bool easy = PawSettings.Difficulty == DuelConfig.DifficultyEasy;
            PlaceSeg(segEasy, easy, segEasyRect);
            PlaceSeg(segHard, !easy, segHardRect);
            // the yellow segment marks the pick; labels stay light on both
            segEasyText.SetColors(easy ? Pal.Cream0 : Pal.Cream1, Pal.Ink, Pal.Ink2);
            segHardText.SetColors(!easy ? Pal.Cream0 : Pal.Cream1, Pal.Ink, Pal.Ink2);
            segEasyText.MarkDirty();
            segHardText.MarkDirty();
        }

        /// <summary>Review: open the title with the first-launch card even if a difficulty is stored.</summary>
        public void ForceChoice() { forceChoice = true; }

        public override void Open()
        {
            choiceOpen = forceChoice || !PawSettings.DifficultyChosen;
            forceChoice = false;
            base.Open();
            best.SetText(PawSettings.BestNs > 0 ? "BEST " + NumFmt.Seconds6(PawSettings.BestNs).Substring(0, 5) + " s" : "BEST --");
            ApplyChoiceVisibility();
        }

        private void ApplyChoiceVisibility()
        {
            choiceRoot.gameObject.SetActive(choiceOpen);
            bool controls = !choiceOpen && !onlineBusy;
            controlsShown = controls;
            play.SetVisible(controls);
            pvp.SetVisible(controls);
            friend.SetVisible(controls && friendShown);
            // PvP is always Hard: the EASY / HARD toggle is hidden while an online card is up
            diffRoot.gameObject.SetActive(controls);
            best.Visible = controls;
            gear.SetVisible(controls);
            profileBtn.SetVisible(controls && AccountsUi.Enabled);
            trophy.SetVisible(controls);
        }

        /// <summary>Shows the FRIEND button (room codes, M5) beside PVP. Off unless net_config.json "friendDuel" is true.</summary>
        public void SetFriendDuel(bool on)
        {
            if (friendShown == on) return;
            friendShown = on;
            Layout();
            ApplyChoiceVisibility();
        }

        /// <summary>While an online card (finding a rival, opponent, error) is over the title: hide its controls.</summary>
        public void SetOnlineBusy(bool busy)
        {
            if (onlineBusy == busy) return;
            onlineBusy = busy;
            ApplyChoiceVisibility();
        }

        /// <summary>Closes the first-launch card (after the game stored the pick).</summary>
        public void CloseChoice()
        {
            choiceOpen = false;
            RefreshDifficulty();
            ApplyChoiceVisibility();
        }

        public UiAction Tick(bool interactive)
        {
            RelayoutIfNeeded();
            t += PawTime.UnscaledDt;
            PlaceCentered(logo, logoDef, CenterX, LogoY() + Mathf.RoundToInt(Mathf.Sin(t * 1.6f) * 1.5f));
            if (!interactive) return UiAction.None;
            if (choiceOpen)
            {
                if (chooseEasy.Tick()) return UiAction.ChooseEasy;
                if (chooseHard.Tick()) return UiAction.ChooseHard;
                // the card is modal: swallow taps elsewhere
                if (UiPointer.Down) UiPointer.Consumed = true;
                return UiAction.None;
            }
            if (onlineBusy) return UiAction.None;
            if (gear.Tick()) return UiAction.OpenSettings;
            if (profileBtn.Tick()) return UiAction.OpenProfile;
            if (trophy.Tick()) return UiAction.Leaderboard;
            if (play.Tick()) return UiAction.Play;
            if (pvp.Tick()) return UiAction.Pvp;
            if (friend.Tick()) return UiAction.Friend;
            if (UiPointer.Down && !UiPointer.Consumed)
            {
                bool easy = PawSettings.Difficulty == DuelConfig.DifficultyEasy;
                if (UiPointer.In(segEasyRect))
                {
                    UiPointer.Consumed = true;
                    if (!easy) return Toggled(UiAction.ChooseEasy);
                }
                else if (UiPointer.In(segHardRect))
                {
                    UiPointer.Consumed = true;
                    if (easy) return Toggled(UiAction.ChooseHard);
                }
            }
            return UiAction.None;
        }

        private UiAction Toggled(UiAction a)
        {
            if (kit.Audio != null) kit.Audio.Play("sfx_ui_toggle");
            PawHaptics.Impact(0);
            return a;
        }
    }
}
