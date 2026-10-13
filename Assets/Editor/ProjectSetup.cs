using LeGamez.Core;
using LeGamez.Editor.OsmImport;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace LeGamez.Editor
{
    /// <summary>
    /// One-shot project configuration: iOS player settings, URP quality, runtime materials.
    /// Menu: LeGamez/Setup Project. Batch: -executeMethod LeGamez.Editor.ProjectSetup.Run
    /// </summary>
    public static class ProjectSetup
    {
        public const string BundleId = "de.letzgo.legamez";
        public const string TeamId = "699G2QY959";

        [MenuItem("LeGamez/Setup Project")]
        public static void Run()
        {
            ConfigurePlayer();
            ConfigureUrp();
            CreateRuntimeMaterials();
            AssetDatabase.SaveAssets();
            Debug.Log("[LeGamez] Project setup done.");
        }

        static void ConfigurePlayer()
        {
            PlayerSettings.companyName = "letzgo";
            PlayerSettings.productName = "Nordisch";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, BundleId);
            PlayerSettings.bundleVersion = "0.1.0";
            PlayerSettings.iOS.buildNumber = "1";
            PlayerSettings.iOS.appleDeveloperTeamID = TeamId;
            PlayerSettings.iOS.appleEnableAutomaticSigning = true;
            PlayerSettings.iOS.targetOSVersionString = "16.0";
            PlayerSettings.iOS.targetDevice = iOSTargetDevice.iPhoneAndiPad;
            PlayerSettings.iOS.hideHomeButton = true;
            PlayerSettings.iOS.deferSystemGesturesMode = UnityEngine.iOS.SystemGestureDeferMode.BottomEdge;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.iOS, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.iOS, new[] { GraphicsDeviceType.Metal });
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.iOS, ManagedStrippingLevel.Low);
            ConfigureAndroid();
            ConfigureBranding();
        }

        /// <summary>Android player settings (same bundle id; ARM64 only, Vulkan with GLES3 fallback).</summary>
        public static void ConfigureAndroid()
        {
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, BundleId);
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 });
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Android, ManagedStrippingLevel.Low);
            PlayerSettings.Android.renderOutsideSafeArea = true; // UI already respects Screen.safeArea (notches)
            PlayerSettings.Android.startInFullscreen = true;
            PlayerSettings.Android.autoRotationBehavior = AndroidAutoRotationBehavior.User; // see AppOrientation
            PlayerSettings.Android.predictiveBackSupport = false; // back button must arrive as Escape (Hud)
        }

        const string LogoPath = "Assets/Resources/Branding/logo.png";

        /// <summary>App icon from the NORDisch logo, navy splash without the Unity logo.</summary>
        static void ConfigureBranding()
        {
            if (AssetImporter.GetAtPath(LogoPath) is TextureImporter ti)
            {
                ti.textureType = TextureImporterType.Default;
                ti.npotScale = TextureImporterNPOTScale.None;
                ti.mipmapEnabled = false;
                ti.isReadable = true; // runtime crops the badge as a sprite
                ti.alphaSource = TextureImporterAlphaSource.None;
                ti.textureCompression = TextureImporterCompression.Uncompressed;
                ti.maxTextureSize = 1024;
                ti.SaveAndReimport();
            }
            var logo = AssetDatabase.LoadAssetAtPath<Texture2D>(LogoPath);
            if (logo != null)
                PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { logo }, IconKind.Any);
            else
                Debug.LogWarning("[LeGamez] Logo missing at " + LogoPath);

            // Branding/wordmark.png is authored (Branding/concepts/make_concepts.py --final); only configure import.
            if (AssetImporter.GetAtPath(WordmarkPath) is TextureImporter wi)
            {
                wi.textureType = TextureImporterType.Sprite;
                wi.spriteImportMode = SpriteImportMode.Single; // full texture, never a stale sub-rect
                var tis = new TextureImporterSettings();
                wi.ReadTextureSettings(tis);
                tis.spriteMeshType = SpriteMeshType.FullRect; // tight meshes clip soft glyph edges
                wi.SetTextureSettings(tis);
                wi.npotScale = TextureImporterNPOTScale.None;
                wi.alphaIsTransparency = true;
                wi.mipmapEnabled = false;
                wi.textureCompression = TextureImporterCompression.Uncompressed;
                wi.SaveAndReimport();
            }

            var navy = new Color(0.102f, 0.227f, 0.306f);
            PlayerSettings.SplashScreen.showUnityLogo = false;
            PlayerSettings.SplashScreen.show = false;
            PlayerSettings.SplashScreen.backgroundColor = navy;
        }

        const string WordmarkPath = "Assets/Resources/Branding/wordmark.png";

        static void ConfigureUrp()
        {
            var pipelines = new System.Collections.Generic.List<RenderPipelineAsset>();
            if (GraphicsSettings.defaultRenderPipeline != null) pipelines.Add(GraphicsSettings.defaultRenderPipeline);
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                var rp = QualitySettings.GetRenderPipelineAssetAt(i);
                if (rp != null && !pipelines.Contains(rp)) pipelines.Add(rp);
            }
            if (pipelines.Count == 0)
            {
                Debug.LogWarning("[LeGamez] No URP asset assigned – project was not created from the URP template.");
                return;
            }
            foreach (var rp in pipelines)
            {
                if (rp is not UniversalRenderPipelineAsset urp) continue;
                urp.shadowDistance = 110f;
                urp.shadowCascadeCount = 2;
                urp.msaaSampleCount = 4;
                urp.renderScale = 1f;
                urp.supportsHDR = true; // subtle bloom on sun-lit glass/water
                var so = new SerializedObject(urp);
                SetInt(so, "m_MainLightShadowmapResolution", 2048);
                SetBool(so, "m_MainLightShadowsSupported", true);
                SetBool(so, "m_SoftShadowsSupported", true);
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(urp);

                foreach (var data in RendererDatas(urp)) EnsureSsao(data);
            }
        }

        static void SetInt(SerializedObject so, string name, int v)
        {
            var p = so.FindProperty(name);
            if (p != null) p.intValue = v; else Debug.LogWarning($"[LeGamez] URP property {name} not found");
        }

        static void SetBool(SerializedObject so, string name, bool v)
        {
            var p = so.FindProperty(name);
            if (p != null) p.boolValue = v; else Debug.LogWarning($"[LeGamez] URP property {name} not found");
        }

        static System.Collections.Generic.IEnumerable<ScriptableRendererData> RendererDatas(UniversalRenderPipelineAsset urp)
        {
            var list = new SerializedObject(urp).FindProperty("m_RendererDataList");
            for (int i = 0; list != null && i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).objectReferenceValue is ScriptableRendererData d) yield return d;
        }

        /// <summary>Adds URP's Screen Space Ambient Occlusion feature (internal type, so via reflection).</summary>
        static void EnsureSsao(ScriptableRendererData data)
        {
            foreach (var f in data.rendererFeatures)
                if (f != null && f.GetType().Name == "ScreenSpaceAmbientOcclusion") return;
            var type = typeof(UniversalRenderPipelineAsset).Assembly.GetType("UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion");
            if (type == null) { Debug.LogWarning("[LeGamez] SSAO feature type not found"); return; }
            var feature = (ScriptableRendererFeature)ScriptableObject.CreateInstance(type);
            feature.name = "SSAO";
            AssetDatabase.AddObjectToAsset(feature, data);
            data.rendererFeatures.Add(feature);

            // Keep the serialized feature map in sync (normally done by the renderer data inspector).
            var so = new SerializedObject(data);
            var map = so.FindProperty("m_RendererFeatureMap");
            if (map != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId))
            {
                map.arraySize++;
                map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            // Mobile-friendly settings: half resolution, moderate radius.
            var settingsField = type.GetField("m_Settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var settings = settingsField?.GetValue(feature);
            if (settings != null)
            {
                void Set(string field, object value)
                {
                    var fi = settings.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (fi != null && fi.FieldType == value.GetType()) fi.SetValue(settings, value);
                }
                Set("Intensity", 1.2f);
                Set("Radius", 0.4f);
                Set("Downsample", true);
                settingsField.SetValue(feature, settings);
            }
            data.SetDirty();
            EditorUtility.SetDirty(data);
            Debug.Log($"[LeGamez] SSAO added to {data.name}");
        }

        static VolumeProfile CreatePostProfile()
        {
            string path = "Assets/Generated/PostFX.asset";
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, path);
            }
            T Get<T>() where T : VolumeComponent
            {
                if (!profile.TryGet<T>(out var c))
                {
                    c = profile.Add<T>(true);
                    c.name = typeof(T).Name;
                    AssetDatabase.AddObjectToAsset(c, profile);
                }
                return c;
            }
            var tone = Get<Tonemapping>();
            tone.mode.Override(TonemappingMode.Neutral);
            var color = Get<ColorAdjustments>();
            color.postExposure.Override(0.15f);
            color.contrast.Override(14f);
            color.saturation.Override(12f);
            var wb = Get<WhiteBalance>();
            wb.temperature.Override(6f);
            var bloom = Get<Bloom>();
            bloom.threshold.Override(1.05f);
            bloom.intensity.Override(0.35f);
            bloom.scatter.Override(0.6f);
            bloom.highQualityFiltering.Override(false);
            var vignette = Get<Vignette>();
            vignette.intensity.Override(0.18f);
            vignette.smoothness.Override(0.45f);
            EditorUtility.SetDirty(profile);
            return profile;
        }

        static void CreateRuntimeMaterials()
        {
            OsmAreaImporter.EnsureFolder("Assets/Resources");
            OsmAreaImporter.EnsureFolder("Assets/Generated/Materials");
            string path = $"Assets/Resources/{GameMaterials.ResourcePath}.asset";
            var gm = AssetDatabase.LoadAssetAtPath<GameMaterials>(path);
            if (gm == null)
            {
                gm = ScriptableObject.CreateInstance<GameMaterials>();
                AssetDatabase.CreateAsset(gm, path);
            }
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            gm.playerBody = Mat("Player_Body", lit, new Color(0.15f, 0.45f, 0.85f));
            gm.playerSkin = Mat("Player_Skin", lit, new Color(0.96f, 0.80f, 0.67f));
            gm.playerAccent = Mat("Player_Accent", lit, new Color(0.9f, 0.4f, 0.3f));
            gm.poiOpen = Mat("Poi_Open", unlit, new Color(0.89f, 0.66f, 0.17f));   // brand mustard
            gm.poiDone = Mat("Poi_Done", unlit, new Color(0.31f, 0.60f, 0.42f));
            gm.postProfile = CreatePostProfile();
            gm.pedestalMaterial = Mat("Pedestal", lit, new Color(0.93f, 0.89f, 0.80f));
            gm.vehicleMaterial = Mat("Vehicle", Shader.Find("LeGamez/VertexColorLit"), Color.white);
            gm.vehicleMaterial.SetColor("_Tint", Color.white);
            CharacterSetup.Run(gm);
            EditorUtility.SetDirty(gm);
        }

        static Material Mat(string name, Shader shader, Color color)
        {
            string path = $"Assets/Generated/Materials/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(m, path);
            }
            m.shader = shader;
            m.SetColor("_BaseColor", color);
            m.color = color;
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}
