using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace Unity2Cocos
{
	/// <summary>
	/// Converts the scene's skybox into a Cocos cubemap (erp-texture-cube).
	/// The active skybox is rendered to a cubemap with Camera.RenderToCubemap (so any skybox shader
	/// type - 6 Sided / Cubemap / Panoramic / Procedural - is handled with correct face orientation),
	/// then reprojected to an equirectangular image and exported in the same meta format as Cocos'
	/// built-in default skybox.
	/// </summary>
	public static class SkyboxExporter
	{
		private const int CubeFaceSize = 1024;
		private const int EquirectWidth = 2048;
		private const int EquirectHeight = 1024;

		// Sub-asset names used by Cocos' erp-texture-cube importer.
		private const string TextureCubeName = "textureCube";
		private static readonly string[] FaceNames = { "right", "left", "top", "bottom", "front", "back" };

		/// <summary>
		/// Exports the given skybox material as a Cocos cubemap.
		/// Returns the textureCube sub-asset uuid ("&lt;uuid&gt;@b47c0"), or empty on failure.
		/// </summary>
		public static string Export(Material skyboxMaterial, string outputFolderPath)
		{
			if (!skyboxMaterial)
			{
				return string.Empty;
			}

			Texture2D equirect = null;
			try
			{
				equirect = RenderSkyboxToEquirect();
				if (!equirect)
				{
					return string.Empty;
				}

				var assetUuid = Utils.NewUuid();
				var textureCubeUuid = $"{assetUuid}@{Utils.CocosNameToSubId(TextureCubeName)}";

				// Output path: place next to the skybox material, as a .png.
				var srcPath = AssetDatabase.GetAssetPath(skyboxMaterial);
				if (string.IsNullOrEmpty(srcPath))
				{
					srcPath = $"Assets/{skyboxMaterial.name}.mat";
				}
				var info = new AssetExporter.ExportInfo(srcPath, outputFolderPath, ".png");
				var outputPath = info.CocosAssetOutputPath;

				Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
				File.WriteAllBytes(outputPath, equirect.EncodeToPNG());

				var meta = BuildMeta(assetUuid, textureCubeUuid);
				File.WriteAllText(outputPath + ".meta", JsonConvert.SerializeObject(meta, Formatting.Indented));

				return textureCubeUuid;
			}
			catch (System.Exception e)
			{
				Debug.LogError($"[SkyboxExporter] Failed to export skybox.\n{e}");
				return string.Empty;
			}
			finally
			{
				if (equirect)
				{
					Object.DestroyImmediate(equirect);
				}
			}
		}

		private static Texture2D RenderSkyboxToEquirect()
		{
			// Render only the skybox (no scene geometry) into a cubemap RenderTexture, then let
			// Unity's official 360-capture API project it to an equirectangular image.
			// (Verified: upright orientation, no seams and correct colors in the Linear color space.)
			var cubeRT = new RenderTexture(
				CubeFaceSize, CubeFaceSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
			{
				dimension = UnityEngine.Rendering.TextureDimension.Cube
			};
			var equirectRT = new RenderTexture(
				EquirectWidth, EquirectHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

			var go = new GameObject("__Unity2Cocos_SkyboxCamera");
			var prevActive = RenderTexture.active;
			try
			{
				var cam = go.AddComponent<Camera>();
				cam.clearFlags = CameraClearFlags.Skybox;
				cam.cullingMask = 0;
				cam.allowHDR = false;
				cam.transform.position = Vector3.zero;
				cam.transform.rotation = Quaternion.identity;

				if (!cam.RenderToCubemap(cubeRT, 63))
				{
					Debug.LogWarning("[SkyboxExporter] RenderToCubemap is not supported on this platform.");
					return null;
				}
				cubeRT.ConvertToEquirect(equirectRT, Camera.MonoOrStereoscopicEye.Mono);

				var result = new Texture2D(EquirectWidth, EquirectHeight, TextureFormat.RGBA32, false);
				RenderTexture.active = equirectRT;
				result.ReadPixels(new UnityEngine.Rect(0, 0, EquirectWidth, EquirectHeight), 0, 0);
				result.Apply();
				return result;
			}
			finally
			{
				RenderTexture.active = prevActive;
				Object.DestroyImmediate(go);
				cubeRT.Release();
				Object.DestroyImmediate(cubeRT);
				equirectRT.Release();
				Object.DestroyImmediate(equirectRT);
			}
		}

		private static Dictionary<string, object> BuildMeta(string assetUuid, string textureCubeUuid)
		{
			var faceSubMetas = new Dictionary<string, object>();
			foreach (var faceName in FaceNames)
			{
				var faceId = Utils.CocosNameToSubId(faceName);
				faceSubMetas.Add(faceId, new Dictionary<string, object>
				{
					{ "importer", "texture-cube-face" },
					{ "uuid", $"{textureCubeUuid}@{faceId}" },
					{ "displayName", "" },
					{ "id", faceId },
					{ "name", faceName },
					{ "userData", new Dictionary<string, object>() },
					{ "ver", "1.0.0" },
					{ "imported", true },
					{ "files", new[] { ".json", ".png" } },
					{ "subMetas", new Dictionary<string, object>() },
				});
			}

			var textureCubeId = Utils.CocosNameToSubId(TextureCubeName);
			var textureCubeSubMeta = new Dictionary<string, object>
			{
				{ "importer", "erp-texture-cube" },
				{ "uuid", textureCubeUuid },
				{ "displayName", "" },
				{ "id", textureCubeId },
				{ "name", TextureCubeName },
				{
					"userData", new Dictionary<string, object>
					{
						{ "wrapModeS", "repeat" },
						{ "wrapModeT", "repeat" },
						{ "minfilter", "linear" },
						{ "magfilter", "linear" },
						{ "mipfilter", "linear" },
						{ "anisotropy", 0 },
						{ "isRGBE", false },
						{ "imageDatabaseUri", assetUuid },
					}
				},
				{ "ver", "1.0.10" },
				{ "imported", true },
				{ "files", new[] { ".json" } },
				{ "subMetas", faceSubMetas },
			};

			return new Dictionary<string, object>
			{
				{ "ver", "1.0.27" },
				{ "importer", "image" },
				{ "imported", true },
				{ "uuid", assetUuid },
				{ "files", new[] { ".json", ".png" } },
				{ "subMetas", new Dictionary<string, object> { { textureCubeId, textureCubeSubMeta } } },
				{
					"userData", new Dictionary<string, object>
					{
						{ "type", "texture cube" },
						{ "isRGBE", false },
						{ "hasAlpha", false },
						{ "fixAlphaTransparencyArtifacts", false },
					}
				},
			};
		}
	}
}
