using System.IO;
using NUnit.Framework;
using PawNoon.Runtime;

namespace PawNoon.Tests
{
    /// <summary>Outcome-moment tunables in feel_config.json (Docs/FEEL.md "Outcome moments").</summary>
    public class OutcomeFeelTests
    {
        [Test]
        public void DefaultsWhenMissing()
        {
            var c = FeelConfig.Load("{}");
            Assert.AreEqual(2, c.streakMin);
            Assert.AreEqual(3, c.streakRollMin);
            Assert.AreEqual(1, c.killPunchFlashFrames);
            Assert.AreEqual(2f, c.killPunchKickPx);
            Assert.Greater(c.confettiCount, 0);
            Assert.LessOrEqual(c.confettiCount, FeelConfig.ConfettiMax);
        }

        [Test]
        public void ParsesAndClamps()
        {
            var c = FeelConfig.Load("{\"streakMin\": 3, \"streakRollMin\": 0, \"confettiCount\": 999, \"confettiSeconds\": 0.01, \"killPunchKickPx\": -4}");
            Assert.AreEqual(3, c.streakMin);
            Assert.AreEqual(0, c.streakRollMin, "0 switches ON A ROLL / rattled off");
            Assert.AreEqual(FeelConfig.ConfettiMax, c.confettiCount, "the pool size caps the burst");
            Assert.AreEqual(0.2f, c.confettiSeconds, 1e-6f);
            Assert.AreEqual(0f, c.killPunchKickPx);
        }

        [Test]
        public void ShippedFileHasTheKeys()
        {
            string text = File.ReadAllText("Assets/PawNoon/Resources/Data/feel_config.json");
            foreach (var k in new[] { "streakMin", "streakRollMin", "streakTagDelay", "rollLabelSeconds", "newBestDelay", "confettiCount",
                                       "confettiSeconds", "confettiSpeed", "confettiGravity", "killPunchFlashFrames", "killPunchKickPx" })
                StringAssert.Contains("\"" + k + "\"", text, k);
            var c = FeelConfig.Load(text);
            Assert.AreEqual(2, c.streakMin);
            Assert.AreEqual(3, c.streakRollMin);
        }
    }
}
