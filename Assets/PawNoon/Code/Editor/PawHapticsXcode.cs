#if UNITY_IOS
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace PawNoon.EditorTools
{
    /// <summary>
    /// iOS build: links CoreHaptics.framework into UnityFramework (Plugins/iOS/PawHaptics.mm uses CHHapticEngine).
    /// The plugin importer also lists it (FrameworkDependencies); this makes the link independent of the .meta.
    /// </summary>
    public static class PawHapticsXcode
    {
        [PostProcessBuild(100)]
        public static void OnPostprocessBuild(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS) return;
            string projPath = PBXProject.GetPBXProjectPath(path);
            var proj = new PBXProject();
            proj.ReadFromFile(projPath);
            string fw = proj.GetUnityFrameworkTargetGuid();
            if (!proj.ContainsFramework(fw, "CoreHaptics.framework")) proj.AddFrameworkToProject(fw, "CoreHaptics.framework", false);
            proj.WriteToFile(projPath);
        }
    }
}
#endif
