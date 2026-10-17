using System.IO;
using UnityEditor;
using UnityEngine;

namespace GoreLab
{
    /// <summary>
    /// The default blood of a Gore Profile: a Particle System prefab that spurts in pulses (a heartbeat that weakens) and then runs, made once
    /// in <c>Content/Prefabs</c> with its material in <c>Content/Materials</c>. A new profile gets it; an older one gets it from the button of its
    /// Inspector. Tweak the prefab freely or give the profile another one: it only has to be a Particle System that points along its forward.
    /// </summary>
    internal static class GoreBloodAssets
    {
        private const string PrefabPath = "Assets/Scripts/GoreLab/Content/Prefabs/Gore Blood.prefab";
        private const string MaterialPath = "Assets/Scripts/GoreLab/Content/Materials/Gore Blood.mat";
        private const string ShaderName = "Universal Render Pipeline/Particles/Unlit";
        private const float Duration = 8f;
        private const int Pulses = 10;

        /// <summary>Gives an earlier default blood material its texture back when it lost it (does nothing when there is no such material).</summary>
        internal static void Mend()
        {
            if (AssetDatabase.LoadAssetAtPath<Material>(MaterialPath) != null)
            {
                DefaultMaterial();
            }
        }

        /// <summary>The default blood prefab, made when it is missing.</summary>
        internal static GameObject DefaultBlood()
        {
            // The material first: an earlier one that lost its texture is mended even when the prefab already exists.
            DefaultMaterial();
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (existing != null)
            {
                return existing;
            }

            EnsureFolder(Path.GetDirectoryName(PrefabPath).Replace('\\', '/'));
            var go = new GameObject("Gore Blood");
            var system = go.AddComponent<ParticleSystem>();
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = system.main;
            main.duration = Duration;
            main.loop = false;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Local;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.7f, 1.3f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.2f, 3.2f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.025f, 0.06f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.45f, 0.01f, 0.02f), new Color(0.7f, 0.04f, 0.05f));
            main.gravityModifier = 1f;
            main.maxParticles = 500;

            // A heartbeat: the blood leaves in pulses that weaken as the time goes on, with a thin run between them.
            var emission = system.emission;
            emission.rateOverTime = new ParticleSystem.MinMaxCurve(150f, PulseCurve());

            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 14f;
            shape.radius = 0.03f;

            // Blood that reaches the ground, a wall or the body is gone.
            var collision = system.collision;
            collision.enabled = true;
            collision.type = ParticleSystemCollisionType.World;
            collision.mode = ParticleSystemCollisionMode.Collision3D;
            collision.quality = ParticleSystemCollisionQuality.Low;
            collision.maxCollisionShapes = 16;
            collision.dampen = 0.3f;
            collision.bounce = 0f;
            collision.lifetimeLoss = 1f;

            var colour = system.colorOverLifetime;
            colour.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.75f), new GradientAlphaKey(0f, 1f) });
            colour.color = new ParticleSystem.MinMaxGradient(gradient);

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = 1.5f;
            renderer.velocityScale = 0.04f;
            renderer.sharedMaterial = DefaultMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var prefab = PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
            Object.DestroyImmediate(go);
            AssetDatabase.SaveAssets();
            return prefab;
        }

        // Pulses over the duration (0 to 1): a peak and a valley for each, the peaks fading.
        private static AnimationCurve PulseCurve()
        {
            var curve = new AnimationCurve();
            for (var i = 0; i < Pulses; i++)
            {
                var fade = Mathf.Exp(-i * 0.3f);
                curve.AddKey((i + 0.25f) / Pulses, fade);
                curve.AddKey((i + 0.7f) / Pulses, 0.08f * fade);
            }

            curve.AddKey(1f, 0f);
            return curve;
        }

        private static Material DefaultMaterial()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material != null)
            {
                if (material.HasProperty("_BaseMap") && material.GetTexture("_BaseMap") == null)
                {
                    material.SetTexture("_BaseMap", SoftDot());
                    EditorUtility.SetDirty(material);
                }

                return material;
            }

            EnsureFolder(Path.GetDirectoryName(MaterialPath).Replace('\\', '/'));
            var shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                Debug.LogWarning("[GoreLab] The '" + ShaderName + "' shader was not found (the Universal Render Pipeline is missing?): the blood falls back to a plain particle shader. Assign your own material to the prefab.");
                shader = Shader.Find("Sprites/Default");
            }

            material = new Material(shader) { name = "Gore Blood" };
            if (material.HasProperty("_BaseMap"))
            {
                material.SetTexture("_BaseMap", SoftDot());
            }

            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", Color.white);
            }

            // Alpha blended, drawn after the opaque things, writing no depth.
            if (material.HasProperty("_Surface"))
            {
                material.SetFloat("_Surface", 1f);
                material.SetFloat("_Blend", 0f);
                material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetFloat("_ZWrite", 0f);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.SetOverrideTag("RenderType", "Transparent");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }

            AssetDatabase.CreateAsset(material, MaterialPath);
            return material;
        }

        // Unity's own soft round particle texture (an editor built-in resource, found through the AssetDatabase).
        private static Texture2D SoftDot() => AssetDatabase.GetBuiltinExtraResource<Texture2D>("Default-Particle.psd");

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
            {
                return;
            }

            var parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolder(parent);
            }

            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }
}
