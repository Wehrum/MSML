// Renders Better Belts' build-card pictures (512x512, transparent, like the game's cards) from real pieces: builds a
// short booster + tube in a clear spot near the player, renders the booster and the tube on their own, then removes them.
//   .\tools\cs.ps1 tools\scripts\tube-cards.cs <folder>     e.g. the repo's official\BetterBelts\package

using MSML;

public static class TubeCards
{
    private const int Layer = 31;
    private const int Size = 512;

    public static IEnumerator Run(ScriptContext ctx)
    {
        ctx.Timeout = 90;
        if (ctx.Args.Length == 0) { ctx.Log("Give the folder to save the cards in."); yield break; }
        var folder = ctx.Args[0];
        var w = World;
        var tube = Belts.TypeById("msml.betterbelts.tube");
        var booster = Belts.TypeById("msml.betterbelts.booster");
        if (tube == null || booster == null) { ctx.Log("Better Belts isn't loaded."); yield break; }
        Money = Math.Max(Money, 10000);
        var builder = Find<MoonDepotBuilder>();
        var up = Player.transform.up;

        // Out on open ground, away from the base's raised platforms (supports would reach down to the surface).
        var outward = Vector3.ProjectOnPlane(Player.transform.forward, up).normalized;
        Player.Teleport(Ground(Position + outward * 150f) + up * 0.3f, true);
        yield return 1.5f;
        up = Player.transform.up;

        // A clear 11 m run within reach.
        Vector3 start = default, dir = default; var found = false;
        for (var off = 4f; off < 30f && !found; off += 3f)
            for (var a = 0; a < 24 && !found; a++)
            {
                var d = Quaternion.AngleAxis(a * 15f, up) * Vector3.ProjectOnPlane(Player.transform.forward, up).normalized;
                var s = Ground(Position + d * off) + up * 0.7f;
                if (!Flat(s, d, 11f, up)) continue;
                string reason; MoonConveyorPath path; Il2CppSystem.Decimal price;
                if (w.CheckConveyor(Player, Plan(s, d, 11f, 0), out reason, out path, out price)) { start = s; dir = d; found = true; }
            }
        if (!found) { ctx.Log("No clear spot near the player."); yield break; }

        builder.SetOpen(true);
        yield return 0.3f;
        var ids = new List<int>();
        foreach (var (type, length) in new[] { (booster, 2.4f), (tube, 8f) })
        {
            builder.SelectStructure(type.Kind);
            builder.BeginPlacement();
            var at = ids.Count == 0 ? start : w.conveyors[ids[^1]].end;
            string reason; int id;
            if (!w.TryBuildConveyor(Player, Plan(at, dir, length, ids.Count == 0 ? 0 : ids[^1]), out reason, out id)) { ctx.Log("Build failed: " + reason); break; }
            ids.Add(id);
        }
        builder.SelectStructure(MoonDepotBuilder.ConveyorKind);
        builder.SetOpen(false);
        yield return 3f;   // views, power and pressure

        if (ids.Count == 2)
        {
            ctx.Log(Render(ids[0], dir, System.IO.Path.Combine(folder, "booster.png")));
            ctx.Log(Render(ids[1], dir, System.IO.Path.Combine(folder, "tube.png")));
        }
        foreach (var id in ids.AsEnumerable().Reverse()) { string reason; w.TryRemoveConveyor(Player, id, out reason); }
    }

    // The ground stays just under the run, so the pieces stand on short legs like the game's card pictures.
    static bool Flat(Vector3 start, Vector3 dir, float length, Vector3 up)
    {
        for (var at = 0f; at <= length; at += 1f)
        {
            var p = start + dir * at;
            var below = Vector3.Dot(p - Ground(p), up);
            if (below < 0.4f || below > 1.0f) return false;
        }
        return true;
    }

    static MoonWorld.ConveyorPlan Plan(Vector3 start, Vector3 dir, float length, int after)
    {
        var up = Player.transform.up;
        var plan = new MoonWorld.ConveyorPlan();
        plan.start = start; plan.end = start + dir * length;
        plan.startDirection = dir; plan.endDirection = dir; plan.startUp = up; plan.endUp = up;
        plan.startBelt = after;
        return plan;
    }

