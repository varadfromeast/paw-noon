#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace PawNoon.EditorTools
{
    /// <summary>
    /// iOS: the share sheet (Plugins/iOS/PawShare.mm) offers "Save Image", which iOS only allows when Info.plist
    /// carries NSPhotoLibraryAddUsageDescription (without it the app is terminated on that action).
    /// </summary>
    public static class PawSharePostBuild
    {
        private const string Key = "NSPhotoLibraryAddUsageDescription";
        private const string Reason = "Save your Paw Noon duel card to Photos.";

        [PostProcessBuild(100)]
        public static void OnPostprocessBuild(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS) return;
            string plistPath = Path.Combine(path, "Info.plist");
            if (!File.Exists(plistPath)) return;
            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);
            if (!plist.root.values.ContainsKey(Key)) plist.root.SetString(Key, Reason);
            plist.WriteToFile(plistPath);
        }
    }
}
#endif
