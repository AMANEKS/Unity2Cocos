using System;
using System.Collections.Generic;
using cc;
using UnityEditor;
using UnityEngine;

namespace cc
{
	public class ParticleSystem : Component
	{
		public AssetReference<Material>[] _materials = Array.Empty<AssetReference<Material>>();
		public object startColor;
		public int scaleSpace;
		public bool startSize3D;
		public object startSizeX;
		public object startSizeY;
		public object startSizeZ;
		public object startSpeed;
		public bool startRotation3D;
		public object startRotationX;
		public object startRotationY;
		public object startRotationZ;
		public object startDelay;
		public object startLifetime;
		public float duration;
		public bool loop;
		public float simulationSpeed = 1f;
		public bool playOnAwake = true;
		public object gravityModifier;
		public object rateOverTime;
		public object rateOverDistance;
		public object[] bursts = Array.Empty<object>();
		public object _colorOverLifetimeModule;
		public object _shapeModule;
		public object _sizeOvertimeModule;
		public object _velocityOvertimeModule;
		public object _rotationOvertimeModule;
		public object _textureAnimationModule;
		public object renderer;
		public bool _prewarm;
		public int _capacity;
		public int _simulationSpace;
	}
}

namespace Unity2Cocos
{
	[ComponentConverter(typeof(UnityEngine.ParticleSystem))]
	public class ParticleSystemConverter : ComponentConverter<UnityEngine.ParticleSystem>
	{
		protected override IEnumerable<CCType> Convert(UnityEngine.ParticleSystem ps, int currentId)
		{
			var main = ps.main;
			var emission = ps.emission;
			var psRenderer = ps.GetComponent<UnityEngine.ParticleSystemRenderer>();
			var path = Utils.GetTransformPath(ps.transform);

			var ccPs = new cc.ParticleSystem
			{
				duration = main.duration,
				loop = main.loop,
				_prewarm = main.prewarm,
				simulationSpeed = main.simulationSpeed,
				playOnAwake = main.playOnAwake,
				_capacity = main.maxParticles,
				// Unity: Local=0, World=1 / Cocos: World=0, Local=1
				_simulationSpace = main.simulationSpace == ParticleSystemSimulationSpace.World ? 0 : 1,
				scaleSpace = 1,
				startDelay = ParticleData.Curve(main.startDelay),
				startLifetime = ParticleData.Curve(main.startLifetime),
				startSpeed = ParticleData.Curve(main.startSpeed),
				startColor = ParticleData.GradientRange(main.startColor),
				gravityModifier = ParticleData.Curve(main.gravityModifier),
				startSize3D = main.startSize3D,
				startSizeX = ParticleData.Curve(main.startSize3D ? main.startSizeX : main.startSize),
				startSizeY = ParticleData.Curve(main.startSizeY),
				startSizeZ = ParticleData.Curve(main.startSizeZ),
				startRotation3D = main.startRotation3D,
				startRotationX = ParticleData.Curve(main.startRotationX, Mathf.Rad2Deg),
				startRotationY = ParticleData.Curve(main.startRotationY, Mathf.Rad2Deg),
				startRotationZ = ParticleData.Curve(
					main.startRotation3D ? main.startRotationZ : main.startRotation, Mathf.Rad2Deg),
				rateOverTime = ParticleData.Curve(emission.rateOverTime),
				rateOverDistance = ParticleData.Curve(emission.rateOverDistance),
			};

			// Bursts
			if (emission.burstCount > 0)
			{
				var bursts = new UnityEngine.ParticleSystem.Burst[emission.burstCount];
				emission.GetBursts(bursts);
				var ccBursts = new List<object>();
				foreach (var b in bursts)
				{
					ccBursts.Add(new Dictionary<string, object>
					{
						{ "__type__", "cc.Burst" },
						{ "_time", b.time },
						{ "_repeatCount", b.cycleCount == 0 ? 9999 : b.cycleCount },
						{ "_repeatInterval", b.repeatInterval },
						{ "count", ParticleData.Curve(b.count) },
					});
				}
				ccPs.bursts = ccBursts.ToArray();
			}

			// Modules
			ccPs._shapeModule = ConvertShape(ps.shape, path);
			if (ps.colorOverLifetime.enabled)
			{
				ccPs._colorOverLifetimeModule = new Dictionary<string, object>
				{
					{ "__type__", "cc.ColorOvertimeModule" },
					{ "_enable", true },
					{ "color", ParticleData.GradientRange(ps.colorOverLifetime.color) },
				};
			}
			if (ps.sizeOverLifetime.enabled)
			{
				ccPs._sizeOvertimeModule = new Dictionary<string, object>
				{
					{ "__type__", "cc.SizeOvertimeModule" },
					{ "_enable", true },
					{ "separateAxes", false },
					{ "size", ParticleData.Curve(ps.sizeOverLifetime.size) },
				};
			}
			if (ps.velocityOverLifetime.enabled)
			{
				var vel = ps.velocityOverLifetime;
				var worldSpace = vel.space == ParticleSystemSimulationSpace.World;
				ccPs._velocityOvertimeModule = new Dictionary<string, object>
				{
					{ "__type__", "cc.VelocityOvertimeModule" },
					{ "_enable", true },
					{ "x", ParticleData.Curve(vel.x) },
					{ "y", ParticleData.Curve(vel.y) },
					// World space velocity needs the LH -> RH z flip.
					{ "z", ParticleData.Curve(vel.z, worldSpace ? -1f : 1f) },
					{ "space", worldSpace ? 0 : 1 },
					{ "speedModifier", ParticleData.Curve(vel.speedModifier) },
				};
			}
			if (ps.rotationOverLifetime.enabled)
			{
				var rot = ps.rotationOverLifetime;
				var module = new Dictionary<string, object>
				{
					{ "__type__", "cc.RotationOvertimeModule" },
					{ "_enable", true },
					{ "_separateAxes", rot.separateAxes },
					{ "z", ParticleData.Curve(rot.z, Mathf.Rad2Deg) },
				};
				if (rot.separateAxes)
				{
					module.Add("x", ParticleData.Curve(rot.x, Mathf.Rad2Deg));
					module.Add("y", ParticleData.Curve(rot.y, Mathf.Rad2Deg));
				}
				ccPs._rotationOvertimeModule = module;
			}
			if (ps.textureSheetAnimation.enabled)
			{
				var tex = ps.textureSheetAnimation;
				ccPs._textureAnimationModule = new Dictionary<string, object>
				{
					{ "__type__", "cc.TextureAnimationModule" },
					{ "_enable", true },
					{ "_numTilesX", tex.numTilesX },
					{ "_numTilesY", tex.numTilesY },
					// Unity: WholeSheet=0, SingleRow=1 (same as Cocos)
					{ "animation", (int)tex.animation },
					{ "frameOverTime", ParticleData.Curve(tex.frameOverTime) },
					{ "startFrame", ParticleData.Curve(tex.startFrame) },
					{ "cycleCount", tex.cycleCount },
				};
			}
			if (ps.trails.enabled)
			{
				Debug.LogWarning($"[ParticleSystemConverter] Trail module is not supported. -> {path}");
			}
			if (ps.noise.enabled)
			{
				Debug.LogWarning($"[ParticleSystemConverter] Noise module is not supported. -> {path}");
			}

			// Renderer
			var renderer = new Dictionary<string, object>
			{
				{ "__type__", "cc.ParticleSystemRenderer" },
				{ "_renderMode", 0 },
				{ "_velocityScale", 1f },
				{ "_lengthScale", 1f },
				{ "_mesh", null },
				{ "_mainTexture", null },
				{ "_useGPU", false },
			};
			if (psRenderer)
			{
				// Unity: Billboard=0, Stretch=1, HorizontalBillboard=2, VerticalBillboard=3, Mesh=4 (same as Cocos)
				var renderMode = (int)psRenderer.renderMode;
				if (renderMode == (int)ParticleSystemRenderMode.None)
				{
					renderMode = 0;
				}
				renderer["_renderMode"] = renderMode;
				renderer["_velocityScale"] = psRenderer.velocityScale;
				renderer["_lengthScale"] = psRenderer.lengthScale;
				if (psRenderer.renderMode == ParticleSystemRenderMode.Mesh && psRenderer.mesh)
				{
					renderer["_mesh"] = new AssetReference<cc.Mesh>(Exporter.GetUuidOrExportAsset(psRenderer.mesh));
				}
				var material = psRenderer.sharedMaterial;
				if (material)
				{
					var uuid = ParticleMaterialExporter.Export(material);
					if (!string.IsNullOrEmpty(uuid))
					{
						ccPs._materials = new[] { new AssetReference<cc.Material>(uuid) };
					}
				}
			}
			ccPs.renderer = renderer;

			return new CCType[] { ccPs };
		}

