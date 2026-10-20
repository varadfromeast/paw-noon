#if UNITY_IOS
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEngine;

namespace PawNoon.EditorTools
{
    /// <summary>
    /// Beta / personal-team iOS builds. When net_config.json has "accounts": false the build must sign with a FREE
    /// Apple ID ("Personal Team"), which cannot carry the In-App Purchase or Sign in with Apple capabilities.
    /// Runs last: drops the In-App Purchase capability flag Unity IAP adds, but keeps StoreKit.framework linked to
    /// UnityFramework (the IAP plugin's native code still references it; without it the link step fails).
    /// </summary>
    public static class PawBetaPostBuild
    {
        public static bool AccountsEnabled()
        {
            var ta = Resources.Load<TextAsset>("Data/net_config");
            if (ta == null) return true;
            return !Regex.IsMatch(ta.text, "\"accounts\"\\s*:\\s*false");
        }

        [PostProcessBuild(1000)]
        public static void OnPostprocessBuild(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS || AccountsEnabled()) return;
            string projPath = PBXProject.GetPBXProjectPath(path);
            var proj = new PBXProject();
            proj.ReadFromFile(projPath);
            proj.AddFrameworkToProject(proj.GetUnityFrameworkTargetGuid(), "StoreKit.framework", false);
            string text = proj.WriteToString();
            // "com.apple.InAppPurchase = { enabled = 1; };" inside TargetAttributes.SystemCapabilities
            text = Regex.Replace(text, "com\\.apple\\.InAppPurchase = \\{\\s*enabled = 1;\\s*\\};", "com.apple.InAppPurchase = { enabled = 0; };");
            File.WriteAllText(projPath, text);
            Debug.Log("[PawNoon] iOS beta build: In-App Purchase capability off, StoreKit kept linked (personal team signing)");
        }
    }
}
#endif
