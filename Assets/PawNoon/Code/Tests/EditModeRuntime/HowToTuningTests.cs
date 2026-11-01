using System.IO;
using NUnit.Framework;
using PawNoon.Runtime;

namespace PawNoon.Tests
{
    /// <summary>HOW TO DUEL comic tunables (feel_config.json howTo*, Docs/TUTORIAL.md).</summary>
    public class HowToTuningTests
    {
        [Test]
        public void DefaultsWhenMissing()
        {
            var t = FeelConfig.Load("{}").HowTo;
            Assert.AreEqual(3.3f, t.beatSeconds, 1e-6f);
            Assert.AreEqual(14, t.slamPx);
            Assert.AreEqual(1, t.flashFrames);
            Assert.AreEqual(214, t.reactionMs);
            Assert.GreaterOrEqual(t.drawLoopSeconds, t.drawAtSeconds + t.reactionMs / 1000f + 1.6f);
        }

        [Test]
        public void ParsesAndClamps()
        {
            var t = FeelConfig.Load("{\"howToBeatSeconds\": 0.2, \"howToSlamPx\": 99, \"howToFlashFrames\": -1, \"howToReactionMs\": 300, \"howToDrawAtSeconds\": 1.0, \"howToDrawLoopSeconds\": 2.4}").HowTo;
            Assert.AreEqual(1.5f, t.beatSeconds, 1e-6f);
            Assert.AreEqual(40, t.slamPx);
            Assert.AreEqual(0, t.flashFrames);
            Assert.AreEqual(300, t.reactionMs);
            Assert.AreEqual(1.0f + 0.3f + 1.6f, t.drawLoopSeconds, 1e-5f, "the loop always holds DRAW, the shot and the hat landing");
        }

        [Test]
        public void ShippedConfigParses()
        {
            var t = FeelConfig.Load(File.ReadAllText("Assets/PawNoon/Resources/Data/feel_config.json")).HowTo;
            Assert.Greater(t.beatSeconds, 1f);
            Assert.Greater(t.drawLoopSeconds, t.drawAtSeconds);
        }
    }
}
