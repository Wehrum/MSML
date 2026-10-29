// Renderer census: every active renderer grouped by what it is (its top-level object, digits stripped, and for MSML's
// belt looks the part name), with how many are visible, their triangles, and how many cast shadows. Shows what the
// frame's triangles and draw calls are made of.
//   .\tools\cs.ps1 tools\scripts\renderer-census.cs [top]

public static class RendererCensus
{
    sealed class Group { public int Count, Visible, Shadows, VisibleShadows; public long Tris, VisibleTris; public HashSet<IntPtr> Materials = new(); }

    public static object Run(ScriptContext ctx)
    {
        var top = ctx.Args.Select(a => int.TryParse(a, out var v) ? v : 0).FirstOrDefault(v => v > 0);
        if (top <= 0) top = 30;
        var meshTris = new Dictionary<IntPtr, long>();
        long TrisOf(Mesh? m)
        {
            if (m == null) return 0;
            if (meshTris.TryGetValue(m.Pointer, out var t)) return t;
            t = 0;
            for (var s = 0; s < m.subMeshCount; s++) t += (long)m.GetIndexCount(s) / 3;
            return meshTris[m.Pointer] = t;
        }
        string Key(Transform t)
        {
            var root = t.root;
            var name = new string(root.name.Where(c => !char.IsDigit(c)).ToArray()).Trim();
            // MSML belt/junction looks: by the part (Glass, Ring, Spoil...).
            if (name.StartsWith("MSML")) return name + " / " + new string(t.name.Where(c => !char.IsDigit(c)).ToArray()).Trim();
            // Inside the world: the object under the world root.
            if (t.parent != null && root != t)
            {
                var child = t;
                while (child.parent != null && child.parent != root) child = child.parent;
                return name + " / " + new string(child.name.Where(c => !char.IsDigit(c)).ToArray()).Trim();
            }
            return name;
        }
        var groups = new Dictionary<string, Group>();
        int total = 0;
        foreach (var r in FindAll<Renderer>())
        {
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
            var kind = r.GetIl2CppType().Name;
            if (kind != "MeshRenderer" && kind != "SkinnedMeshRenderer") continue;
            total++;
            Mesh? mesh = null;
            if (r.TryCast<SkinnedMeshRenderer>() is { } sk) mesh = sk.sharedMesh;
            else if (r.GetComponent<MeshFilter>() is { } mf) mesh = mf.sharedMesh;
            var tris = TrisOf(mesh);
            var key = Key(r.transform);
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = new Group();
            var visible = r.isVisible;
            var shadows = r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off;
            g.Count++; g.Tris += tris;
            if (shadows) g.Shadows++;
            if (visible) { g.Visible++; g.VisibleTris += tris; if (shadows) g.VisibleShadows++; }
            if (r.sharedMaterial != null) g.Materials.Add(r.sharedMaterial.Pointer);
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{total} active renderers, {groups.Values.Sum(g => g.Visible)} visible, {groups.Values.Sum(g => g.VisibleTris):N0} visible triangles (each drawn again per shadow cascade if it casts shadows)");
        sb.AppendLine($"{"what",-58} {"count",7} {"visible",7} {"vis tris",12} {"all tris",12} {"shadow",7} {"mats",5}");
        foreach (var (k, g) in groups.OrderByDescending(p => p.Value.VisibleTris).Take(top))
            sb.AppendLine($"{(k.Length > 58 ? k.Substring(0, 58) : k),-58} {g.Count,7} {g.Visible,7} {g.VisibleTris,12:N0} {g.Tris,12:N0} {g.VisibleShadows,7} {g.Materials.Count,5}");
        sb.AppendLine("\nby renderer count (visible):");
        foreach (var (k, g) in groups.OrderByDescending(p => p.Value.Visible).Take(12))
            sb.AppendLine($"{(k.Length > 58 ? k.Substring(0, 58) : k),-58} {g.Visible,7} visible, {g.VisibleShadows} cast shadows");
        return sb.ToString();
    }
}
