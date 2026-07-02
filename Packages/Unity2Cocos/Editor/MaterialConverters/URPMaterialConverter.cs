using System.Collections.Generic;
using cc;
using UnityEditor;
using UnityEngine;

namespace Unity2Cocos
{
	public static class URPMaterialConverter
	{
		private static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
		private static readonly int MainTex = Shader.PropertyToID("_MainTex");
		private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
		private static readonly int Color = Shader.PropertyToID("_Color");
		private static readonly int Cutoff = Shader.PropertyToID("_Cutoff");
		private static readonly int Surface = Shader.PropertyToID("_Surface");
		private static readonly int Mode = Shader.PropertyToID("_Mode");
		private static readonly int Opacity = Shader.PropertyToID("_Opacity");
		private static readonly int Cull = Shader.PropertyToID("_Cull");
		private static readonly int SrcBlend = Shader.PropertyToID("_SrcBlend");
		private static readonly int DstBlend = Shader.PropertyToID("_DstBlend");
		private static readonly int SrcBlendAlpha = Shader.PropertyToID("_SrcBlendAlpha");
		private static readonly int DstBlendAlpha = Shader.PropertyToID("_DstBlendAlpha");
		
		public static void BuildCocosMaterial(
			UnityEngine.Material urpMat,
			ref cc.Material ccMat,
			string cocosTextureUseKeyword)
		{
			var define = ccMat._defines[0];
			var state = ccMat._states[0];
			var prop = ccMat._props[0];
			
			// Texture
			// NOTE: Material.mainTexture/color resolve via [MainTexture]/[MainColor] tags and fall back to
			// _MainTex/_Color, which warns on custom shaders that only have _BaseMap/_BaseColor.
			// Read explicitly from whichever property exists.
			var hasBaseMap = urpMat.HasTexture(BaseMap);
			if (hasBaseMap || urpMat.HasTexture(MainTex))
			{
				var mainTexProp = hasBaseMap ? BaseMap : MainTex;
				var mainTexture = urpMat.GetTexture(mainTexProp);
				if (mainTexture)
				{
					define.Add(cocosTextureUseKeyword, true);
					prop.Add("mainTexture", new AssetReference<cc.Texture2D>(Exporter.GetUuidOrExportAsset(mainTexture)));
				}
				var scale = urpMat.GetTextureScale(mainTexProp);
				var offset = urpMat.GetTextureOffset(mainTexProp);
				prop.Add("tilingOffset", new Vec4()
				{
					x = scale.x,
					y = scale.y,
					z = offset.x,
					w = offset.y
				});
			}
			else if (TryFindCustomMainTexture(urpMat, out var customTexProp, out var customTexture))
			{
				// Custom shaders often use their own property names. (ex. "_BaseColorTexture")
				define.Add(cocosTextureUseKeyword, true);
				prop.Add("mainTexture", new AssetReference<cc.Texture2D>(Exporter.GetUuidOrExportAsset(customTexture)));
				var scale = urpMat.GetTextureScale(customTexProp);
				var offset = urpMat.GetTextureOffset(customTexProp);
				prop.Add("tilingOffset", new Vec4()
				{
					x = scale.x,
					y = scale.y,
					z = offset.x,
					w = offset.y
				});
			}

			// Color
			if (urpMat.HasColor(BaseColor))
			{
				prop.Add("mainColor", Utils.Color32ToCocosColor(urpMat.GetColor(BaseColor)));
			}
			else if (urpMat.HasColor(Color))
			{
				prop.Add("mainColor", Utils.Color32ToCocosColor(urpMat.GetColor(Color)));
			}
			else if (TryFindCustomMainColor(urpMat, out var customColor))
			{
				// Custom shader tint color. (ex. "_BaseTexColorTint")
				// NOTE: Alpha is forced to 1, as it is often unused and would break alpha test.
				customColor.a = 1f;
				prop.Add("mainColor", Utils.Color32ToCocosColor(customColor));
			}

			// Alpha Test
			// Custom shaders clip without the keyword, so also check the RenderType tag.
			var isAlphaTest = urpMat.IsKeywordEnabled("_ALPHATEST_ON") ||
			                  urpMat.GetTag("RenderType", true, string.Empty) == "TransparentCutout";
			if (isAlphaTest)
			{
				define.Add("USE_ALPHA_TEST", true);
				prop.Add("alphaThreshold", urpMat.HasFloat(Cutoff) ? urpMat.GetFloat(Cutoff) : 0.5f);
			}

			// Cull Mode
			if (urpMat.HasFloat(Cull) && urpMat.GetInt(Cull) != 2)
			{
				state.rasterizerState.Add("cullMode", urpMat.GetInt(Cull));
			}
			else if (isAlphaTest && !urpMat.HasFloat(Cull) && !urpMat.HasFloat(Surface) && !urpMat.HasFloat(Mode))
			{
				// Custom cutout shaders (ex. foliage) typically hardcode "Cull Off", render double-sided.
				state.rasterizerState.Add("cullMode", 0);
			}
			
			// Blend Mode
			if (urpMat.HasFloat(Surface))
			{
				// 0: Opaque, 1: Transparent
				ccMat._techIdx = urpMat.GetInt(Surface);
				
				if (ccMat._techIdx > 0)
				{
					var target = state.blendState.targets[0];
					target.Add("blendSrc", Utils.BlendModeToCocos(urpMat.GetInt(SrcBlend)));
					target.Add("blendDst", Utils.BlendModeToCocos(urpMat.GetInt(DstBlend)));
					target.Add("blendSrcAlpha", Utils.BlendModeToCocos(urpMat.GetInt(SrcBlendAlpha)));
					target.Add("blendDstAlpha", Utils.BlendModeToCocos(urpMat.GetInt(DstBlendAlpha)));
				}
			}
			
			// Transparent custom shaders (ex. water).
			// URP (_Surface) and built-in Standard (_Mode) transparency are handled by their converters.
			if (!urpMat.HasFloat(Surface) && !urpMat.HasFloat(Mode) &&
			    urpMat.GetTag("RenderType", true, string.Empty) == "Transparent")
			{
				ccMat._techIdx = 1;
				// Custom shaders often control alpha with a dedicated property.
				var opacity = urpMat.HasFloat(Opacity) ? Mathf.Clamp01(urpMat.GetFloat(Opacity)) : 0f;
				if (prop.TryGetValue("mainColor", out var mainColorObj) && mainColorObj is cc.Color mainColor)
				{
					if (opacity <= 0f)
					{
						opacity = mainColor.a > 0 ? mainColor.a / 255f : 0.6f;
					}
					mainColor.a = (byte)Mathf.RoundToInt(opacity * 255f);
				}
				// Slight gloss so that transparent surfaces (water etc.) catch the light.
				if (!prop.ContainsKey("roughness"))
				{
					prop.Add("roughness", 0.3f);
				}
				if (!prop.ContainsKey("specularIntensity"))
				{
					prop.Add("specularIntensity", 0.5f);
				}
			}

			// Render Queue -> Priority
			// Cocos default priority (128) corresponds to Unity's Geometry queue (2000), and a higher
			// priority renders later. Converting Unity's render queue keeps the relative draw order,
			// which prevents z-fighting between coplanar overlapping objects (e.g. ground decals at
			// the AlphaTest queue 2450 rendering after the opaque ground at 2000).
			if (urpMat.renderQueue >= 0)
			{
				var priorityOffset = urpMat.renderQueue - (int)UnityEngine.Rendering.RenderQueue.Geometry;
				state.priority = Mathf.Clamp(state.priority + priorityOffset, 0, 255);
			}
		}

