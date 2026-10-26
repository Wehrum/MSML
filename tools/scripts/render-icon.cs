// Renders a vehicle's in-game model to a transparent PNG icon, like the garage's 512x512 vehicle pictures.
//   .\tools\cs.ps1 tools\scripts\render-icon.cs "Hex harvester" C:\path\garage.png [yaw] [pitch]

public static class RenderIcon
{
    private const int Layer = 31;
    private const int Size = 512;

    public static object Run(ScriptContext ctx)
    {
        var name = ctx.Args.Length > 0 ? ctx.Args[0].Replace('_', ' ') : "Hex harvester";
        var output = ctx.Args.Length > 1 ? ctx.Args[1] : System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "devshots", "icon.png");
        var yaw = ctx.Args.Length > 2 ? float.Parse(ctx.Args[2], System.Globalization.CultureInfo.InvariantCulture) : -35f;
        var pitch = ctx.Args.Length > 3 ? float.Parse(ctx.Args[3], System.Globalization.CultureInfo.InvariantCulture) : 25f;
        var index = VehicleIndex(name);

        // Far above the player, on a layer nothing else uses, so only the model is in the picture.
        var root = new GameObject("Icon model");
        root.transform.position = Position + Up * 400f;
        root.transform.rotation = Quaternion.identity;
        var model = Equipment.Models.Vehicle(index, root.transform);
        model.SetActive(true);
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = Layer;
        foreach (var body in root.GetComponentsInChildren<Rigidbody>(true)) body.isKinematic = true;

        var bounds = new Bounds();
        var first = true;
        foreach (var r in root.GetComponentsInChildren<Renderer>(false))
        {
            if (!r.enabled) continue;
            if (first) { bounds = r.bounds; first = false; } else bounds.Encapsulate(r.bounds);
        }

        var camObject = new GameObject("Icon camera");
        var cam = camObject.AddComponent<Camera>();
        cam.enabled = false;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0, 0, 0, 0);
        cam.cullingMask = 1 << Layer;
        cam.fieldOfView = 22f;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 1000f;
        var direction = Quaternion.Euler(-pitch, yaw, 0) * Vector3.forward;   // vehicles face +Z: look from front-left, above
        // Fit: distance where every bounds corner fits the view, plus a small margin.
        var lookRotation = Quaternion.LookRotation(-direction, Vector3.up);
        var right = lookRotation * Vector3.right;
        var upAxis = lookRotation * Vector3.up;
        var forwardAxis = lookRotation * Vector3.forward;
        var tan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        var distance = 0f;
        for (var i = 0; i < 8; i++)
        {
            var corner = bounds.center + Vector3.Scale(bounds.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            var offset = corner - bounds.center;
            var depth = Vector3.Dot(offset, forwardAxis);
            distance = Mathf.Max(distance, Mathf.Abs(Vector3.Dot(offset, right)) / tan - depth, Mathf.Abs(Vector3.Dot(offset, upAxis)) / tan - depth);
        }
        cam.transform.position = bounds.center + direction * distance * 1.06f;
        cam.transform.rotation = lookRotation;

        var lightObject = new GameObject("Icon light");
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.6f;
        light.cullingMask = 1 << Layer;
        lightObject.transform.rotation = Quaternion.Euler(40f, yaw + 30f, 0);
        var fill = new GameObject("Icon fill").AddComponent<Light>();
        fill.type = LightType.Directional;
        fill.intensity = 0.6f;
        fill.cullingMask = 1 << Layer;
        fill.transform.rotation = Quaternion.Euler(20f, yaw - 120f, 0);

        var rt = new RenderTexture(Size * 2, Size * 2, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
        cam.targetTexture = rt;
        cam.Render();

        var previous = RenderTexture.active;
        RenderTexture.active = rt;
        var full = new Texture2D(Size * 2, Size * 2, TextureFormat.RGBA32, false);
        full.ReadPixels(new Rect(0, 0, Size * 2, Size * 2), 0, 0);
        full.Apply();
        RenderTexture.active = previous;

        // Crop to the model (opaque pixels) as a centred square with a small margin, then scale to the icon size.
        var pixels = full.GetPixels32();
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, w = full.width;
        for (var i = 0; i < pixels.Length; i++)
        {
            if (pixels[i].a < 8) continue;
            int x = i % w, y = i / w;
            if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        var side = Mathf.Min(full.width, (int)(Mathf.Max(maxX - minX, maxY - minY) * 1.08f));
        var cx = (minX + maxX) / 2; var cy = (minY + maxY) / 2;
        var crop = new Texture2D(side, side, TextureFormat.RGBA32, false);
        var clear = new Color32[side * side];
        crop.SetPixels32(clear);
        for (var y = 0; y < side; y++)
            for (var x = 0; x < side; x++)
            {
                int sx = cx - side / 2 + x, sy = cy - side / 2 + y;
                if (sx >= 0 && sy >= 0 && sx < full.width && sy < full.height) clear[y * side + x] = pixels[sy * w + sx];
            }
        crop.SetPixels32(clear);
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
        foreach (var o in new Object[] { rt, full, crop, tex, camObject, lightObject, fill.gameObject, root }) Object.Destroy(o);
        return $"{name}: bounds {bounds.size}, saved {output}";
    }
}