		private static object ConvertShape(UnityEngine.ParticleSystem.ShapeModule shape, string path)
		{
			if (!shape.enabled)
			{
				return null;
			}
			// Unity ShapeType -> Cocos (shapeType, emitFrom). Cocos: Box=0, Circle=1, Cone=2, Sphere=3, Hemisphere=4
			// Cocos EmitLocation: Base=0, Edge=1, Shell=2, Volume=3
			int shapeType;
			var emitFrom = 3; // Volume
			switch (shape.shapeType)
			{
				case ParticleSystemShapeType.Sphere: shapeType = 3; break;
				case ParticleSystemShapeType.SphereShell: shapeType = 3; emitFrom = 2; break;
				case ParticleSystemShapeType.Hemisphere: shapeType = 4; break;
				case ParticleSystemShapeType.HemisphereShell: shapeType = 4; emitFrom = 2; break;
				case ParticleSystemShapeType.Cone: shapeType = 2; emitFrom = 0; break;
				case ParticleSystemShapeType.ConeShell: shapeType = 2; emitFrom = 2; break;
				case ParticleSystemShapeType.ConeVolume: shapeType = 2; break;
				case ParticleSystemShapeType.ConeVolumeShell: shapeType = 2; emitFrom = 2; break;
				case ParticleSystemShapeType.Box: shapeType = 0; break;
				case ParticleSystemShapeType.BoxShell: shapeType = 0; emitFrom = 2; break;
				case ParticleSystemShapeType.BoxEdge: shapeType = 0; emitFrom = 1; break;
				case ParticleSystemShapeType.Circle: shapeType = 1; break;
				case ParticleSystemShapeType.CircleEdge: shapeType = 1; emitFrom = 1; break;
				default:
					Debug.LogWarning(
						$"[ParticleSystemConverter] Unsupported shape type '{shape.shapeType}', using Sphere. -> {path}");
					shapeType = 3;
					break;
			}
			return new Dictionary<string, object>
			{
				{ "__type__", "cc.ShapeModule" },
				{ "_enable", true },
				{ "_shapeType", shapeType },
				{ "emitFrom", emitFrom },
				{ "_angle", shape.angle * Mathf.Deg2Rad },
				{ "_arc", shape.arc * Mathf.Deg2Rad },
				{ "radius", shape.radius },
				{ "radiusThickness", shape.radiusThickness },
				{ "length", shape.length },
				{ "boxThickness", Utils.Vector3ToVec3(shape.boxThickness) },
				{ "_position", Utils.Vector3ToVec3(shape.position) },
				{ "_rotation", Utils.Vector3ToVec3(shape.rotation) },
				{ "_scale", Utils.Vector3ToVec3(shape.scale) },
				{ "alignToDirection", shape.alignToDirection },
				{ "randomDirectionAmount", shape.randomDirectionAmount },
				{ "sphericalDirectionAmount", shape.sphericalDirectionAmount },
				{ "randomPositionAmount", shape.randomPositionAmount },
			};
		}
	}

