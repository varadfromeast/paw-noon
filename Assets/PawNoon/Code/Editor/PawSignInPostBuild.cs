#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEngine;

namespace PawNoon.EditorTools
{
    /// <summary>
    /// M6: after an iOS build, adds the "Sign in with Apple" capability (entitlement
    /// com.apple.developer.applesignin = [Default]) to the Unity-iPhone target and links AuthenticationServices
    /// (also declared by Plugins/iOS/PawSignIn.mm.meta). The App ID must have the capability too (Docs/ACCOUNTS.md).
    /// Google on iOS needs nothing here: ASWebAuthenticationSession handles its redirect scheme itself.
    /// </summary>
    public static class PawSignInPostBuild
    {
        private const string Entitlements = "Unity-iPhone/PawNoon.entitlements";

        [PostProcessBuild(900)]
        public static void OnPostprocessBuild(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS) return;
            string projPath = PBXProject.GetPBXProjectPath(path);
            var proj = new PBXProject();
            proj.ReadFromFile(projPath);
            string main = proj.GetUnityMainTargetGuid();
            string framework = proj.GetUnityFrameworkTargetGuid();
            proj.AddFrameworkToProject(framework, "AuthenticationServices.framework", false);
            // keep an entitlements file another script may already have set
            string existing = proj.GetBuildPropertyForAnyConfig(main, "CODE_SIGN_ENTITLEMENTS");
            string ent = string.IsNullOrEmpty(existing) ? Entitlements : existing;
            proj.WriteToFile(projPath);

            var caps = new ProjectCapabilityManager(projPath, ent, null, main);
            caps.AddSignInWithApple();
            caps.WriteToFile();
            Debug.Log("[PawNoon] iOS: Sign in with Apple capability added (" + ent + ")");
        }
    }
}
#endif
