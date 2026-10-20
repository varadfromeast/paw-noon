using NUnit.Framework;
using PawNoon.Core;

namespace PawNoon.Tests
{
    /// <summary>M5 friend duels: room-code validation, the keypad entry buffer and the keypad's pixel geometry.</summary>
    public class RoomCodeTests
    {
        [Test]
        public void ValidCodesAreExactlyFourUpperCaseLetters()
        {
            Assert.IsTrue(RoomCode.IsValid("HOWL"));
            Assert.IsTrue(RoomCode.IsValid("ABIO"), "joining accepts every letter, I and O included");
            Assert.IsFalse(RoomCode.IsValid("howl"), "IsValid is strict; Normalize upper-cases");
            Assert.IsFalse(RoomCode.IsValid("HOW"));
            Assert.IsFalse(RoomCode.IsValid("HOWLS"));
            Assert.IsFalse(RoomCode.IsValid("H0WL"));
            Assert.IsFalse(RoomCode.IsValid("ÄBCD"));
            Assert.IsFalse(RoomCode.IsValid(null));
        }

        [Test]
        public void NormalizeUpperCasesAndDropsBlanksAndDashes()
        {
            Assert.AreEqual("HOWL", RoomCode.Normalize("howl"));
            Assert.AreEqual("HOWL", RoomCode.Normalize(" Ho-wL "));
            Assert.AreEqual("ABCD", RoomCode.Normalize("a b\tc-d"));
            Assert.IsNull(RoomCode.Normalize("abc"));
            Assert.IsNull(RoomCode.Normalize("abcde"));
            Assert.IsNull(RoomCode.Normalize("ab1d"));
            Assert.IsNull(RoomCode.Normalize("ab.d"));
            Assert.IsNull(RoomCode.Normalize(""));
            Assert.IsNull(RoomCode.Normalize(null));
        }

        [Test]
        public void EntryTypesFourLettersAndBackspaces()
        {
            var e = new RoomCodeEntry();
            Assert.IsTrue(e.Empty);
            Assert.IsNull(e.Code);
            Assert.IsTrue(e.Add('h'));
            Assert.IsTrue(e.Add('O'));
            Assert.IsFalse(e.Add('1'), "digits are not letters");
            Assert.IsFalse(e.Add(' '));
            Assert.AreEqual(2, e.Count);
            Assert.AreEqual('H', e[0]);
            Assert.AreEqual('\0', e[2], "past the typed slots");
            Assert.IsTrue(e.Add('w'));
            Assert.IsTrue(e.Add('L'));
            Assert.IsTrue(e.Complete);
            Assert.IsFalse(e.Add('X'), "full");
            Assert.AreEqual("HOWL", e.Code);
            Assert.AreSame(e.Code, e.Code, "the string is built once per change");
            Assert.IsTrue(e.Backspace());
            Assert.IsFalse(e.Complete);
            Assert.IsNull(e.Code);
            Assert.IsTrue(e.Add('k'));
            Assert.AreEqual("HOWK", e.Code);
            e.Clear();
            Assert.IsFalse(e.Backspace(), "nothing to delete");
            Assert.AreEqual(0, e.Count);
        }

        [Test]
        public void EntrySetTakesUpToFourLettersAndStopsAtAnythingElse()
        {
            var e = new RoomCodeEntry();
            e.Set("dustbowl");
            Assert.AreEqual("DUST", e.Code);
            e.Set("ab3cd");
            Assert.AreEqual(2, e.Count);
            e.Set(null);
            Assert.AreEqual(0, e.Count);
            Assert.AreSame(e.Chars, e.Chars, "the live buffer is not copied");
        }

        private const int KW = 22, KH = 20, G = 2, X0 = 100, Y0 = 300;

