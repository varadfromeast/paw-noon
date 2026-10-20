using System;
using UnityEngine;

namespace PawNoon.Runtime.Net
{
    /// <summary>
    /// Share seam for the room code (M5). A native plugin (iOS UIActivityViewController / Android ACTION_SEND
    /// chooser; not in this milestone, no external packages) sets <see cref="Handler"/> at startup; it returns
    /// true when the share sheet opened. Without one (Editor, today's builds) the text goes to the clipboard
    /// (<c>GUIUtility.systemCopyBuffer</c>) and the card says COPIED.
    /// </summary>
    public static class NativeShare
    {
        public enum Result { Shared, Copied, Failed }

        /// <summary>Native share sheet: text -> opened. Null until a plugin installs it.</summary>
        public static Func<string, bool> Handler;

        /// <summary>The last text handed out (review / tests).</summary>
        public static string LastText { get; private set; }

        public static Result Share(string text)
        {
            LastText = text;
            if (Handler != null)
            {
                try
                {
                    if (Handler(text)) return Result.Shared;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[PawNoon] native share failed, copying instead: " + e.Message);
                }
            }
            try
            {
                GUIUtility.systemCopyBuffer = text;
                return Result.Copied;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PawNoon] clipboard unavailable: " + e.Message);
                return Result.Failed;
            }
        }

        /// <summary>The message a friend receives.</summary>
        public static string RoomMessage(string code)
        {
            return "Duel me at high noon in Paw Noon! Friend duel room code: " + code + " (open for 10 minutes)";
        }
    }
}
