using System;
using System.Collections.Generic;
using System.IO;
using PawNoon.Runtime;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PawNoon.EditorTools
{
    /// <summary>"Paw Noon/..." menu: open/play, builds and review screenshot capture.</summary>
    public static class PawMenu
    {
        [MenuItem("Paw Noon/Open Main Scene", false, 1)]
        public static void OpenMain()
        {
            if (!File.Exists(PawProjectSetup.ScenePath)) PawProjectSetup.Configure();
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                EditorSceneManager.OpenScene(PawProjectSetup.ScenePath, OpenSceneMode.Single);
        }

        [MenuItem("Paw Noon/Play", false, 2)]
        public static void Play()
        {
            if (EditorApplication.isPlaying) { EditorApplication.isPlaying = false; return; }
            OpenMain();
            EditorApplication.isPlaying = true;
        }

        [MenuItem("Paw Noon/Build iOS Xcode Project", false, 20)]
        public static void BuildIos()
        {
            var prev = PlayerSettings.iOS.sdkVersion;
            PlayerSettings.iOS.sdkVersion = iOSSdkVersion.DeviceSDK;
            Build(BuildTarget.iOS, BuildTargetGroup.iOS, "Builds/iOS", BuildOptions.None);
            PlayerSettings.iOS.sdkVersion = prev;
        }

        [MenuItem("Paw Noon/Build iOS Simulator", false, 21)]
        public static void BuildIosSimulator()
        {
            var prev = PlayerSettings.iOS.sdkVersion;
            PlayerSettings.iOS.sdkVersion = iOSSdkVersion.SimulatorSDK;
            try { Build(BuildTarget.iOS, BuildTargetGroup.iOS, "Builds/iOSSimulator", BuildOptions.None); }
            finally { PlayerSettings.iOS.sdkVersion = prev; }
        }

        [MenuItem("Paw Noon/Build Android APK", false, 22)]
        public static void BuildAndroid()
        {
            EditorUserBuildSettings.buildAppBundle = false;
            Directory.CreateDirectory("Builds/Android");
            Build(BuildTarget.Android, BuildTargetGroup.Android, "Builds/Android/PawNoon.apk", BuildOptions.None);
        }

        /// <summary>Builds the APK and installs + launches it on a USB-connected phone (USB debugging on).</summary>
        [MenuItem("Paw Noon/Build And Run Android (USB phone)", false, 23)]
        public static void BuildAndRunAndroid()
        {
            EditorUserBuildSettings.buildAppBundle = false;
            Directory.CreateDirectory("Builds/Android");
            Build(BuildTarget.Android, BuildTargetGroup.Android, "Builds/Android/PawNoon.apk", BuildOptions.AutoRunPlayer);
        }

        /// <summary>Command line: Unity -batchmode -quit -projectPath . -executeMethod PawNoon.EditorTools.PawMenu.BuildAndroidBatch</summary>
        public static void BuildAndroidBatch()
        {
            PawProjectSetup.Configure();
            EditorUserBuildSettings.buildAppBundle = false;
            Directory.CreateDirectory("Builds/Android");
            bool ok = Build(BuildTarget.Android, BuildTargetGroup.Android, "Builds/Android/PawNoon.apk", BuildOptions.None);
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        private static bool Build(BuildTarget target, BuildTargetGroup group, string path, BuildOptions options)
        {
            if (!File.Exists(PawProjectSetup.ScenePath)) PawProjectSetup.Configure();
            var opts = new BuildPlayerOptions
            {
                scenes = new[] { PawProjectSetup.ScenePath },
                locationPathName = path,
                target = target,
                targetGroup = group,
                options = options
            };
            var report = BuildPipeline.BuildPlayer(opts);
            var sum = report.summary;
            if (sum.result == BuildResult.Succeeded)
                Debug.Log("[PawNoon] Build succeeded: " + path + " (" + (sum.totalSize / (1024 * 1024)) + " MB)");
            else
                Debug.LogError("[PawNoon] Build " + sum.result + " for " + target + " (" + sum.totalErrors + " errors)");
            return sum.result == BuildResult.Succeeded;
        }

        // ------------------------------------------------------------------ review screenshots

        private static readonly Queue<string> captureQueue = new Queue<string>();
        private static double nextAt;
        private static string pending;
        private static readonly List<string> captured = new List<string>();
        private const int CaptureW = 1179;
        private const int CaptureH = 2556;

        [MenuItem("Paw Noon/Capture Review Screenshots", false, 40)]
        public static void CaptureReview()
        {
            if (!EditorApplication.isPlaying || PawGame.Instance == null)
            {
                EditorUtility.DisplayDialog("Paw Noon", "Enter Play mode first (Paw Noon/Play), then run Capture Review Screenshots.", "OK");
                return;
            }
            captureQueue.Clear();
            captured.Clear();
            for (int i = 0; i < ReviewDirector.States.Length; i++) captureQueue.Enqueue(ReviewDirector.States[i]);
            pending = null;
            nextAt = EditorApplication.timeSinceStartup + 0.2;
            EditorApplication.update -= CaptureStep;
            EditorApplication.update += CaptureStep;
        }

        /// <summary>All captured stills side by side (4 per row, 4x box-downsampled) in Docs/Screens/contact_sheet.png.</summary>
        private static void WriteContactSheet()
        {
            if (captured.Count == 0) return;
            const int Down = 4, Cols = 4, Gap = 6;
            int tw = CaptureW / Down, th = CaptureH / Down;
            int rows = (captured.Count + Cols - 1) / Cols;
            int sw = Cols * tw + (Cols + 1) * Gap, sh = rows * th + (rows + 1) * Gap;
            var sheet = new Color32[sw * sh];
            var bg = new Color32(26, 16, 32, 255);
            for (int i = 0; i < sheet.Length; i++) sheet[i] = bg;
            for (int n = 0; n < captured.Count; n++)
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                try
                {
                    if (!tex.LoadImage(File.ReadAllBytes(captured[n]))) continue;
                    var px = tex.GetPixels32();
                    int w = tex.width, h = tex.height;
                    int ox = Gap + (n % Cols) * (tw + Gap);
                    int oyTop = Gap + (n / Cols) * (th + Gap); // rows counted from the top of the sheet
                    for (int y = 0; y < th; y++)
                        for (int x = 0; x < tw; x++)
                        {
                            int r = 0, g = 0, b = 0, cnt = 0;
                            for (int dy = 0; dy < Down; dy++)
                                for (int dx = 0; dx < Down; dx++)
                                {
                                    int sx = x * Down + dx, sy = y * Down + dy;
                                    if (sx >= w || sy >= h) continue;
                                    var c = px[sy * w + sx];
                                    r += c.r; g += c.g; b += c.b; cnt++;
                                }
                            if (cnt == 0) continue;
                            // PNG pixel rows are bottom-up: row y of the thumb (bottom-up) lands at sheet row (bottom-up)
                            int sheetY = (sh - 1) - (oyTop + (th - 1 - y));
                            sheet[sheetY * sw + ox + x] = new Color32((byte)(r / cnt), (byte)(g / cnt), (byte)(b / cnt), 255);
                        }
                }
                finally { UnityEngine.Object.DestroyImmediate(tex); }
            }
            var outTex = new Texture2D(sw, sh, TextureFormat.RGBA32, false);
            outTex.SetPixels32(sheet);
            outTex.Apply(false, false);
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "../Docs/Screens/contact_sheet.png"));
            File.WriteAllBytes(path, outTex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(outTex);
            Debug.Log("[PawNoon] contact sheet " + path);
        }

        [MenuItem("Paw Noon/Open Builds Folder", false, 24)]
        public static void OpenBuildsFolder()
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Builds"));
            Directory.CreateDirectory(dir);
            EditorUtility.RevealInFinder(dir);
        }

        private static void CaptureStep()
        {
            if (!EditorApplication.isPlaying || PawGame.Instance == null)
            {
                EditorApplication.update -= CaptureStep;
                return;
            }
            if (EditorApplication.timeSinceStartup < nextAt) return;

            if (pending != null)
            {
                try
                {
                    string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Docs/Screens"));
                    Directory.CreateDirectory(dir);
                    var png = PawGame.Instance.Pipeline.CapturePng(CaptureW, CaptureH);
                    string file = Path.Combine(dir, pending + ".png");
                    File.WriteAllBytes(file, png);
                    captured.Add(file);
                    Debug.Log("[PawNoon] captured " + file);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[PawNoon] capture of " + pending + " failed: " + e.Message);
                }
                pending = null;
            }

            if (captureQueue.Count == 0)
            {
                EditorApplication.update -= CaptureStep;
                ReviewDirector.Show("title");
                try { WriteContactSheet(); }
                catch (Exception e) { Debug.LogWarning("[PawNoon] contact sheet failed: " + e.Message); }
                Debug.Log("[PawNoon] review screenshots done (Docs/Screens/).");
                return;
            }
            pending = captureQueue.Dequeue();
            Debug.Log("[PawNoon] " + ReviewDirector.Show(pending));
            nextAt = EditorApplication.timeSinceStartup + 0.9; // let pops/stamps settle
        }
    }
}
