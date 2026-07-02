using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using cc;
using UnityEditor;

namespace Unity2Cocos
{
	public static class Converter
	{
		private static readonly Dictionary<Type, ComponentConverter> _componentConverters = new();
		private static readonly Dictionary<string, MaterialConverter> _materialConverters = new();
		private static readonly List<SceneNodeIdReplaceable> _sceneNodeIdReplaceableList = new();
		private static readonly Dictionary<int, int> _unityComponentToNodeId = new();
		private static readonly Dictionary<int, Vector3> _meshDefaultPositions = new();
		private static MonoBehaviourConverter _monoBehaviourConverter;

		public static void Initialize()
		{
			_sceneNodeIdReplaceableList.Clear();
			_unityComponentToNodeId.Clear();
			_meshDefaultPositions.Clear();
			MaterialConverter.ClearForceInstancing();
			
			// Component Converter
			_componentConverters.Clear();
			var converters = Utils.GetTypesIsSubclassOf<ComponentConverter>();
			foreach (var converter in converters)
			{
				var attribute = Utils.GetAttribute<ComponentConverterAttribute>(converter);
				if (attribute == null)
				{
					Debug.LogError($"[ComponentConverter] ComponentConverterAttribute is not assigned. -> {converter.Name}");
					continue;
				}
				_componentConverters.Add(attribute.Type, (ComponentConverter)Activator.CreateInstance(converter));
			}
			if (_componentConverters.TryGetValue(typeof(MonoBehaviour), out var monoBehaviourConverter))
			{
				_monoBehaviourConverter = monoBehaviourConverter as MonoBehaviourConverter;
				_monoBehaviourConverter?.Initialize(ExportSetting.Instance.ScriptMappers);
			}
			
			// Material Converter
			_materialConverters.Clear();
			converters = Utils.GetTypesIsSubclassOf<MaterialConverter>();
			foreach (var converter in converters)
			{
				var attribute = Utils.GetAttribute<MaterialConverterAttribute>(converter);
				if (attribute == null)
				{
					Debug.LogError($"[ComponentConverter] ComponentConverterAttribute is not assigned. -> {converter.Name}");
					continue;
				}
				_materialConverters.Add(attribute.Shader, (MaterialConverter)Activator.CreateInstance(converter));
			}
		}
		
		public static void ConvertHierarchy(Transform root, List<CCType> list)
		{
			ConvertTransformAndChildren(1, root, list);
		}