	[ComponentConverter(typeof(UnityEngine.ParticleSystemRenderer))]
	public class ParticleSystemRendererConverter : ComponentConverter<UnityEngine.ParticleSystemRenderer>
	{
		protected override IEnumerable<CCType> Convert(UnityEngine.ParticleSystemRenderer component, int currentId)
		{
			// Integrated into ParticleSystemConverter.
			return Array.Empty<CCType>();
		}
	}

	/// <summary>
	/// Builders for Cocos particle data structures. (CurveRange / GradientRange / Gradient)
	/// Emits only the keys relevant to each mode so that engine defaults are not overwritten.
	/// </summary>
	public static class ParticleData
	{
		public static object Curve(UnityEngine.ParticleSystem.MinMaxCurve curve, float scale = 1f)
		{
			var dict = new Dictionary<string, object> { { "__type__", "cc.CurveRange" } };
			switch (curve.mode)
			{
				case ParticleSystemCurveMode.TwoConstants:
					dict.Add("mode", 3);
					dict.Add("constantMin", curve.constantMin * scale);
					dict.Add("constantMax", curve.constantMax * scale);
					break;
				case ParticleSystemCurveMode.Curve:
					// NOTE: Curve keyframes are not converted, approximate with the average value.
					dict.Add("mode", 0);
					dict.Add("constant", AverageCurve(curve.curve) * curve.curveMultiplier * scale);
					break;
				case ParticleSystemCurveMode.TwoCurves:
					dict.Add("mode", 3);
					dict.Add("constantMin", AverageCurve(curve.curveMin) * curve.curveMultiplier * scale);
					dict.Add("constantMax", AverageCurve(curve.curveMax) * curve.curveMultiplier * scale);
					break;
				default: // Constant
					dict.Add("mode", 0);
					dict.Add("constant", curve.constant * scale);
					break;
			}
			dict.Add("multiplier", 1f);
			return dict;
		}

