// Close-up renders of the Hex Harvester in the world (own camera and light, so it works at night), plus checks:
// wheel centres vs ground, whether the body is in the swaying assembly, and where the lamps are.
//   .\tools\cs.ps1 tools\scripts\harvester-shots.cs

public static class HarvesterShots
{
    public static IEnumerator Run(ScriptContext ctx)
    {
        var index = VehicleIndex("Hex harvester");
        var v = FindAll<MoonPrototypeVehicle>().FirstOrDefault(x => x.Index == index && x.StorageId.StartsWith("garage")) ?? FindAll<MoonPrototypeVehicle>().First(x => x.Index == index);
        yield return 0.5f;
        var t = v.transform;
        foreach (var w in v.GetComponentsInChildren<Transform>(true).Where(x => x.name.StartsWith("Wheel_") && x.name.Length == 8).Take(2))
        {
            var up = w.position.normalized;
            var hit = Physics.RaycastAll(w.position + up * 3f, -up, 10f, ~0, QueryTriggerInteraction.Ignore).Where(h => h.collider.attachedRigidbody == null).OrderBy(h => h.distance).FirstOrDefault();
            ctx.Log($"{w.name}: centre {hit.distance - 3f:0.00} m above ground (tyre radius 0.96)");
        }
        var visuals = v.GetComponentsInChildren<Transform>(true).First(x => x.name == "Hex Harvester visuals");
        ctx.Log($"visuals parent: {visuals.parent.name}");
        foreach (var l in v.GetComponentsInChildren<Transform>(true).Where(x => x.parent != null && x.parent.name == "Vehicle lights"))
            ctx.Log($"  {l.name}: metres {(t.InverseTransformPoint(l.position) * 1.45f)}");

        var folder = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "devshots");
        Render(v, new Vector3(-6f, 1.6f, 9f), new Vector3(0, 2.4f, 3f), System.IO.Path.Combine(folder, "hh-front.png"));
        Render(v, new Vector3(-7.5f, 0.9f, -1f), new Vector3(0, 1.0f, -0.5f), System.IO.Path.Combine(folder, "hh-side.png"));
        Render(v, new Vector3(5f, 3.2f, -10f), new Vector3(0, 2.4f, -2.5f), System.IO.Path.Combine(folder, "hh-rear.png"));
        Render(v, new Vector3(-4.5f, 5.2f, 3.0f), new Vector3(0, 3.0f, 3.2f), System.IO.Path.Combine(folder, "hh-cab.png"));
        Render(v, new Vector3(3.5f, 1.6f, 7.2f), new Vector3(0, 2.2f, 3.6f), System.IO.Path.Combine(folder, "hh-nose.png"));
        ctx.Log("rendered hh-front, hh-side, hh-rear, hh-cab, hh-nose");
    }

    /// <summary>Renders from a camera at <paramref name="eye"/> looking at <paramref name="at"/> (both in the vehicle's metres).</summary>
    private static void Render(MoonPrototypeVehicle v, Vector3 eye, Vector3 at, string path)
    {
        var t = v.transform;
        Vector3 W(Vector3 metres) => t.TransformPoint(metres / 1.45f);
        var up = t.position.normalized;
        var camObject = new GameObject("Shot camera");
        var cam = camObject.AddComponent<Camera>();
        cam.enabled = false;
        cam.fieldOfView = 45f;
        cam.nearClipPlane = 0.05f;
        cam.transform.position = W(eye);
        cam.transform.rotation = Quaternion.LookRotation(W(at) - W(eye), up);
        var lightObject = new GameObject("Shot light");
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.3f;
        lightObject.transform.rotation = Quaternion.LookRotation(-up * 0.7f + t.right * 0.5f + t.forward * 0.4f, up);
        var fill = new GameObject("Shot fill").AddComponent<Light>();
        fill.type = LightType.Directional;
        fill.intensity = 0.5f;
        fill.transform.rotation = Quaternion.LookRotation(-up * 0.3f - t.right * 0.6f - t.forward * 0.5f, up);

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
        foreach (var o in new Object[] { rt, tex, camObject, lightObject, fill.gameObject }) Object.Destroy(o);
    }
}