		private static void ConvertTransformAndChildren(int parent, Transform t, List<CCType> list)
		{
			var nodeId = list.Count;
			var hasRenderableMesh =
				t.TryGetComponent<UnityEngine.MeshFilter>(out var meshFilter) && meshFilter.sharedMesh &&
				t.TryGetComponent<UnityEngine.MeshRenderer>(out _);
			// FBX mesh display requires correction (LH -> RH 180 deg & FBX default position offset),
			// but it must not propagate to children.
			// If the node has children, isolate the correction into a dedicated child node.
			var useMeshCorrectionNode = hasRenderableMesh && t.childCount > 0;
			var node = TransformToNode(t, hasRenderableMesh && !useMeshCorrectionNode);
			node._parent = new SceneNodeId(parent);
			AddUnityComponentToNodeIdCache(t, list.Count);
			list.Add(node);

			var meshHostNodeId = nodeId;
			var meshHostNode = node;
			if (useMeshCorrectionNode)
			{
				meshHostNodeId = list.Count;
				meshHostNode = CreateMeshCorrectionNode(t, meshFilter, nodeId);
				node._children.Add(new SceneNodeId(meshHostNodeId));
				list.Add(meshHostNode);
			}

			// Cocos terrain extends toward +x/+z from its node origin. With the LH -> RH conversion
			// (z flip), Unity's terrain area [z, z + size.z] maps to [-(z + size.z), -z].
			// Host the Terrain component on an offset child node so that scene children
			// (e.g. spawned terrain trees) are not displaced.
			var terrainHostNodeId = nodeId;
			var terrainHostNode = node;
			if (t.TryGetComponent<UnityEngine.Terrain>(out var terrainComponent) && terrainComponent.terrainData)
			{
				terrainHostNodeId = list.Count;
				terrainHostNode = CreateTerrainOffsetNode(t, terrainComponent, nodeId);
				node._children.Add(new SceneNodeId(terrainHostNodeId));
				list.Add(terrainHostNode);
			}

			var transformPath = Utils.GetTransformPath(t);
			var components = t.GetComponents<UnityEngine.Component>();
			foreach (var component in components)
			{
				if (!component)
				{
					// GetComponents returns null for components whose script is missing.
					Debug.LogWarning(
						$"[Converter] Skipped of missing script component. -> {transformPath}");
					continue;
				}
				if (component is Transform)
				{
					continue;
				}

				var type = component.GetType();
				if (!_componentConverters.TryGetValue(type, out var converter))
				{
					if (!type.IsSubclassOf(typeof(MonoBehaviour)))
					{
						Debug.LogWarning(
							$"[Converter] Skipped of unsupported component. -> {transformPath}<{type.Name}>");
						continue;
					}
					converter = _monoBehaviourConverter;
				}

				var hostNodeId = nodeId;
				var hostNode = node;
				if (component is UnityEngine.MeshRenderer)
				{
					hostNodeId = meshHostNodeId;
					hostNode = meshHostNode;
				}
				else if (component is UnityEngine.Terrain)
				{
					hostNodeId = terrainHostNodeId;
					hostNode = terrainHostNode;
				}

				var results = converter.ConvertExecute(component, list.Count);
				var ccTypes = results as CCType[] ?? results.ToArray();
				if (!ccTypes.Any()) continue;

				AddUnityComponentToNodeIdCache(component, list.Count);
				foreach (var result in ccTypes)
				{
					if (result is cc.Component ccComponent)
					{
						ccComponent.node = new SceneNodeId(hostNodeId);
						hostNode._components.Add(new SceneNodeId(list.Count));
					}

					list.Add(result);
				}
			}

			for (var i = 0; i < t.childCount; ++i)
			{
				node._children.Add(new SceneNodeId(list.Count));
				ConvertTransformAndChildren(nodeId, t.GetChild(i), list);
			}
		}

		/// <summary>
		/// Node that hosts the Cocos Terrain component, offset by -size.z (in Cocos space) so the
		/// terrain area matches the z-flipped scene without displacing the scene children.
		/// </summary>
		private static Node CreateTerrainOffsetNode(Transform t, UnityEngine.Terrain terrain, int parentId)
		{
			return new Node
			{
				_name = $"{t.name} (Terrain)",
				_active = true,
				_parent = new SceneNodeId(parentId),
				_lpos = new Vec3 { x = 0, y = 0, z = -terrain.terrainData.size.z },
				_lrot = Quat.Identity,
				_lscale = new Vec3 { x = 1, y = 1, z = 1 },
				_mobility = t.gameObject.isStatic ? 0 : 2,
				_euler = Vec3.Zero,
				_layer = 1 << Utils.LayerConvert(t.gameObject.layer)
			};
		}

		/// <summary>
		/// Node that applies mesh display correction to its own mesh only, without affecting scene children.
		/// </summary>
		private static Node CreateMeshCorrectionNode(Transform t, UnityEngine.MeshFilter meshFilter, int parentId)
		{
			var p = -GetMeshDefaultPosition(meshFilter);
			var r = Quaternion.AngleAxis(180f, Vector3.up);
			return new Node
			{
				_name = $"{t.name} (Mesh)",
				_active = true,
				_parent = new SceneNodeId(parentId),
				_lpos = Utils.Vector3ToVec3(p.RightHanded()),
				_lrot = Utils.QuaternionToQuat(r),
				_lscale = new Vec3 { x = 1, y = 1, z = 1 },
				_mobility = t.gameObject.isStatic ? 0 : 2,
				_euler = Utils.EulerAnglesToVec3(r.eulerAngles),
				_layer = 1 << Utils.LayerConvert(t.gameObject.layer)
			};
		}
		