		private static float AverageCurve(AnimationCurve curve)
		{
			if (curve == null || curve.length == 0)
			{
				return 0f;
			}
			var sum = 0f;
			const int samples = 8;
			for (var i = 0; i < samples; ++i)
			{
				sum += curve.Evaluate(i / (float)(samples - 1));
			}
			return sum / samples;
		}

		public static object GradientRange(UnityEngine.ParticleSystem.MinMaxGradient gradient)
		{
			var dict = new Dictionary<string, object> { { "__type__", "cc.GradientRange" } };
			switch (gradient.mode)
			{
				case ParticleSystemGradientMode.TwoColors:
					dict.Add("_mode", 2);
					dict.Add("colorMin", Utils.Color32ToCocosColor(gradient.colorMin));
					dict.Add("colorMax", Utils.Color32ToCocosColor(gradient.colorMax));
					break;
				case ParticleSystemGradientMode.Gradient:
					dict.Add("_mode", 1);
					dict.Add("gradient", GradientDict(gradient.gradient));
					break;
				case ParticleSystemGradientMode.TwoGradients:
					dict.Add("_mode", 3);
					dict.Add("gradientMin", GradientDict(gradient.gradientMin));
					dict.Add("gradientMax", GradientDict(gradient.gradientMax));
					break;
				case ParticleSystemGradientMode.RandomColor:
					dict.Add("_mode", 4);
					dict.Add("gradient", GradientDict(gradient.gradient));
					break;
				default: // Color
					dict.Add("_mode", 0);
					dict.Add("color", Utils.Color32ToCocosColor(gradient.color));
					break;
			}
			return dict;
		}

