using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Unity2Cocos
{
	public static class FBXExporter
	{
		private class Meta : cc.Meta
		{
			public class UserData
			{
				public Dictionary<string, bool> fbx = new()
				{
					{ "smartMaterialEnabled", false }
				};
				public Dictionary<string, List<string>> assetFinder = new();
			}

			public Meta()
			{
				ver = "2.3.12";
				importer = "fbx";
			}
		}

		public static string Export(AssetExporter.ExportInfo info, Object source)
		{
			var ccMeta = new Meta();
			ccMeta.userData = new Meta.UserData();

			// Cocos Creator assigns each sub-asset an id derived from its name ("<mesh name>.mesh").
			// Compute the same id here so that scene references resolve without any post-processing.
			var result = string.Empty;
			var meshes = AssetDatabase.LoadAllAssetsAtPath(info.UnityAssetPath).OfType<Mesh>().ToArray();
			var usedSubIds = new HashSet<string>();
			foreach (var mesh in meshes)
			{
				var subName = $"{mesh.name}.mesh";
				var extend = 0;
				var subId = Utils.CocosNameToSubId(subName);
				while (!usedSubIds.Add(subId))
				{
					// Cocos extends the id on name collision.
					// NOTE: Cocos import order is not guaranteed to match Unity's sub-asset order.
					subId = Utils.CocosNameToSubId(subName, ++extend);
				}
				if (extend > 0)
				{
					Debug.LogWarning(
						"[FBXExporter] Duplicate mesh name in FBX, reference may need to be fixed manually on Cocos. " +
						$"-> {info.UnityAssetName}<{mesh.name}>");
				}

				var uuid = $"{ccMeta.uuid}@{subId}";
				if (mesh.Equals(source))
				{
					result = uuid;
				}
				Exporter.AddAssetMap(mesh, uuid);
			}

			AssetExporter.ExportAssetCopy(info);
			AssetExporter.ExportMeta(ccMeta, info);

			return result;
		}
	}
}
