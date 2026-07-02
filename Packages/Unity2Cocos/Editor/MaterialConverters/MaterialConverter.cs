using System;
using System.Collections.Generic;
using cc;

namespace Unity2Cocos
{
	[AttributeUsage(AttributeTargets.Class)]
	public class MaterialConverterAttribute : Attribute
	{
		public string Shader { get; }

		public MaterialConverterAttribute(string shader)
		{
			Shader = shader;
		}
	}

	public abstract class MaterialConverter
	{
		public abstract Material Convert(UnityEngine.Material material);

		// Materials that should be exported with GPU instancing regardless of the Unity setting.
		// (ex. terrain tree vegetation that is instanced by Unity's terrain system)
		private static readonly HashSet<int> _forceInstancingMaterials = new();

		public static void ClearForceInstancing()
		{
			_forceInstancingMaterials.Clear();
		}

		public static void RegisterForceInstancing(UnityEngine.Material material)
		{
			if (material)
			{
				_forceInstancingMaterials.Add(material.GetHashCode());
			}
		}

		public static Material CreateMaterial(UnityEngine.Material src, string effectUuid, int passCount)
		{
			var ccMat = new Material
			{
				_name = "",
				_effectAsset = new(effectUuid),
				_defines = new MaterialDefine[passCount],
				_states = new MaterialState[passCount],
				_props = new MaterialProp[passCount]
			};
			for (var i = 0; i < ccMat._defines.Length; ++i)
			{
				ccMat._defines[i] = new MaterialDefine();
			}
			for (var i = 0; i < ccMat._states.Length; ++i)
			{
				var state = new MaterialState();
				state.blendState.targets[0] = new Dictionary<string, object>();
				ccMat._states[i] = state;
			}
			for (var i = 0; i < ccMat._props.Length; ++i)
			{
				ccMat._props[i] = new MaterialProp();
			}
			
			// Instancing
			if (src.enableInstancing || _forceInstancingMaterials.Contains(src.GetHashCode()))
			{
				// NOTE: Apply to every pass like the Cocos editor does.
				// Enabling instancing only on the forward pass leaves the shadow-caster pass
				// non-instanced, and such models fail to render into the shadow map.
				foreach (var define in ccMat._defines)
				{
					define.Add("USE_INSTANCING", true);
				}
			}

			return ccMat;
		}
	}
}
