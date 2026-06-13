using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
			// Cocos names a mesh after the FBX Geometry node, while Unity names the Mesh asset after
			// the FBX Model node (e.g. LOD meshes become "<name>_LOD0" in Unity but keep their geometry
			// name like "Mesh.471" in Cocos). Resolve the geometry name from the FBX so the ids match.
			var modelToGeometry = FbxModelToGeometryMap.Get(info.UnityAssetPath);

			var result = string.Empty;
			var meshes = AssetDatabase.LoadAllAssetsAtPath(info.UnityAssetPath).OfType<Mesh>().ToArray();
			var usedSubIds = new HashSet<string>();
			foreach (var mesh in meshes)
			{
				// Unity's mesh.name is the FBX Model node name; map it to the geometry name Cocos uses.
				var cocosMeshName = modelToGeometry.TryGetValue(mesh.name, out var geometryName)
					? geometryName
					: mesh.name;
				var subName = $"{cocosMeshName}.mesh";
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
						$"-> {info.UnityAssetName}<{cocosMeshName}>");
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

	/// <summary>
	/// Parses an FBX file to map Model node names (= Unity Mesh asset names) to their connected
	/// Geometry node names (= the names Cocos/FBX2glTF uses for the imported meshes).
	/// Supports binary FBX (the format Unity/Cocos consume); returns an empty map for anything else,
	/// in which case the caller falls back to Unity's mesh name.
	/// </summary>
	internal static class FbxModelToGeometryMap
	{
		private static readonly Dictionary<string, Dictionary<string, string>> _cache = new();

		public static Dictionary<string, string> Get(string fbxPath)
		{
			if (_cache.TryGetValue(fbxPath, out var cached))
			{
				return cached;
			}
			var map = new Dictionary<string, string>();
			try
			{
				Parse(fbxPath, map);
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[FBXExporter] Failed to parse FBX for mesh names, using Unity names. -> {fbxPath}\n{e}");
				map.Clear();
			}
			_cache[fbxPath] = map;
			return map;
		}

		private sealed class Node
		{
			public string Name;
			public readonly List<(char Type, long Long, string Str)> Props = new();
			public readonly List<Node> Children = new();
		}

		private static void Parse(string fbxPath, Dictionary<string, string> map)
		{
			var bytes = File.ReadAllBytes(fbxPath);
			// Binary FBX magic: "Kaydara FBX Binary  \0".
			var magic = "Kaydara FBX Binary  ";
			if (bytes.Length < 27 || Encoding.ASCII.GetString(bytes, 0, magic.Length) != magic)
			{
				return; // Not a binary FBX (e.g. ASCII FBX) - fall back to Unity names.
			}

			using var ms = new MemoryStream(bytes);
			using var br = new BinaryReader(ms);
			ms.Position = 23;
			var version = br.ReadUInt32();
			var is64 = version >= 7500;
			ms.Position = 27;

			var roots = new List<Node>();
			var footer = is64 ? 25 : 13;
			while (ms.Position < ms.Length - footer)
			{
				var node = ReadNode(br, is64);
				if (node == null)
				{
					break;
				}
				roots.Add(node);
			}

			var objects = roots.FirstOrDefault(n => n.Name == "Objects");
			var connections = roots.FirstOrDefault(n => n.Name == "Connections");
			if (objects == null || connections == null)
			{
				return;
			}

			// id -> (class, clean name) for Geometry / Model objects.
			var idInfo = new Dictionary<long, (string Cls, string Name)>();
			foreach (var child in objects.Children)
			{
				if (child.Name != "Geometry" && child.Name != "Model")
				{
					continue;
				}
				if (child.Props.Count < 2 || child.Props[0].Type != 'L')
				{
					continue;
				}
				idInfo[child.Props[0].Long] = (child.Name, CleanName(child.Props[1].Str));
			}

			// Connections: ("OO", childId, parentId). Geometry(child) connects to Model(parent).
			foreach (var c in connections.Children)
			{
				if (c.Name != "C" || c.Props.Count < 3 || c.Props[0].Str != "OO")
				{
					continue;
				}
				if (idInfo.TryGetValue(c.Props[1].Long, out var ci) &&
				    idInfo.TryGetValue(c.Props[2].Long, out var pi) &&
				    ci.Cls == "Geometry" && pi.Cls == "Model" &&
				    !string.IsNullOrEmpty(pi.Name) && !string.IsNullOrEmpty(ci.Name))
				{
					map[pi.Name] = ci.Name;
				}
			}
		}

		private static Node ReadNode(BinaryReader br, bool is64)
		{
			var endOffset = is64 ? (long)br.ReadUInt64() : br.ReadUInt32();
			var numProps = is64 ? (long)br.ReadUInt64() : br.ReadUInt32();
			_ = is64 ? (long)br.ReadUInt64() : br.ReadUInt32(); // property list length (unused)
			int nameLen = br.ReadByte();
			if (endOffset == 0)
			{
				return null; // Null record terminates a node list.
			}

			var node = new Node { Name = Encoding.ASCII.GetString(br.ReadBytes(nameLen)) };
			for (long i = 0; i < numProps; ++i)
			{
				node.Props.Add(ReadProp(br));
			}
			while (br.BaseStream.Position < endOffset)
			{
				var child = ReadNode(br, is64);
				if (child == null)
				{
					break;
				}
				node.Children.Add(child);
			}
			br.BaseStream.Position = endOffset;
			return node;
		}

		private static (char, long, string) ReadProp(BinaryReader br)
		{
			var type = (char)br.ReadByte();
			switch (type)
			{
				case 'Y': br.ReadInt16(); break;
				case 'C': br.ReadByte(); break;
				case 'I': br.ReadInt32(); break;
				case 'F': br.ReadSingle(); break;
				case 'D': br.ReadDouble(); break;
				case 'L': return (type, br.ReadInt64(), null);
				case 'f':
				case 'd':
				case 'l':
				case 'i':
				case 'b':
				{
					_ = br.ReadUInt32(); // array length
					_ = br.ReadUInt32(); // encoding
					var compressedLen = br.ReadUInt32();
					br.BaseStream.Position += compressedLen;
					break;
				}
				case 'S':
				case 'R':
				{
					var len = br.ReadUInt32();
					var data = br.ReadBytes((int)len);
					return (type, 0, type == 'S' ? Encoding.ASCII.GetString(data) : null);
				}
				default:
					throw new InvalidDataException($"Unknown FBX property type '{type}'.");
			}
			return (type, 0, null);
		}

		// FBX object names are stored as "Name\0\x01ClassType".
		private static string CleanName(string raw)
		{
			if (string.IsNullOrEmpty(raw))
			{
				return raw;
			}
			var idx = raw.IndexOf('\0');
			return idx >= 0 ? raw.Substring(0, idx) : raw;
		}
	}
}
