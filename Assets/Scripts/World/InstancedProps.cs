using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LeGamez.World
{
    /// <summary>
    /// Trees, bushes and lamps of one world tile (<see cref="WorldTile.instances"/>): drawn every frame with
    /// Graphics.RenderMeshInstanced – one call per prototype (and per 1023 instances). Before submitting, each batch is
    /// culled on the CPU against the main camera (frustum widened by <see cref="ShadowMargin"/> so off-screen trees still
    /// cast their shadows into view, and <see cref="MaxDistance"/> – fog ends at 650 m); batches beyond
    /// <see cref="ShadowDistance"/> are drawn without shadow casting (URP draws no shadows that far anyway), beyond
    /// <see cref="FarDistance"/> with simple stand-in meshes. Per-instance colour goes in as
    /// <c>_InstanceTint</c> (VertexColorLit, instancing variant). Trunks and lamp poles block the player with capsule
    /// colliders on a child object (layer 2, like the former MeshColliders).
    /// Outside play mode (editor snapshots, thumbnails: camera.Render doesn't see queued instanced draws) the instances
    /// are baked into ordinary meshes instead – same shapes and colours.
    /// </summary>
    public class InstancedProps : MonoBehaviour
    {
        const int MaxPerCall = 1023;
        const float MaxDistance = 700f, ShadowDistance = 140f, ShadowMargin = 25f;
        /// <summary>Beyond this, crowns/bushes are drawn as simple stand-ins (InstancePrototypes.GetFar), trunks and lamps not at all.</summary>
        const float FarDistance = 250f;
        static readonly int TintId = Shader.PropertyToID("_InstanceTint");

        class Batch
        {
            public Mesh mesh, far;
            public RenderParams rp, rpNoShadow;
            public Matrix4x4[] matrices;
        }

        readonly List<Batch> batches = new List<Batch>();
        readonly List<Mesh> baked = new List<Mesh>();

        /// <summary>Draw calls this tile issues per camera pass (before Unity's bounds culling).</summary>
        public int BatchCount => batches.Count;

        /// <summary>Adds the instances of a tile under <paramref name="parent"/>; null if there are none.</summary>
        public static InstancedProps Create(Transform parent, List<WorldTile.Instance> list, IList<Material> materials, HideFlags hide = HideFlags.None)
        {
            if (list == null || list.Count == 0) return null;
            var go = new GameObject("Instances") { hideFlags = hide, layer = InstancePrototypes.ColliderLayer };
            go.transform.SetParent(parent, false);
            var props = go.AddComponent<InstancedProps>();
            props.AddColliders(list);
            if (Application.isPlaying) props.BuildBatches(list, materials);
            else props.Bake(list, materials, hide);
            return props;
        }

        static Material MaterialOf(int proto, IList<Material> materials)
        {
            int mi = InstancePrototypes.MaterialOf(proto);
            return materials != null && mi < materials.Count ? materials[mi] : null;
        }

        void BuildBatches(List<WorldTile.Instance> list, IList<Material> materials)
        {
            // Group by prototype (stable order: proto, then file order).
            var byProto = new List<WorldTile.Instance>[InstancePrototypes.Count];
            foreach (var i in list)
            {
                if (i.proto >= byProto.Length) continue;
                (byProto[i.proto] ??= new List<WorldTile.Instance>()).Add(i);
            }
            for (int p = 0; p < byProto.Length; p++)
            {
                var group = byProto[p];
                if (group == null) continue;
                var mesh = InstancePrototypes.Get(p);
                var mat = MaterialOf(p, materials);
                if (mesh == null || mat == null) continue;
                for (int start = 0; start < group.Count; start += MaxPerCall)
                {
                    int n = Mathf.Min(MaxPerCall, group.Count - start);
                    var matrices = new Matrix4x4[n];
                    var tints = new Vector4[n];
                    var bounds = new Bounds();
                    for (int k = 0; k < n; k++)
                    {
                        var inst = group[start + k];
                        matrices[k] = InstancePrototypes.Matrix(inst);
                        tints[k] = InstancePrototypes.TintOf(inst);
                        var b = TransformBounds(mesh.bounds, matrices[k]);
                        if (k == 0) bounds = b; else bounds.Encapsulate(b);
                    }
                    var block = new MaterialPropertyBlock();
                    block.SetVectorArray(TintId, tints);
                    var rp = new RenderParams(mat)
                    {
                        layer = 0,
                        shadowCastingMode = ShadowCastingMode.On,
                        receiveShadows = true,
                        lightProbeUsage = LightProbeUsage.BlendProbes,
                        worldBounds = bounds,
                        matProps = block,
                    };
                    var noShadow = rp;
                    noShadow.shadowCastingMode = ShadowCastingMode.Off;
                    batches.Add(new Batch { mesh = mesh, far = InstancePrototypes.GetFar(p), matrices = matrices, rp = rp, rpNoShadow = noShadow });
                }
            }
        }

        // Camera planes, computed once per frame for all tiles.
        static readonly Plane[] planes = new Plane[6];
        static int planesFrame = -1;
        static Vector3 camPos;
        static bool haveCamera;

        /// <summary>Batches submitted / skipped in the last frame (all tiles) – for profiling overlays.</summary>
        public static int Submitted { get; private set; }
        public static int Culled { get; private set; }
        static int countFrame = -1;

        static void UpdatePlanes()
        {
            if (planesFrame == Time.frameCount) return;
            planesFrame = Time.frameCount;
            var cam = Camera.main;
            haveCamera = cam != null;
            if (!haveCamera) return;
            camPos = cam.transform.position;
            GeometryUtility.CalculateFrustumPlanes(cam, planes);
            // Widen the side planes so trees just outside the view still cast their shadows into it.
            for (int i = 0; i < 4; i++) planes[i].distance += ShadowMargin;
        }

        void LateUpdate()
        {
            UpdatePlanes();
            if (countFrame != Time.frameCount) { countFrame = Time.frameCount; Submitted = 0; Culled = 0; }
            foreach (var b in batches)
            {
                var bounds = b.rp.worldBounds;
                float d = haveCamera ? Mathf.Sqrt(bounds.SqrDistance(camPos)) : 0f;
                if (haveCamera && (d > MaxDistance || !GeometryUtility.TestPlanesAABB(planes, bounds))) { Culled++; continue; }
                if (d > FarDistance)
                {
                    if (b.far == null) { Culled++; continue; } // trunks, lamps: invisible that far
                    Submitted++;
                    Graphics.RenderMeshInstanced(b.rpNoShadow, b.far, 0, b.matrices);
                    continue;
                }
                Submitted++;
                Graphics.RenderMeshInstanced(d > ShadowDistance ? b.rpNoShadow : b.rp, b.mesh, 0, b.matrices);
            }
        }

        static Bounds TransformBounds(Bounds b, Matrix4x4 m)
        {
            var c = m.MultiplyPoint3x4(b.center);
            var e = b.extents;
            var ax = m.MultiplyVector(new Vector3(e.x, 0, 0));
            var ay = m.MultiplyVector(new Vector3(0, e.y, 0));
            var az = m.MultiplyVector(new Vector3(0, 0, e.z));
            var ext = new Vector3(
                Mathf.Abs(ax.x) + Mathf.Abs(ay.x) + Mathf.Abs(az.x),
                Mathf.Abs(ax.y) + Mathf.Abs(ay.y) + Mathf.Abs(az.y),
                Mathf.Abs(ax.z) + Mathf.Abs(ay.z) + Mathf.Abs(az.z));
            return new Bounds(c, ext * 2f);
        }

        /// <summary>One capsule per trunk/lamp – all on this object (static colliders, no cooking needed).</summary>
        void AddColliders(List<WorldTile.Instance> list)
        {
            foreach (var i in list)
            {
                var c = InstancePrototypes.Collider(i.proto);
                if (c.x <= 0f) continue;
                float r = Mathf.Max(0.05f, c.x * i.scale.x), h = Mathf.Max(c.y * i.scale.y, r * 2f);
                var cap = gameObject.AddComponent<CapsuleCollider>();
                cap.radius = r;
                cap.height = h;
                cap.direction = 1;
                cap.center = i.position + Vector3.up * (h * 0.5f);
            }
        }

        /// <summary>Editor (not playing): instances as plain meshes, one per material.</summary>
        void Bake(List<WorldTile.Instance> list, IList<Material> materials, HideFlags hide)
        {
            var perMaterial = new Dictionary<int, (List<Vector3> v, List<Vector3> n, List<Color32> c, List<int> t)>();
            foreach (var inst in list)
            {
                var mesh = InstancePrototypes.Get(inst.proto);
                if (mesh == null) continue;
                int mi = InstancePrototypes.MaterialOf(inst.proto);
                if (!perMaterial.TryGetValue(mi, out var g))
                    perMaterial[mi] = g = (new List<Vector3>(), new List<Vector3>(), new List<Color32>(), new List<int>());
                var m = InstancePrototypes.Matrix(inst);
                var nm = m.inverse.transpose;
                var tint = InstancePrototypes.TintOf(inst);
                var pv = mesh.vertices; var pn = mesh.normals; var pc = mesh.colors32; var pt = mesh.triangles;
                int offset = g.v.Count;
                for (int k = 0; k < pv.Length; k++)
                {
                    g.v.Add(m.MultiplyPoint3x4(pv[k]));
                    g.n.Add(nm.MultiplyVector(pn[k]).normalized);
                    var c = pc[k];
                    g.c.Add(new Color32((byte)Mathf.Min(255f, c.r * tint.x), (byte)Mathf.Min(255f, c.g * tint.y), (byte)Mathf.Min(255f, c.b * tint.z), c.a));
                }
                foreach (var t in pt) g.t.Add(t + offset);
            }
            foreach (var kv in perMaterial)
            {
                var mesh = new Mesh { name = "InstancesBaked", hideFlags = HideFlags.DontSave, indexFormat = IndexFormat.UInt32 };
                mesh.SetVertices(kv.Value.v);
                mesh.SetNormals(kv.Value.n);
                mesh.SetColors(kv.Value.c);
                mesh.SetTriangles(kv.Value.t, 0);
                mesh.RecalculateBounds();
                baked.Add(mesh);
                var go = new GameObject("InstancesBaked") { hideFlags = hide, layer = 0 };
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = materials != null && kv.Key < materials.Count ? materials[kv.Key] : null;
                mr.shadowCastingMode = ShadowCastingMode.On;
            }
        }

        void OnDestroy()
        {
            foreach (var m in baked) if (m != null) DestroyImmediate(m);
            baked.Clear();
            batches.Clear();
        }
    }
}