        [Test]
        public void KeypadHasTheAlphabetInSevenColumnsAndAWideDelete()
        {
            Assert.AreEqual(7 * 22 + 6 * 2, KeypadLayout.Width(KW, G));
            Assert.AreEqual(4 * 20 + 3 * 2, KeypadLayout.Height(KH, G));
            Assert.AreEqual('A', KeypadLayout.LetterAt(0));
            Assert.AreEqual('Z', KeypadLayout.LetterAt(25));
            Assert.AreEqual('\0', KeypadLayout.LetterAt(KeypadLayout.Delete));

            var a = KeypadLayout.KeyRect(0, X0, Y0, KW, KH, G);
            Assert.AreEqual(X0, a.x);
            Assert.AreEqual(Y0, a.y);
            var h = KeypadLayout.KeyRect('H' - 'A', X0, Y0, KW, KH, G);
            Assert.AreEqual(X0, h.x, "H starts row 2");
            Assert.AreEqual(Y0 + KH + G, h.y);
            var z = KeypadLayout.KeyRect(25, X0, Y0, KW, KH, G);
            Assert.AreEqual(X0 + 4 * (KW + G), z.x, "Z is the 5th key of the last row");
            Assert.AreEqual(Y0 + 3 * (KH + G), z.y);
            var del = KeypadLayout.KeyRect(KeypadLayout.Delete, X0, Y0, KW, KH, G);
            Assert.AreEqual(X0 + 5 * (KW + G), del.x);
            Assert.AreEqual(2 * KW + G, del.w, "two keys and the gap between them");
            Assert.AreEqual(X0 + KeypadLayout.Width(KW, G), del.xMax, "DEL ends flush with the right edge");
        }

        [Test]
        public void KeypadHitTestFindsEveryKeyAndIgnoresGaps()
        {
            for (int i = 0; i < KeypadLayout.KeyCount; i++)
            {
                var r = KeypadLayout.KeyRect(i, X0, Y0, KW, KH, G);
                Assert.AreEqual(i, KeypadLayout.HitTest(r.x, r.y, X0, Y0, KW, KH, G), "top-left of key " + i);
                Assert.AreEqual(i, KeypadLayout.HitTest(r.xMax - 1, r.yMax - 1, X0, Y0, KW, KH, G), "bottom-right of key " + i);
                Assert.AreEqual(i, KeypadLayout.HitTest(r.x + r.w / 2, r.y + r.h / 2, X0, Y0, KW, KH, G), "centre of key " + i);
            }
            Assert.AreEqual(-1, KeypadLayout.HitTest(X0 + KW, Y0 + 5, X0, Y0, KW, KH, G), "gap between A and B");
            Assert.AreEqual(-1, KeypadLayout.HitTest(X0 + 5, Y0 + KH, X0, Y0, KW, KH, G), "gap between rows");
            Assert.AreEqual(KeypadLayout.Delete, KeypadLayout.HitTest(X0 + 5 * (KW + G) + KW, Y0 + 3 * (KH + G) + 3, X0, Y0, KW, KH, G), "the gap inside DEL is DEL");
            Assert.AreEqual(-1, KeypadLayout.HitTest(X0 - 1, Y0, X0, Y0, KW, KH, G));
            Assert.AreEqual(-1, KeypadLayout.HitTest(X0, Y0 - 1, X0, Y0, KW, KH, G));
            Assert.AreEqual(-1, KeypadLayout.HitTest(X0 + KeypadLayout.Width(KW, G), Y0, X0, Y0, KW, KH, G));
            Assert.AreEqual(-1, KeypadLayout.HitTest(X0, Y0 + KeypadLayout.Height(KH, G), X0, Y0, KW, KH, G));
        }

        [Test]
        public void KeysNeverOverlap()
        {
            for (int i = 0; i < KeypadLayout.KeyCount; i++)
            for (int j = i + 1; j < KeypadLayout.KeyCount; j++)
            {
                var a = KeypadLayout.KeyRect(i, X0, Y0, KW, KH, G);
                var b = KeypadLayout.KeyRect(j, X0, Y0, KW, KH, G);
                bool overlap = a.x < b.xMax && b.x < a.xMax && a.y < b.yMax && b.y < a.yMax;
                Assert.IsFalse(overlap, i + " overlaps " + j);
            }
        }
    }
}
