using UnityEditor;
using UnityEngine;

namespace PawNoon.EditorTools
{
    /// <summary>Import rules for pixel art and audio (SPEC §7).</summary>
    public sealed class PawImportRules : AssetPostprocessor
    {
        private const string ArtRoot = "Assets/PawNoon/Resources/Art/";
        private const string IconRoot = "Assets/PawNoon/Art/AppIcon/";
        private const string AudioRoot = "Assets/PawNoon/Resources/Audio/";

        private void OnPreprocessTexture()
        {
            string p = assetPath.Replace('\\', '/');
            if (!p.StartsWith(ArtRoot) && !p.StartsWith(IconRoot)) return;
            var ti = (TextureImporter)assetImporter;
            ti.textureType = TextureImporterType.Default;
            ti.textureShape = TextureImporterShape.Texture2D;
            ti.sRGBTexture = true;
            ti.alphaSource = TextureImporterAlphaSource.FromInput;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = false;
            ti.filterMode = FilterMode.Point;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.maxTextureSize = 4096;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.isReadable = false;

            var def = ti.GetDefaultPlatformTextureSettings();
            def.maxTextureSize = 4096;
            def.textureCompression = TextureImporterCompression.Uncompressed;
            ti.SetPlatformTextureSettings(def);
            string[] platforms = { "iPhone", "Android", "Standalone" };
            for (int i = 0; i < platforms.Length; i++)
            {
                var ps = ti.GetPlatformTextureSettings(platforms[i]);
                ps.overridden = true;
                ps.maxTextureSize = 4096;
                ps.format = TextureImporterFormat.RGBA32;
                ps.textureCompression = TextureImporterCompression.Uncompressed;
                ti.SetPlatformTextureSettings(ps);
            }
        }

        private void OnPreprocessAudio()
        {
            string p = assetPath.Replace('\\', '/');
            if (!p.StartsWith(AudioRoot)) return;
            var ai = (AudioImporter)assetImporter;
            var s = ai.defaultSampleSettings;
            if (p.StartsWith(AudioRoot + "SFX/"))
            {
                ai.forceToMono = true;
                ai.loadInBackground = false;
                s.loadType = AudioClipLoadType.DecompressOnLoad;
                s.compressionFormat = AudioCompressionFormat.PCM;
                s.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
            }
            else if (p.StartsWith(AudioRoot + "Music/") || p.StartsWith(AudioRoot + "Loops/"))
            {
                ai.forceToMono = false;
                ai.loadInBackground = true;
                s.loadType = AudioClipLoadType.Streaming;
                s.compressionFormat = AudioCompressionFormat.Vorbis;
                s.quality = 0.6f;
                s.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
            }
            else return;
            ai.defaultSampleSettings = s;
        }
    }
}
