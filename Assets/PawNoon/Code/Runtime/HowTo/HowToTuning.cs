using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// HOW TO DUEL comic tunables (Resources/Data/feel_config.json, keys howTo*; Docs/TUTORIAL.md). Presentation only.
    /// </summary>
    public sealed class HowToTuning
    {
        /// <summary>Seconds a beat stays up before the comic turns the page by itself (beats 1 and 2; beat 3 waits for GOT IT).</summary>
        public float beatSeconds = 3.3f;
        /// <summary>Slam-in drop height (canvas px): the panel falls in whole-pixel steps, lands, bounces 2 px.</summary>
        public int slamPx = 14;
        /// <summary>Seconds per slam keyframe.</summary>
        public float slamStepSeconds = 0.04f;
        /// <summary>Frames of white over the scene when a panel lands, and on BANG (0 = off).</summary>
        public int flashFrames = 1;
        /// <summary>Comic shake amplitude (px) for JAM and BANG.</summary>
        public int shakePx = 2;
        /// <summary>Beat 2: scene time at which the thumb twitches and the gun jams.</summary>
        public float jamAtSeconds = 1.5f;
        /// <summary>Beat 3: scene time of the DRAW! pop.</summary>
        public float drawAtSeconds = 0.6f;
        /// <summary>Beat 3: the reaction the little timer counts to (ms); BANG lands this long after DRAW!.</summary>
        public int reactionMs = 214;
        /// <summary>Beat 3: the scene loops with this period while the player reads GOT IT.</summary>
        public float drawLoopSeconds = 3.6f;

        public static HowToTuning Read(object root)
        {
            var t = new HowToTuning();
            if (root == null) return t;
            t.beatSeconds = Mathf.Clamp((float)PnJson.Num(root, "howToBeatSeconds", t.beatSeconds), 1.5f, 10f);
            t.slamPx = Mathf.Clamp(PnJson.Int(root, "howToSlamPx", t.slamPx), 0, 40);
            t.slamStepSeconds = Mathf.Clamp((float)PnJson.Num(root, "howToSlamStepSeconds", t.slamStepSeconds), 0.016f, 0.2f);
            t.flashFrames = Mathf.Clamp(PnJson.Int(root, "howToFlashFrames", t.flashFrames), 0, 4);
            t.shakePx = Mathf.Clamp(PnJson.Int(root, "howToShakePx", t.shakePx), 0, 6);
            t.jamAtSeconds = Mathf.Clamp((float)PnJson.Num(root, "howToJamAtSeconds", t.jamAtSeconds), 0.6f, 2.4f);
            t.drawAtSeconds = Mathf.Clamp((float)PnJson.Num(root, "howToDrawAtSeconds", t.drawAtSeconds), 0.2f, 1.5f);
            t.reactionMs = Mathf.Clamp(PnJson.Int(root, "howToReactionMs", t.reactionMs), 100, 600);
            t.drawLoopSeconds = Mathf.Clamp((float)PnJson.Num(root, "howToDrawLoopSeconds", t.drawLoopSeconds), 2.4f, 8f);
            // the loop must hold the DRAW, the shot and the hat landing
            float minLoop = t.drawAtSeconds + t.reactionMs / 1000f + 1.6f;
            if (t.drawLoopSeconds < minLoop) t.drawLoopSeconds = minLoop;
            return t;
        }
    }
}