		private static object GradientDict(Gradient gradient)
		{
			var colorKeys = new List<object>();
			var alphaKeys = new List<object>();
			if (gradient != null)
			{
				foreach (var key in gradient.colorKeys)
				{
					colorKeys.Add(new Dictionary<string, object>
					{
						{ "__type__", "cc.ColorKey" },
						{ "color", Utils.Color32ToCocosColor(key.color) },
						{ "time", key.time },
					});
				}
				foreach (var key in gradient.alphaKeys)
				{
					alphaKeys.Add(new Dictionary<string, object>
					{
						{ "__type__", "cc.AlphaKey" },
						{ "alpha", key.alpha },
						{ "time", key.time },
					});
				}
			}
			return new Dictionary<string, object>
			{
				{ "__type__", "cc.Gradient" },
				{ "colorKeys", colorKeys },
				{ "alphaKeys", alphaKeys },
				{ "mode", 0 },
			};
		}
	}

	/// <summary>
	/// Exports a Unity particle material as a Cocos material using the builtin-particle effect.
	/// </summary>
	public static class ParticleMaterialExporter
	{
		private const string BuiltinParticleEffectUuid = "d1346436-ac96-4271-b863-1f4fdead95b0";

		private static readonly Dictionary<int, string> _cache = new();

		public static void ClearCache()
		{
			_cache.Clear();
		}

		private class Meta : cc.Meta
		{
			public Meta()
			{
				ver = "1.0.21";
				importer = "material";
			}
		}

		public static string Export(UnityEngine.Material material)
		{
			if (_cache.TryGetValue(material.GetHashCode(), out var cached))
			{
				return cached;
			}
			var srcPath = AssetDatabase.GetAssetPath(material);
			if (string.IsNullOrEmpty(srcPath))
			{
				return string.Empty;
			}

			var ccMat = MaterialConverter.CreateMaterial(material, BuiltinParticleEffectUuid, 1);
			// builtin-particle techniques: 0 = add, 1 = alpha-blend
			ccMat._techIdx = IsAdditive(material) ? 0 : 1;

			var prop = ccMat._props[0];
			Texture mainTexture = null;
			if (material.HasTexture("_MainTex") && material.GetTexture("_MainTex"))
			{
				mainTexture = material.GetTexture("_MainTex");
			}
			else if (material.HasTexture("_BaseMap") && material.GetTexture("_BaseMap"))
			{
				mainTexture = material.GetTexture("_BaseMap");
			}
			else if (URPMaterialConverter.TryFindCustomMainTexture(material, out _, out var customTex))
			{
				mainTexture = customTex;
			}
			if (mainTexture)
			{
				prop.Add("mainTexture", new AssetReference<cc.Texture2D>(Exporter.GetUuidOrExportAsset(mainTexture)));
			}

			// The builtin-particle shader multiplies the tint color by 2.
			var tint = UnityEngine.Color.white;
			if (material.HasColor("_BaseColor"))
			{
				tint = material.GetColor("_BaseColor");
			}
			else if (material.HasColor("_Color"))
			{
				tint = material.GetColor("_Color");
			}
			else if (URPMaterialConverter.TryFindCustomMainColor(material, out var customColor))
			{
				tint = customColor;
			}
			prop.Add("tintColor", new Vec4
			{
				x = tint.r * 0.5f,
				y = tint.g * 0.5f,
				z = tint.b * 0.5f,
				w = Mathf.Clamp01(tint.a) * 0.5f
			});

			var info = new AssetExporter.ExportInfo(srcPath, Exporter.OutputFolderPath, ".mtl");
			var ccMeta = new Meta();
			AssetExporter.ExportAssetToJson(ccMat, info);
			AssetExporter.ExportMeta(ccMeta, info);
			_cache.Add(material.GetHashCode(), ccMeta.uuid);
			return ccMeta.uuid;
		}

		private static bool IsAdditive(UnityEngine.Material material)
		{
			// Blend DstFactor == One means additive.
			if (material.HasFloat("_DstBlend"))
			{
				return material.GetInt("_DstBlend") == (int)UnityEngine.Rendering.BlendMode.One;
			}
			var name = (material.shader ? material.shader.name : string.Empty).ToLowerInvariant();
			return name.Contains("add") || material.name.ToLowerInvariant().Contains("glow");
		}
	}
}
