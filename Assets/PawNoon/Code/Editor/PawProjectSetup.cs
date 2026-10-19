using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PawNoon.EditorTools
{
    /// <summary>
    /// One-time project configuration on editor load. Re-runs when ConfigVersion is bumped (marker in
    /// Library/, so a fresh checkout configures itself once) or via "Paw Noon/Reconfigure Project".
    /// </summary>
    [InitializeOnLoad]
    public static class PawProjectSetup
    {
        public const int ConfigVersion = 3;
        public const string ScenePath = "Assets/PawNoon/Scenes/Main.unity";
        public const string IconPath = "Assets/PawNoon/Art/AppIcon/icon_1024.png";
        public const string BundleId = "com.pawnoon.pixel";
        private const string MarkerPath = "Library/PawNoon/config_version.txt";

        static PawProjectSetup()
        {
            EditorApplication.delayCall += AutoRun;
        }

        private static void AutoRun()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer) return;
            int done = 0;
            try
            {
                if (File.Exists(MarkerPath)) int.TryParse(File.ReadAllText(MarkerPath).Trim(), out done);
            }
            catch (Exception) { done = 0; }
            if (done >= ConfigVersion) return;
            Configure();
        }

        [MenuItem("Paw Noon/Reconfigure Project", false, 100)]
        public static void Configure()
        {
            Step("player settings", ConfigurePlayer);
            Step("app icon", ConfigureIcon);
            Step("main scene", EnsureScene);
            Step("input system package", EnsureInputSystem);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath));
                File.WriteAllText(MarkerPath, ConfigVersion.ToString());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PawNoon] could not write config marker: " + e.Message);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[PawNoon] Project configured (v" + ConfigVersion + ").");
        }

        private static void Step(string name, Action a)
        {
            try { a(); }
            catch (Exception e) { Debug.LogWarning("[PawNoon] setup step '" + name + "' failed: " + e.Message); }
        }

        private static void ConfigurePlayer()
        {
            PlayerSettings.companyName = "Paw Noon";
            PlayerSettings.productName = "Paw Noon";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, BundleId);
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, BundleId);
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, BundleId);

            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = false;
            PlayerSettings.allowedAutorotateToLandscapeRight = false;

            PlayerSettings.iOS.targetOSVersionString = "15.0";
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.iOS, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)26;
            PlayerSettings.colorSpace = ColorSpace.Gamma;
            PlayerSettings.statusBarHidden = true;

            try { PlayerSettings.SplashScreen.show = false; }
            catch (Exception e) { Debug.LogWarning("[PawNoon] splash screen could not be disabled: " + e.Message); }
        }

        private static void ConfigureIcon()
        {
            var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath);
            if (icon == null)
            {
                Debug.Log("[PawNoon] no app icon at " + IconPath + " yet (skipped).");
                return;
            }
            PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { icon }, IconKind.Any);
        }

        private static void EnsureScene()
        {
            if (!File.Exists(ScenePath))
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene, ScenePath);
                AssetDatabase.Refresh();
            }
            var list = EditorBuildSettings.scenes;
            bool ok = list.Length > 0 && list[0].path == ScenePath && list[0].enabled;
            if (!ok) EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        private static void EnsureInputSystem()
        {
            string manifest = "Packages/manifest.json";
            if (File.Exists(manifest) && File.ReadAllText(manifest).Contains("\"com.unity.inputsystem\"")) return;
            Debug.Log("[PawNoon] Adding com.unity.inputsystem via Package Manager...");
            UnityEditor.PackageManager.Client.Add("com.unity.inputsystem");
        }
    }
}