		private static readonly string[] _texturePreferKeywords =
			{ "basecolor", "albedo", "diffuse", "base", "main", "color" };
		private static readonly string[] _textureExcludeKeywords =
		{
			"normal", "bump", "emis", "metal", "rough", "smooth", "spec",
			"occl", "mask", "detail", "noise", "wind", "fade", "gradient"
		};

		/// <summary>
		/// Finds the most likely albedo texture from a custom shader's texture properties.
		/// </summary>
		private static bool TryFindCustomMainTexture(
			UnityEngine.Material mat, out string propName, out Texture texture)
		{
			propName = null;
			texture = null;
			var shader = mat.shader;
			if (!shader)
			{
				return false;
			}
			var bestScore = int.MaxValue;
			var count = shader.GetPropertyCount();
			for (var i = 0; i < count; ++i)
			{
				if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture)
				{
					continue;
				}
				var name = shader.GetPropertyName(i);
				var lower = name.ToLowerInvariant();
				if (System.Array.Exists(_textureExcludeKeywords, k => lower.Contains(k)))
				{
					continue;
				}
				var tex = mat.GetTexture(name);
				if (!(tex is UnityEngine.Texture2D))
				{
					continue;
				}
				var keywordRank = _texturePreferKeywords.Length;
				for (var k = 0; k < _texturePreferKeywords.Length; ++k)
				{
					if (lower.Contains(_texturePreferKeywords[k]))
					{
						keywordRank = k;
						break;
					}
				}
				var score = keywordRank * 1000 + i;
				if (score < bestScore)
				{
					bestScore = score;
					propName = name;
					texture = tex;
				}
			}
			return texture;
		}

		private static readonly string[] _colorPreferKeywords =
			{ "basecolor", "maincolor", "albedo", "tint", "color" };
		private static readonly string[] _colorExcludeKeywords =
		{
			"emis", "spec", "fade", "wind", "ground", "distance",
			"rim", "outline", "shadow", "fresnel"
		};

