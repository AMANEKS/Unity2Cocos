using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using cc;
using UnityEditor;
using UnityEngine;

namespace cc
{
	public abstract class TerrainAsset : Asset
	{
	}

	public class Terrain : Component
	{
		public AssetReference __asset;
		public AssetReference _effectAsset = null;
		public object[] _lightmapInfos = Array.Empty<object>();
		public bool _receiveShadow = true;
		public bool _useNormalmap;
		public bool _usePBR;
		public bool _lodEnable;
		public float _lodBias;
	}
}

namespace Unity2Cocos
{
	[ComponentConverter(typeof(UnityEngine.Terrain))]
	public class TerrainConverter : ComponentConverter<UnityEngine.Terrain>
	{
		protected override IEnumerable<CCType> Convert(UnityEngine.Terrain component, int currentId)
		{
			if (!component.terrainData)
			{
				return Array.Empty<CCType>();
			}
			var result = TerrainExporter.Export(component, Exporter.OutputFolderPath);
			if (string.IsNullOrEmpty(result.Uuid))
			{
				return Array.Empty<CCType>();
			}
			return new CCType[]
			{
				new cc.Terrain
				{
					_enabled = component.enabled,
					__asset = new AssetReference(result.Uuid) { __expectedType__ = "cc.TerrainAsset" },
					_useNormalmap = result.HasNormalMap,
				}
			};
		}
	}

	/// <summary>
	/// Converts a Unity Terrain into a Cocos native terrain asset (.terrain, version 8 binary).
	/// </summary>
	public static class TerrainExporter
	{
		public struct Result
		{
			public string Uuid;
			public bool HasNormalMap;
		}

		// One Cocos terrain block is 32x32 tiles; vertex count per axis = blockCount * 32 + 1.
		private const int BlockTileComplexity = 32;
		private const int WeightMapSize = 32;
		private const int LightMapSize = 128;
		private const int HeightBase = 32768;
		private const float HeightFactory = 1.0f / 128.0f; // worldHeight = (raw - 32768) * HeightFactory
		private const int TerrainDataVersion8 = 0x01010008;
		private const int MaxLayersPerBlock = 4;

		private class TerrainMeta : cc.Meta
		{
			public TerrainMeta()
			{
				ver = "1.1.50";
				importer = "terrain";
				files = new[] { ".bin", ".json" };
			}
		}

		public static Result Export(UnityEngine.Terrain terrain, string outputFolderPath)
		{
			var data = terrain.terrainData;
			var srcPath = AssetDatabase.GetAssetPath(data);
			if (string.IsNullOrEmpty(srcPath))
			{
				srcPath = $"Assets/{data.name}.asset";
			}
			var info = new AssetExporter.ExportInfo(srcPath, outputFolderPath, ".terrain");

			// --- Geometry: block count aligned to Cocos 32-tile blocks. ---
			var heightmapRes = data.heightmapResolution;
			var blockCount = Mathf.Max(1, Mathf.RoundToInt((heightmapRes - 1) / (float)BlockTileComplexity));
			var vertexCount = blockCount * BlockTileComplexity + 1;
			var size = data.size;
			var tileSize = size.x / (blockCount * BlockTileComplexity);

			// --- Heights (resampled to the Cocos grid, Z flipped to match right-handed scene). ---
			var unityHeights = data.GetHeights(0, 0, heightmapRes, heightmapRes);
			var heights = new ushort[vertexCount * vertexCount];
			var worldHeights = new float[vertexCount * vertexCount];
			for (var j = 0; j < vertexCount; ++j)
			{
				for (var i = 0; i < vertexCount; ++i)
				{
					var u = i / (float)(vertexCount - 1);
					var v = j / (float)(vertexCount - 1);
					// Flip Z so the terrain surface matches the z-negated (right-handed) scene.
					var h = SampleHeight(unityHeights, heightmapRes, u, 1f - v) * size.y;
					worldHeights[j * vertexCount + i] = h;
					var raw = Mathf.Clamp(Mathf.RoundToInt(h / HeightFactory) + HeightBase, 0, 65535);
					heights[j * vertexCount + i] = (ushort)raw;
				}
			}

			// --- Normals (from world heights). ---
			var normals = new float[vertexCount * vertexCount * 3];
			for (var j = 0; j < vertexCount; ++j)
			{
				for (var i = 0; i < vertexCount; ++i)
				{
					var hl = worldHeights[j * vertexCount + Mathf.Max(i - 1, 0)];
					var hr = worldHeights[j * vertexCount + Mathf.Min(i + 1, vertexCount - 1)];
					var hd = worldHeights[Mathf.Max(j - 1, 0) * vertexCount + i];
					var hu = worldHeights[Mathf.Min(j + 1, vertexCount - 1) * vertexCount + i];
					var n = new Vector3(hl - hr, 2f * tileSize, hd - hu).normalized;
					var o = (j * vertexCount + i) * 3;
					normals[o] = n.x;
					normals[o + 1] = n.y;
					normals[o + 2] = n.z;
				}
			}

			// --- Layers, weights & per-block layer assignment. ---
			var layers = data.terrainLayers ?? Array.Empty<TerrainLayer>();
			var layerCount = layers.Length;
			var alphaRes = data.alphamapResolution;
			var alphamaps = layerCount > 0 ? data.GetAlphamaps(0, 0, alphaRes, alphaRes) : null;

			var globalW = WeightMapSize * blockCount;
			var weights = new byte[globalW * globalW * 4];
			var layerBuffer = new short[blockCount * blockCount * MaxLayersPerBlock];
			for (var i = 0; i < layerBuffer.Length; ++i)
			{
				layerBuffer[i] = -1;
			}

			if (alphamaps != null)
			{
				BuildWeights(alphamaps, alphaRes, layerCount, blockCount, globalW, weights, layerBuffer);
			}

			var hasNormalMap = false;
			var layerInfos = new List<LayerInfo>(layerCount);
			foreach (var layer in layers)
			{
				var detailUuid = layer && layer.diffuseTexture
					? Exporter.GetUuidOrExportAsset(layer.diffuseTexture) : string.Empty;
				var normalUuid = layer && layer.normalMapTexture
					? Exporter.GetUuidOrExportAsset(layer.normalMapTexture) : string.Empty;
				if (!string.IsNullOrEmpty(normalUuid))
				{
					hasNormalMap = true;
				}
				layerInfos.Add(new LayerInfo
				{
					TileSize = layer ? layer.tileSize.x : 1.0,
					DetailMapId = detailUuid ?? string.Empty,
					NormalMapId = normalUuid ?? string.Empty,
					Metallic = layer ? layer.metallic : 0.0,
					Roughness = layer ? 1.0 - layer.smoothness : 0.5,
				});
			}

			var meta = new TerrainMeta();
			var bytes = Serialize(tileSize, blockCount, heights, normals, weights, layerBuffer, layerInfos);

			Directory.CreateDirectory(Path.GetDirectoryName(info.CocosAssetOutputPath)!);
			File.WriteAllBytes(info.CocosAssetOutputPath, bytes);
			AssetExporter.ExportMeta(meta, info);

			return new Result { Uuid = meta.uuid, HasNormalMap = hasNormalMap };
		}