		private static Node TransformToNode(Transform t, bool applyMeshCorrection)
		{
			var p = t.localPosition;
			var r = t.localRotation;
			if (applyMeshCorrection && t.TryGetComponent<UnityEngine.MeshFilter>(out var meshFilter) && meshFilter.sharedMesh)
			{
				p -= GetMeshDefaultPosition(meshFilter);
				r *= Quaternion.AngleAxis(180f, Vector3.up);
			}

			if (t.TryGetComponent<UnityEngine.ReflectionProbe>(out var reflectionProbe))
			{
				// ReflectionProbe's Offset property does not exist, so let Node have it.
				var offset = reflectionProbe.bounds.center - t.position;
				if (t.parent)
				{
					offset = t.parent.rotation * offset;
				}
				p += new Vector3(offset.x, offset.y, -offset.z);
			}

			return new Node
			{
				_name = t.name,
				_active = t.gameObject.activeSelf,
				_lpos = Utils.Vector3ToVec3(p.RightHanded()),
				_lrot = Utils.QuaternionToQuat(r.RightHanded()),
				_lscale = new Vec3 { x = t.localScale.x, y = t.localScale.y, z = t.localScale.z },
				_mobility = t.gameObject.isStatic ? 0 : 2,
				_euler = Utils.EulerAnglesToVec3(r.RightHanded().eulerAngles),
				_layer = 1 << Utils.LayerConvert(t.gameObject.layer)
			};
		}

		/// <summary>
		/// In Cocos, meshes below FBX have a value of 0. (There are rare exceptions.)
		/// Instantiate Mesh, check initial coordinates, and take diff.
		/// </summary>
		private static Vector3 GetMeshDefaultPosition(UnityEngine.MeshFilter meshFilter)
		{
			var hash = meshFilter.sharedMesh.GetHashCode();
			if (_meshDefaultPositions.TryGetValue(hash, out var defaultPos))
			{
				return defaultPos;
			}
			var assetPath = AssetDatabase.GetAssetPath(meshFilter.sharedMesh);
			if (string.Equals(Path.GetExtension(assetPath), ".fbx", StringComparison.OrdinalIgnoreCase))
			{
				var root = AssetDatabase.LoadMainAssetAtPath(assetPath) as GameObject;
				if (root)
				{
					var obj = GameObject.Instantiate(root);
					var defaultMeshFilter = obj.GetComponentsInChildren<MeshFilter>()
						.FirstOrDefault(x => x.sharedMesh && x.sharedMesh.Equals(meshFilter.sharedMesh));
					if (defaultMeshFilter)
					{
						defaultPos = defaultMeshFilter.transform.localPosition;
					}
					GameObject.DestroyImmediate(obj);
				}
			}
			_meshDefaultPositions.Add(hash, defaultPos);
			return defaultPos;
		}

		public static void AddSceneNodeIdReplaceable(SceneNodeIdReplaceable replaceable)
		{
			_sceneNodeIdReplaceableList.Add(replaceable);
		}

		private static void AddUnityComponentToNodeIdCache(UnityEngine.Component component, int id)
		{
			var hash = component.GetHashCode();
			if (_unityComponentToNodeId.ContainsKey(hash))
			{
				return;
			}
			_unityComponentToNodeId.Add(hash, id);
		}
		
		public static void ApplySceneNodeIdReplaceable()
		{
			foreach (var replaceable in _sceneNodeIdReplaceableList)
			{
				if (_unityComponentToNodeId.TryGetValue(replaceable.TargetUnityComponent.GetHashCode(), out var id))
				{
					replaceable.__id__ = id;
				}
			}
			_sceneNodeIdReplaceableList.Clear();
		}

		public static cc.Material ConvertMaterial(UnityEngine.Material material)
		{
			var shader = material.shader.name;
			if (_materialConverters.TryGetValue(shader, out var converter))
			{
				return converter.Convert(material);
			}
			Debug.LogWarning(
				$"[Material] Unsupported shader, export standard material. -> {material.name}<{shader}>");
			return StandardMaterialConverter.GetStandardMaterial(material);
		}
	}
}
