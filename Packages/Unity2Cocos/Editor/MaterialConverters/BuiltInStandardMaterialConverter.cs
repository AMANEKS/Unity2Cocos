using cc;
using UnityEngine;

namespace Unity2Cocos
{
	/// <summary>
	/// Built-in RP Standard to Cocos Standard Material.
	/// </summary>
	/// <remarks>
	/// Property names of emission and normal map are the same as URP Lit.
	/// </remarks>
	[MaterialConverter("Standard")]
	public class BuiltInStandardMaterialConverter : StandardMaterialConverter
	{
		private static readonly int Mode = Shader.PropertyToID("_Mode");
		private static readonly int Glossiness = Shader.PropertyToID("_Glossiness");
		private static readonly int Metallic = Shader.PropertyToID("_Metallic");
		private static readonly int MetallicGlossMap = Shader.PropertyToID("_MetallicGlossMap");
		private static readonly int OcclusionMap = Shader.PropertyToID("_OcclusionMap");

		public override cc.Material Convert(UnityEngine.Material material)
		{
			var ccMat = GetStandardMaterial(material);
			URPMaterialConverter.BuildLitParams(material, ref ccMat);

			var define = ccMat._defines[0];
			var prop = ccMat._props[0];

			// Built-in Standard transparency. (_Mode: 2 = Fade, 3 = Transparent)
			if (material.HasFloat(Mode) && material.GetFloat(Mode) >= 2f)
			{
				ccMat._techIdx = 1;
			}

			var metallic = material.HasFloat(Metallic) ? material.GetFloat(Metallic) : 0f;
			var smoothness = material.HasFloat(Glossiness) ? material.GetFloat(Glossiness) : 0.5f;
			var roughness = 1f - smoothness;

			var metallicMap = material.HasTexture(MetallicGlossMap)
				? material.GetTexture(MetallicGlossMap) as UnityEngine.Texture2D : null;
			if (metallicMap)
			{
				define.Add("USE_PBR_MAP", true);
				var pbrMapUuid = URPMaterialConverter.ExportPBRMap(
					metallicMap, smoothness, 0f, URPMaterialConverter.PBRMapSourceType.LitMetallic);
				prop.Add("pbrMap", new AssetReference(pbrMapUuid));
				metallic = 1f;
				roughness = 1f;
			}

			prop.Add("metallic", metallic);
			prop.Add("roughness", roughness);

			var occlusionMap = material.HasTexture(OcclusionMap) ? material.GetTexture(OcclusionMap) : null;
			if (occlusionMap)
			{
				define.Add("USE_OCCLUSION_MAP", true);
				prop.Add("occlusionMap", new AssetReference<cc.Texture2D>(Exporter.GetUuidOrExportAsset(occlusionMap)));
			}

			return ccMat;
		}
	}
}
