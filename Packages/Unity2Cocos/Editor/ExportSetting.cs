using System;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Unity2Cocos
{
	[CreateAssetMenu(menuName = "Unity2Cocos/ExportSetting", fileName = "ExportSetting")]
	public class ExportSetting : ScriptableObject
	{
		public static ExportSetting Instance { get; set; }
		
		[Tooltip("Scene to be converted.")]
		public List<SceneAsset> Scenes = new();
		
		[Tooltip("Replace assets referenced in Unity with assets on the Cocos side.")]
		public List<AssetMapper> AssetMappers = new();
		
		[Tooltip("Replace scripts referenced in Unity with scripts on the Cocos side.")]
		public List<ScriptMapper> ScriptMappers = new();

		[Serializable]
		public class AdvancedSettings
		{
			[Tooltip("Unity light intensity to Cocos illuminance. (Directional)")]
			public float IntensityToLightIlluminance = 38400;

			[Tooltip("Unity light intensity to Cocos luminance. (Point/Spot)\n" +
			         "Matches Unity's falloff brightness under the Cocos standard camera exposure.")]
			public float IntensityToLightLuminance = 10;

			[Tooltip("Force GPU instancing (USE_INSTANCING) on materials used by terrain trees.\n" +
			         "Reduces draw calls when a large amount of vegetation is placed on the terrain.")]
			public bool TerrainTreeGPUInstancing = true;
		}

		public AdvancedSettings Advanced;

		private void Reset()
		{
			var builtInResourcesMapperPath = AssetDatabase.GUIDToAssetPath(AssetMapper.BuiltInResourcesMapperGUID);
			AssetMappers.Add(AssetDatabase.LoadMainAssetAtPath(builtInResourcesMapperPath) as AssetMapper);
			var urpResourcesMapperPath = AssetDatabase.GUIDToAssetPath(AssetMapper.URPResourcesMapperGUID);
			AssetMappers.Add(AssetDatabase.LoadMainAssetAtPath(urpResourcesMapperPath) as AssetMapper);
		}
	}

	[CustomEditor(typeof(ExportSetting))]
	public class SceneExportSettingEditor : Editor
	{
		public override void OnInspectorGUI()
		{
			base.OnInspectorGUI();

			var exportSetting = target as ExportSetting;
			if (exportSetting && GUILayout.Button("Convert for Cocos"))
			{
				try
				{
					ExportSetting.Instance = exportSetting;
					Exporter.Export();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
				finally
				{
					EditorUtility.ClearProgressBar();
				}
			}
		}
	}
}
