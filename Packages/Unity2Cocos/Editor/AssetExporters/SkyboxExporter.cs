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
			var cubemap = new Cubemap(CubeFaceSize, TextureFormat.RGBA32, false);

			// Render only the skybox (no scene geometry) into the cubemap.
			var go = new GameObject("__Unity2Cocos_SkyboxCamera");
			Texture2D equirect;
			try
			{
				var cam = go.AddComponent<Camera>();
				cam.clearFlags = CameraClearFlags.Skybox;
				cam.cullingMask = 0;
				cam.transform.position = Vector3.zero;
				if (!cam.RenderToCubemap(cubemap))
				{
					Debug.LogWarning("[SkyboxExporter] RenderToCubemap is not supported on this platform.");
					Object.DestroyImmediate(cubemap);
					return null;
				}

				equirect = CubemapToEquirect(cubemap);
			}
			finally
			{
				Object.DestroyImmediate(go);
				Object.DestroyImmediate(cubemap);
			}
			return equirect;
		}

		private static Texture2D CubemapToEquirect(Cubemap cubemap)
		{
			var faces = new[]
			{
				cubemap.GetPixels(CubemapFace.PositiveX),
				cubemap.GetPixels(CubemapFace.NegativeX),
				cubemap.GetPixels(CubemapFace.PositiveY),
				cubemap.GetPixels(CubemapFace.NegativeY),
				cubemap.GetPixels(CubemapFace.PositiveZ),
				cubemap.GetPixels(CubemapFace.NegativeZ),
			};
			var size = cubemap.width;

			var result = new Texture2D(EquirectWidth, EquirectHeight, TextureFormat.RGBA32, false);
			var pixels = new Color[EquirectWidth * EquirectHeight];

			for (var y = 0; y < EquirectHeight; ++y)
			{
				// v=0 at top of image -> elevation +90deg (up).
				var v = (y + 0.5f) / EquirectHeight;
				var elevation = (0.5f - v) * Mathf.PI;
				var cosEl = Mathf.Cos(elevation);
				var sinEl = Mathf.Sin(elevation);
				for (var x = 0; x < EquirectWidth; ++x)
				{
					var u = (x + 0.5f) / EquirectWidth;
					var azimuth = (u - 0.5f) * 2f * Mathf.PI;
					// dir is the Cocos-space (right-handed) view direction; the scene is converted with
					// z -> -z, so sample the Unity (left-handed) skybox cubemap at the z-flipped direction.
					var dir = new Vector3(cosEl * Mathf.Sin(azimuth), sinEl, -cosEl * Mathf.Cos(azimuth));
					pixels[y * EquirectWidth + x] = SampleCube(faces, size, dir);
				}
			}
			result.SetPixels(pixels);
			result.Apply();
			return result;
		}

		private static Color SampleCube(Color[][] faces, int size, Vector3 dir)
		{
			var ax = Mathf.Abs(dir.x);
			var ay = Mathf.Abs(dir.y);
			var az = Mathf.Abs(dir.z);

			int faceIndex;
			float sc, tc, ma;
			if (ax >= ay && ax >= az)
			{
				ma = ax;
				if (dir.x >= 0) { faceIndex = 0; sc = -dir.z; tc = -dir.y; } // +X right
				else            { faceIndex = 1; sc = dir.z;  tc = -dir.y; } // -X left
			}
			else if (ay >= az)
			{
				ma = ay;
				if (dir.y >= 0) { faceIndex = 2; sc = dir.x; tc = dir.z;  } // +Y top
				else            { faceIndex = 3; sc = dir.x; tc = -dir.z; } // -Y bottom
			}
			else
			{
				ma = az;
				if (dir.z >= 0) { faceIndex = 4; sc = dir.x;  tc = -dir.y; } // +Z front
				else            { faceIndex = 5; sc = -dir.x; tc = -dir.y; } // -Z back
			}

			var fu = (sc / ma + 1f) * 0.5f;
			var fv = (tc / ma + 1f) * 0.5f;

			var px = Mathf.Clamp(Mathf.FloorToInt(fu * size), 0, size - 1);
			// Unity GetPixels is bottom-up; cube map tc is top-down, so flip vertically.
			var py = Mathf.Clamp(Mathf.FloorToInt((1f - fv) * size), 0, size - 1);
			return faces[faceIndex][py * size + px];
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
