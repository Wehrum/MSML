// The Hex Harvester's glass silo at several fill levels (rendered with its own camera and light, so it works at
// night), and its upgrade prices.
//   .\tools\cs.ps1 tools\scripts\silo-test.cs

public static class SiloTest
{
    public static IEnumerator Run(ScriptContext ctx)
    {
        var index = VehicleIndex("Hex harvester");
        foreach (var d in MoonVehicleUpgrades.All)
            ctx.Log($"upgrade {d.kind}: level I ${MoonVehicleUpgrades.Price(d, index, 0)} (rover ${MoonVehicleUpgrades.Price(d, 21, 0)})");

        var v = FindAll<MoonPrototypeVehicle>().FirstOrDefault(x => x.Index == index) ?? Spawn("Hex harvester", 15f);
        var storage = World.vehicleStorage[v.StorageId];
        var original = storage.kilograms;
        var folder = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "devshots");

        foreach (var fill in new[] { 0.0, 0.35, 0.75, 1.0 })
        {
            storage.kilograms = v.DustCapacityKg * fill;
            yield return 2.5f;   // the column eases towards the new level
            var path = System.IO.Path.Combine(folder, $"silo-{(int)(fill * 100)}.png");
            Render(v, path);
            ctx.Log($"fill {fill:P0}: {v.DustKilograms:N0}/{v.DustCapacityKg:N0} kg -> {path}");
        }
        storage.kilograms = original;
    }

    /// <summary>Renders the vehicle from its left-rear quarter, lit by a temporary light.</summary>
    private static void Render(MoonPrototypeVehicle v, string path)
    {
        var t = v.transform;
        var up = t.position.normalized;
        var silo = t.position + up * 4.5f - t.forward * 2.5f;
        var camObject = new GameObject("Silo camera");
        var cam = camObject.AddComponent<Camera>();
        cam.enabled = false;
        cam.fieldOfView = 26f;
        cam.transform.position = silo - t.right * 11f - t.forward * 6f + up * 3f;
        cam.transform.rotation = Quaternion.LookRotation(silo - cam.transform.position, up);
        var lightObject = new GameObject("Silo light");
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.4f;
        lightObject.transform.rotation = Quaternion.LookRotation(-up * 0.6f + t.right * 0.6f + t.forward * 0.3f, up);

        var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        cam.targetTexture = rt;
        cam.Render();
        var previous = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(1280, 720, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
        tex.Apply();
        RenderTexture.active = previous;
        cam.targetTexture = null;
        System.IO.File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex).ToArray());
        foreach (var o in new Object[] { rt, tex, camObject, lightObject }) Object.Destroy(o);
    }
}