		/// <summary>
		/// Finds the most likely main (tint) color from a custom shader's color properties.
		/// </summary>
		private static bool TryFindCustomMainColor(UnityEngine.Material mat, out UnityEngine.Color color)
		{
			color = UnityEngine.Color.white;
			var shader = mat.shader;
			if (!shader)
			{
				return false;
			}
			var found = false;
			var bestScore = int.MaxValue;
			var count = shader.GetPropertyCount();
			for (var i = 0; i < count; ++i)
			{
				if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Color)
				{
					continue;
				}
				var name = shader.GetPropertyName(i);
				var lower = name.ToLowerInvariant();
				if (System.Array.Exists(_colorExcludeKeywords, k => lower.Contains(k)))
				{
					continue;
				}
				var keywordRank = System.Array.FindIndex(_colorPreferKeywords, k => lower.Contains(k));
				if (keywordRank < 0)
				{
					continue;
				}
				var score = keywordRank * 1000 + i;
				if (score < bestScore)
				{
					bestScore = score;
					color = mat.GetColor(name);
					found = true;
				}
			}
			return found;
		}

		private static readonly int BumpMap = Shader.PropertyToID("_BumpMap");
		private static readonly int BumpScale = Shader.PropertyToID("_BumpScale");
		private static readonly int EmissionMap = Shader.PropertyToID("_EmissionMap");
		private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
		
		public static void BuildLitParams(
			UnityEngine.Material urpMat,
			ref cc.Material ccMat)
		{
			var define = ccMat._defines[0];
			var prop = ccMat._props[0];

			// Normal Map
			var hasNormalMap = urpMat.IsKeywordEnabled("_NORMALMAP") && urpMat.HasTexture(BumpMap);
			if (hasNormalMap)
			{
				var normalMap = urpMat.GetTexture(BumpMap);
				if (normalMap)
				{
					define.Add("USE_NORMAL_MAP", true);
					prop.Add("normalMap", new AssetReference<cc.Texture2D>(Exporter.GetUuidOrExportAsset(normalMap)));
					prop.Add("normalStrength", Mathf.Clamp(urpMat.GetFloat(BumpScale), 0, 5));
				}
			}
			
			// Emission
			var useEmission = urpMat.IsKeywordEnabled("_EMISSION");
			if (useEmission)
			{
				var hasEmissionMap = urpMat.HasTexture(EmissionMap);
				if (hasEmissionMap)
				{
					var emissionMap = urpMat.GetTexture(EmissionMap);
					if (emissionMap)
					{
						define.Add("USE_EMISSIVE_MAP", true);
						prop.Add("emissiveMap", new AssetReference<cc.Texture2D>(Exporter.GetUuidOrExportAsset(emissionMap)));
					}
				}

				var emission = urpMat.GetColor(EmissionColor);
				var intensity = Utils.GetHDRColorIntensity(emission);
				var factor = 1f / Mathf.Pow(2f, intensity);
				var ldrColor = new UnityEngine.Color(
					emission.r * factor, emission.g * factor, emission.b * factor, emission.a);
				prop.Add("emissive", Utils.Color32ToCocosColor(ldrColor));
				prop.Add("emissiveScale", new cc.Vec3() { x = intensity, y = intensity, z = intensity });
			}
		}

		private static readonly Dictionary<int, string> _pbrTextureMap = new();

		public static void Initialize()
		{
			_pbrTextureMap.Clear();
		}

		public enum PBRMapSourceType
		{
			Albedo,
			SimpleLitSpecular,
			LitMetallic,
		}
		public static string ExportPBRMap(
			UnityEngine.Texture2D src, float smoothness, float minRoughness, PBRMapSourceType type)
		{
			var srcPath = AssetDatabase.GetAssetPath(src);
			var key = src.GetHashCode();
			if (_pbrTextureMap.TryGetValue(key, out var cocosUuid))
			{
				return cocosUuid;
			}
			var isReadable = src.isReadable;
			if (!isReadable)
			{
				src = Utils.CreateReadableTexture2D(src);
			}
			var colors = src.GetPixels(0, 0, src.width, src.height);
			for (var i = 0; i < colors.Length; ++i)
			{
				// Cocos PBR Map
				//
				// OCCLUSION_CHANNEL          r
				// ROUGHNESS_CHANNEL          g
				// METALLIC_CHANNEL           b
				// SPECULAR_INTENSITY_CHANNEL a
				var c = colors[i];
				colors[i].r = 1f;
				colors[i].g = Mathf.Max(1f - smoothness * c.a, minRoughness);
				colors[i].b = type == PBRMapSourceType.LitMetallic ? c.r : 0f;
				
				// NOTE: Cocos Standard shader does not support specular color, so it grayscale.
				colors[i].a = type == PBRMapSourceType.SimpleLitSpecular ? 
						(c.r + c.g + c.b) / 3f : 1f;
			}

			var dst = new UnityEngine.Texture2D(src.width, src.height);
			dst.SetPixels(colors);
			dst.Apply();

			cocosUuid = Texture2DExporter.ExportPBRMap(dst, srcPath);
			_pbrTextureMap.Add(key, cocosUuid);
			
			if (!isReadable)
			{
				Object.DestroyImmediate(src);
			}
			Object.DestroyImmediate(dst);
			return cocosUuid;
		}
	}
}