		private static float SampleHeight(float[,] heights, int res, float u, float v)
		{
			var fx = Mathf.Clamp01(u) * (res - 1);
			var fy = Mathf.Clamp01(v) * (res - 1);
			var x0 = Mathf.FloorToInt(fx);
			var y0 = Mathf.FloorToInt(fy);
			var x1 = Mathf.Min(x0 + 1, res - 1);
			var y1 = Mathf.Min(y0 + 1, res - 1);
			var tx = fx - x0;
			var ty = fy - y0;
			// Unity GetHeights is indexed [y (z-axis), x].
			var h00 = heights[y0, x0];
			var h10 = heights[y0, x1];
			var h01 = heights[y1, x0];
			var h11 = heights[y1, x1];
			return Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), ty);
		}

		private static void BuildWeights(
			float[,,] alphamaps, int alphaRes, int layerCount, int blockCount, int globalW,
			byte[] weights, short[] layerBuffer)
		{
			for (var by = 0; by < blockCount; ++by)
			{
				for (var bx = 0; bx < blockCount; ++bx)
				{
					// Pick the dominant layers within the block (Cocos allows up to 4 per block).
					var sums = new double[layerCount];
					for (var ly = 0; ly < WeightMapSize; ++ly)
					{
						for (var lx = 0; lx < WeightMapSize; ++lx)
						{
							var gx = bx * WeightMapSize + lx;
							var gy = by * WeightMapSize + ly;
							SampleAlpha(alphamaps, alphaRes, layerCount, gx, gy, globalW, out var ax, out var ay);
							for (var l = 0; l < layerCount; ++l)
							{
								sums[l] += alphamaps[ay, ax, l];
							}
						}
					}
					var top = Enumerable.Range(0, layerCount)
						.OrderByDescending(l => sums[l])
						.Take(MaxLayersPerBlock)
						.Where(l => sums[l] > 0)
						.ToArray();

					var blockId = by * blockCount + bx;
					for (var k = 0; k < top.Length; ++k)
					{
						layerBuffer[blockId * MaxLayersPerBlock + k] = (short)top[k];
					}

					for (var ly = 0; ly < WeightMapSize; ++ly)
					{
						for (var lx = 0; lx < WeightMapSize; ++lx)
						{
							var gx = bx * WeightMapSize + lx;
							var gy = by * WeightMapSize + ly;
							SampleAlpha(alphamaps, alphaRes, layerCount, gx, gy, globalW, out var ax, out var ay);
							float w0 = 0, w1 = 0, w2 = 0, w3 = 0;
							if (top.Length > 0) w0 = alphamaps[ay, ax, top[0]];
							if (top.Length > 1) w1 = alphamaps[ay, ax, top[1]];
							if (top.Length > 2) w2 = alphamaps[ay, ax, top[2]];
							if (top.Length > 3) w3 = alphamaps[ay, ax, top[3]];
							var sum = w0 + w1 + w2 + w3;
							if (sum > 0)
							{
								w0 /= sum; w1 /= sum; w2 /= sum; w3 /= sum;
							}
							else if (top.Length > 0)
							{
								w0 = 1;
							}
							var idx = (gy * globalW + gx) * 4;
							weights[idx] = (byte)Mathf.RoundToInt(w0 * 255);
							weights[idx + 1] = (byte)Mathf.RoundToInt(w1 * 255);
							weights[idx + 2] = (byte)Mathf.RoundToInt(w2 * 255);
							weights[idx + 3] = (byte)Mathf.RoundToInt(w3 * 255);
						}
					}
				}
			}
		}

		private static void SampleAlpha(
			float[,,] alphamaps, int alphaRes, int layerCount, int gx, int gy, int globalW,
			out int ax, out int ay)
		{
			var u = gx / (float)(globalW - 1);
			var v = gy / (float)(globalW - 1);
			ax = Mathf.Clamp(Mathf.RoundToInt(u * (alphaRes - 1)), 0, alphaRes - 1);
			// Flip Z to match the height map orientation.
			ay = Mathf.Clamp(Mathf.RoundToInt((1f - v) * (alphaRes - 1)), 0, alphaRes - 1);
		}

		private struct LayerInfo
		{
			public double TileSize;
			public string DetailMapId;
			public string NormalMapId;
			public double Metallic;
			public double Roughness;
		}

		private static byte[] Serialize(
			double tileSize, int blockCount, ushort[] heights, float[] normals,
			byte[] weights, short[] layerBuffer, List<LayerInfo> layerInfos)
		{
			using var ms = new MemoryStream();
			using var bw = new BinaryWriter(ms);

			bw.Write(TerrainDataVersion8);
			bw.Write(tileSize);
			bw.Write(blockCount); // blockCount[0]
			bw.Write(blockCount); // blockCount[1]
			bw.Write((short)WeightMapSize);
			bw.Write((short)LightMapSize);

			bw.Write(heights.Length);
			foreach (var h in heights) bw.Write((short)h);

			bw.Write(normals.Length);
			foreach (var n in normals) bw.Write(n);

			bw.Write(weights.Length);
			bw.Write(weights);

			bw.Write(layerBuffer.Length);
			foreach (var l in layerBuffer) bw.Write(l);

			bw.Write(layerInfos.Count);
			for (var i = 0; i < layerInfos.Count; ++i)
			{
				var info = layerInfos[i];
				bw.Write(i); // slot
				bw.Write(info.TileSize);
				WriteString(bw, info.DetailMapId);
				WriteString(bw, info.NormalMapId);
				bw.Write(info.Roughness);
				bw.Write(info.Metallic);
			}

			bw.Flush();
			return ms.ToArray();
		}

		// Cocos TerrainBuffer.writeString: int32 length + one byte per char.
		private static void WriteString(BinaryWriter bw, string value)
		{
			value ??= string.Empty;
			bw.Write(value.Length);
			foreach (var c in value)
			{
				bw.Write((byte)c);
			}
		}
	}

	/// <summary>
	/// Spawns Unity terrain tree instances as temporary GameObjects so the normal hierarchy
	/// conversion picks them up as regular nodes (Cocos has no terrain-embedded trees).
	/// </summary>
	public static class TerrainTreeInstancer
	{
		public static List<GameObject> Spawn()
		{
			var spawned = new List<GameObject>();
			foreach (var terrain in UnityEngine.Object.FindObjectsOfType<UnityEngine.Terrain>())
			{
				var data = terrain.terrainData;
				if (!data)
				{
					continue;
				}
				var prototypes = data.treePrototypes;
				var size = data.size;
				var basePos = terrain.transform.position;
				foreach (var inst in data.treeInstances)
				{
					if (inst.prototypeIndex < 0 || inst.prototypeIndex >= prototypes.Length)
					{
						continue;
					}
					var prefab = prototypes[inst.prototypeIndex].prefab;
					if (!prefab)
					{
						continue;
					}
					var go = UnityEngine.Object.Instantiate(prefab);
					go.name = prefab.name;
					go.transform.SetParent(terrain.transform, true);
					go.transform.position = basePos + new Vector3(
						inst.position.x * size.x, inst.position.y * size.y, inst.position.z * size.z);
					// Compose with the prefab root's own rotation / scale, not replace them.
					go.transform.rotation =
						Quaternion.AngleAxis(inst.rotation * Mathf.Rad2Deg, Vector3.up) * prefab.transform.rotation;
					go.transform.localScale = Vector3.Scale(
						prefab.transform.localScale, new Vector3(inst.widthScale, inst.heightScale, inst.widthScale));
					spawned.Add(go);
				}
			}
			return spawned;
		}

		public static void Despawn(List<GameObject> spawned)
		{
			foreach (var go in spawned)
			{
				if (go)
				{
					UnityEngine.Object.DestroyImmediate(go);
				}
			}
		}
	}
}
