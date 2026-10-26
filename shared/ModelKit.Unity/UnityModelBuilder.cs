using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace MoonSweepMods.ModelKit;

/// <summary>Turns a ModelKit <see cref="Model"/> into Unity GameObjects with meshes. Compiled into mods only.</summary>
public static class UnityModelBuilder
{
    /// <summary>
    /// Builds every group of <paramref name="model"/> under <paramref name="parent"/>. <paramref name="parentFor"/> can
    /// redirect a group to another transform (for example a game-animated wheel); return null to use the default.
    /// Returns the created group transforms by name.
    /// </summary>
    public static Dictionary<string, Transform> Build(Model model, Transform parent, Func<string, Material?> material,
        Func<ModelGroup, Transform?>? parentFor = null, int layer = -1)
    {
        var made = new Dictionary<string, Transform>();
        foreach (var group in model.Groups)
        {
            var go = new GameObject(group.Name);
            var custom = parentFor?.Invoke(group);
            go.transform.SetParent(custom ?? parent, false);
            if (custom == null) go.transform.localPosition = ToUnity(group.Pivot);
            go.layer = layer >= 0 ? layer : parent.gameObject.layer;

            foreach (var (matName, data) in group.ByMaterial)
            {
                if (data.Triangles.Count == 0) continue;
                var part = new GameObject(matName);
                part.transform.SetParent(go.transform, false);
                part.layer = go.layer;
                var mesh = new Mesh { name = $"{model.Name} {group.Name} {matName}" };
                if (data.Vertices.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                var verts = new Il2CppStructArray<Vector3>(data.Vertices.Count);
                var norms = new Il2CppStructArray<Vector3>(data.Normals.Count);
                for (var i = 0; i < data.Vertices.Count; i++) { verts[i] = ToUnity(data.Vertices[i]); norms[i] = ToUnity(data.Normals[i]); }
                var tris = new Il2CppStructArray<int>(data.Triangles.Count);
                for (var i = 0; i < data.Triangles.Count; i++) tris[i] = data.Triangles[i];
                mesh.vertices = verts;
                mesh.normals = norms;
                mesh.triangles = tris;
                mesh.RecalculateBounds();
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = part.AddComponent<MeshRenderer>();
                var mat = material(matName);
                if (mat != null) renderer.sharedMaterial = mat;
            }
            made[group.Name] = go.transform;
        }
        return made;
    }

    public static Vector3 ToUnity(V3 v) => new(v.X, v.Y, v.Z);

    public static Color ToColor(Rgba c) => new(c.R, c.G, c.B, c.A);
}