    static string Render(int belt, Vector3 dir, string output)
    {
        // The belt type's parts live in their own object ("MSML belt <id> (<type>)").
        var view = FindAll<Transform>().FirstOrDefault(t => t.parent == null && t.name.StartsWith($"MSML belt {belt} "))?.gameObject;
        if (view == null) return $"belt {belt} has no parts";
        // Show full pressure on the card (there may be no power where the pieces were built).
        foreach (var r in view.GetComponentsInChildren<Renderer>(true))
            if (r.name is "Ring" or "Status") BeltLooks.SetGlow(r, new Color(0.12f, 1f, 0.08f), 1.4f);
        var layers = new Dictionary<Transform, int>();
        foreach (var t in view.GetComponentsInChildren<Transform>(true)) { layers[t] = t.gameObject.layer; t.gameObject.layer = Layer; }
        var bounds = new Bounds(); var first = true;
        foreach (var r in view.GetComponentsInChildren<Renderer>(false))
        {
            if (!r.enabled) continue;
            if (first) { bounds = r.bounds; first = false; } else bounds.Encapsulate(r.bounds);
        }

        var up = Player.transform.up;
        var right = Vector3.Cross(up, dir).normalized;
        // Isometric-ish, like the game's cards: from the front-right, above.
        var direction = (-dir * 0.75f + right * 0.85f + up * 0.75f).normalized;
        var camObject = new GameObject("Card camera");
        var cam = camObject.AddComponent<Camera>();
        cam.enabled = false;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0, 0, 0, 0);
        cam.cullingMask = 1 << Layer;
        cam.fieldOfView = 20f;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 1000f;
        var look = Quaternion.LookRotation(-direction, up);
        var tan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        var distance = 0f;
        for (var i = 0; i < 8; i++)
        {
            var corner = bounds.center + Vector3.Scale(bounds.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            var offset = corner - bounds.center;
            var depth = Vector3.Dot(offset, look * Vector3.forward);
            distance = Mathf.Max(distance, Mathf.Abs(Vector3.Dot(offset, look * Vector3.right)) / tan - depth, Mathf.Abs(Vector3.Dot(offset, look * Vector3.up)) / tan - depth);
        }
        cam.transform.position = bounds.center + direction * distance * 1.05f;
        cam.transform.rotation = look;

        var key = new GameObject("Card light").AddComponent<Light>();
        key.type = LightType.Directional; key.intensity = 1.5f; key.cullingMask = 1 << Layer;
        key.transform.rotation = Quaternion.LookRotation(-(direction + up * 0.8f + dir * 0.3f).normalized, up);
        var fill = new GameObject("Card fill").AddComponent<Light>();
        fill.type = LightType.Directional; fill.intensity = 0.55f; fill.cullingMask = 1 << Layer;
        fill.transform.rotation = Quaternion.LookRotation((right + up * 0.3f).normalized, up);

        var rt = new RenderTexture(Size * 2, Size * 2, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
        cam.targetTexture = rt;
        cam.Render();
        foreach (var (t, layer) in layers) if (t != null) t.gameObject.layer = layer;

        var previous = RenderTexture.active;
        RenderTexture.active = rt;
        var full = new Texture2D(Size * 2, Size * 2, TextureFormat.RGBA32, false);
        full.ReadPixels(new Rect(0, 0, Size * 2, Size * 2), 0, 0);
        full.Apply();
        RenderTexture.active = previous;

        // Crop to the piece as a centred square with a margin, then scale to the card size.
        var pixels = full.GetPixels32();
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, w = full.width;
        for (var i = 0; i < pixels.Length; i++)
        {
            if (pixels[i].a < 8) continue;
            int x = i % w, y = i / w;
            if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        var side = Mathf.Min(full.width, (int)(Mathf.Max(maxX - minX, maxY - minY) * 1.12f));
        var cx = (minX + maxX) / 2; var cy = (minY + maxY) / 2;
        var square = new Color32[side * side];
        for (var y = 0; y < side; y++)
            for (var x = 0; x < side; x++)
            {
                int sx = cx - side / 2 + x, sy = cy - side / 2 + y;
                if (sx >= 0 && sy >= 0 && sx < full.width && sy < full.height) square[y * side + x] = pixels[sy * w + sx];
            }
        var crop = new Texture2D(side, side, TextureFormat.RGBA32, false);
        crop.SetPixels32(square);
        crop.Apply();
        var scaled = RenderTexture.GetTemporary(Size, Size, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(crop, scaled);
        RenderTexture.active = scaled;
        var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
        tex.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(scaled);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output));
        System.IO.File.WriteAllBytes(output, ImageConversion.EncodeToPNG(tex).ToArray());

        cam.targetTexture = null;
        foreach (var o in new Object[] { rt, full, crop, tex, camObject, key.gameObject, fill.gameObject }) Object.Destroy(o);
        return $"belt {belt}: saved {output}";
    }
}
