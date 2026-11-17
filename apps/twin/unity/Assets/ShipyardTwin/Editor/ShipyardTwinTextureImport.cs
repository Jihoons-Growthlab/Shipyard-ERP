using UnityEditor;
using UnityEngine;

namespace ShipyardTwin.Editor
{
    /// <summary>Import settings for the generated detail textures: tiling, mipmaps, anisotropy; *_normal as normal maps.</summary>
    public sealed class ShipyardTwinTextureImport : AssetPostprocessor
    {
        private const string Folder = "Assets/ShipyardTwin/Runtime/Resources/ShipyardTwin/Textures/";

        private const string Satellite = "Assets/ShipyardTwin/Runtime/Resources/ShipyardTwin/Satellite/";

        private void OnPreprocessTexture()
        {
            if (assetPath.StartsWith(Satellite, System.StringComparison.Ordinal))
            {
                var satellite = (TextureImporter)assetImporter;
                satellite.wrapMode = TextureWrapMode.Clamp;
                satellite.maxTextureSize = 4096;
                satellite.alphaSource = TextureImporterAlphaSource.FromInput;
                satellite.alphaIsTransparency = true;
                satellite.mipmapEnabled = true;
                satellite.anisoLevel = 8;
                satellite.textureCompression = TextureImporterCompression.CompressedHQ;
                return;
            }

            if (!assetPath.StartsWith(Folder, System.StringComparison.Ordinal))
            {
                return;
            }

            var importer = (TextureImporter)assetImporter;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.mipmapEnabled = true;
            importer.anisoLevel = 8;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            if (assetPath.EndsWith("_normal.png", System.StringComparison.Ordinal))
            {
                importer.textureType = TextureImporterType.NormalMap;
            }
        }
    }
}
