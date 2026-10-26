using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MoonCleaners;
using UnityEngine;
using UnityEngine.UI;

namespace MoonSweepMods.DevHarness;

/// <summary>
/// Development-only remote control. Polls BepInEx\devharness.in for commands (one per line) and appends results to
/// BepInEx\devharness.out. Expressions start at "menu" (the MoonMainMenu), "/Path/To/GameObject", or
/// "static:Full.Type.Name", followed by .Member or [index] steps.
///   shot &lt;file.png&gt;              screenshot
///   dump &lt;expr&gt; [depth]           transform hierarchy with components, rects and text
///   get &lt;expr&gt;                    print a value
///   call &lt;expr&gt;.Method [args]     invoke a method (args: numbers, true/false, enum names, "strings")
///   click &lt;expr&gt;                  invoke the first Button.onClick under an object
///   quit
/// </summary>
[BepInPlugin("moonsweepmods.devharness", "Dev Harness", "0.1.0")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;

    public override void Load()
    {
        Logger = Log;
        AddComponent<Harness>();
        new Harmony("moonsweepmods.devharness").PatchAll(typeof(MenuHook));
        new Harmony("moonsweepmods.devharness.devtools").PatchAll(typeof(DevToolsPatch));
        new Harmony("moonsweepmods.devharness.drive").PatchAll(typeof(DrivePatch));
    }
}

[HarmonyPatch(typeof(MoonPrototypeVehicle), nameof(MoonPrototypeVehicle.DriverControls))]
internal static class DrivePatch
{
    internal static float Throttle, Steer, Until;

    [HarmonyPostfix]
    private static void Postfix(MoonPrototypeVehicle __instance)
    {
        if (Time.unscaledTime > Until) return;
        __instance.throttle = Throttle;
        __instance.steer = Steer;
        __instance.brake = false;
    }
}

[HarmonyPatch(typeof(MoonDevTools), nameof(MoonDevTools.Enabled), MethodType.Getter)]
internal static class DevToolsPatch
{
    internal static bool On;

    [HarmonyPrefix]
    private static bool Prefix(ref bool __result)
    {
        if (!On) return true;
        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(MoonMainMenu), "OnEnable")]
internal static class MenuHook
{
    internal static MoonMainMenu? Menu;

    [HarmonyPostfix]
    private static void Postfix(MoonMainMenu __instance)
    {
        Menu = __instance;
        Plugin.Logger.LogInfo("DevHarness: main menu ready");
    }
}

public class Harness : MonoBehaviour
{
    private float _next;
    private string _inFile = "";
    private string _outFile = "";
    private static Harness? Instance;

    public Harness(IntPtr ptr) : base(ptr) { }

    // ScreenCapture.CaptureScreenshot(string) is broken in this game's interop; capture a texture instead.
    private static System.Collections.IEnumerator CaptureAtEndOfFrame(string path)
    {
        yield return new WaitForEndOfFrame();
        var tex = ScreenCapture.CaptureScreenshotAsTexture();
        try { File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex)); }
        catch (Exception e) { Plugin.Logger.LogError("Screenshot failed: " + e); }
        finally { Destroy(tex); }
    }

    private void Start()
    {
        Instance = this;
        _inFile = Path.Combine(Paths.BepInExRootPath, "devharness.in");
        _outFile = Path.Combine(Paths.BepInExRootPath, "devharness.out");
        Plugin.Logger.LogInfo($"Dev harness listening on {_inFile}");
    }

    private void Update()
    {
        if (Time.realtimeSinceStartup < _next) return;
        _next = Time.realtimeSinceStartup + 0.25f;
        if (!File.Exists(_inFile)) return;

        string[] lines;
        try { lines = File.ReadAllLines(_inFile); File.Delete(_inFile); }
        catch (IOException) { return; }

        var output = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            output.AppendLine("> " + line);
            try { output.AppendLine(Run(line)); }
            catch (Exception e) { output.AppendLine("ERROR: " + (e.InnerException ?? e)); }
        }
        output.AppendLine("<<done>>");
        File.AppendAllText(_outFile, output.ToString());
    }

    private static string Run(string line)
    {
        var space = line.IndexOf(' ');
        var cmd = space < 0 ? line : line[..space];
        var rest = space < 0 ? "" : line[(space + 1)..].Trim();
        switch (cmd)
        {
            case "shot":
                Instance!.StartCoroutine(CaptureAtEndOfFrame(rest).WrapToIl2Cpp());
                return "ok";
            case "get":
                return Describe(Resolve(rest));
            case "dump":
            {
                var cut = rest.LastIndexOf(' ');
                var depth = 3;
                var expr = rest;
                if (cut > 0 && int.TryParse(rest[(cut + 1)..], out var d)) { depth = d; expr = rest[..cut]; }
                var sb = new StringBuilder();
                Dump(TransformOf(Resolve(expr)), sb, 0, depth);
                return sb.ToString();
            }
            case "call":
                return Describe(Call(rest));
            case "set":
            {
                // set <expr>.Member <value>
                var sp = rest.LastIndexOf(' ');
                var target = rest[..sp];
                var value = rest[(sp + 1)..].Trim('"');
                var cut = Math.Max(target.LastIndexOf('.'), target.LastIndexOf(':'));
                var member = target[(cut + 1)..];
                var ownerExpr = target[..cut];
                object? owner = null;
                Type type;
                if (ownerExpr.StartsWith("static:") && ownerExpr.Count(c => c == ':') == 1) type = FindType(ownerExpr["static:".Length..]);
                else { owner = Resolve(ownerExpr); type = owner!.GetType(); }
                var prop = type.GetProperty(member, All);
                if (prop != null) prop.SetValue(owner, Convert(value, prop.PropertyType));
                else { var f = type.GetField(member, All) ?? throw new InvalidOperationException("No member " + member); f.SetValue(owner, Convert(value, f.FieldType)); }
                return "ok";
            }
            case "click":
            {
                var button = TransformOf(Resolve(rest)).GetComponentInChildren<Button>(true);
                if (button == null) return "no Button found";
                button.onClick.Invoke();
                return "clicked " + button.name;
            }
            case "drive":
            {
                // drive <throttle> <steer> <seconds> [clean]: overrides the driven vehicle's input (keyboard injection doesn't reach the game)
                var a = rest.Split(' ');
                DrivePatch.Throttle = float.Parse(a[0], CultureInfo.InvariantCulture);
                DrivePatch.Steer = float.Parse(a[1], CultureInfo.InvariantCulture);
                DrivePatch.Until = Time.unscaledTime + float.Parse(a[2], CultureInfo.InvariantCulture);
                return "driving";
            }
            case "devtools":
                DevToolsPatch.On = rest != "off";
                return "MoonDevTools.Enabled forced " + (DevToolsPatch.On ? "on" : "off") + "; reads now " + MoonCleaners.MoonDevTools.Enabled;
            case "quit":
                Application.Quit();
                return "quitting";
            default:
                return "unknown command";
        }
    }

    // ---- expression evaluation ---------------------------------------------------------------

    private static object? Resolve(string expr)
    {
        var steps = Tokenize(expr, out var root);
        object? current = root switch
        {
            "menu" => MenuHook.Menu ?? throw new InvalidOperationException("Main menu not seen yet."),
            _ when root.StartsWith("/") => GameObject.Find(root) ?? throw new InvalidOperationException("GameObject not found: " + root),
            _ when root.StartsWith("static:") => null,
            _ when root.StartsWith("find:") => FindLive(root["find:".Length..]),
            _ => throw new InvalidOperationException("Expressions start with menu, /Path, find:Type or static:Type"),
        };
        Type? staticType = root.StartsWith("static:") ? FindType(root["static:".Length..]) : null;
        foreach (var step in steps)
        {
            if (step.StartsWith("["))
            {
                var index = int.Parse(step[1..^1]);
                var item = current!.GetType().GetProperty("Item", new[] { typeof(int) }) ?? throw new InvalidOperationException("Not indexable");
                current = item.GetValue(current, new object[] { index });
            }
            else
            {
                current = GetMember(current, staticType, step);
                staticType = null;
            }
        }
        return current;
    }

    private static List<string> Tokenize(string expr, out string root)
    {
        // root: "menu", "/A/B/C", or "static:Namespace.Type" (type ends at the first '.' that starts a member we
        // can't find, so write static types as static:Full.Type.Name:Member).
        var steps = new List<string>();
        string rest;
        if (expr.StartsWith("static:"))
        {
            var colon = expr.IndexOf(':', "static:".Length);
            root = colon < 0 ? expr : expr[..colon];
            rest = colon < 0 ? "" : "." + expr[(colon + 1)..];
        }
        else if (expr.StartsWith("/"))
        {
            var dot = expr.IndexOf('.');
            root = dot < 0 ? expr : expr[..dot];
            rest = dot < 0 ? "" : expr[dot..];
        }
        else
        {
            var cut = expr.IndexOfAny(new[] { '.', '[' });
            root = cut < 0 ? expr : expr[..cut];
            rest = cut < 0 ? "" : expr[cut..];
        }
        var i = 0;
        while (i < rest.Length)
        {
            if (rest[i] == '.') { var j = rest.IndexOfAny(new[] { '.', '[' }, i + 1); if (j < 0) j = rest.Length; steps.Add(rest[(i + 1)..j]); i = j; }
            else if (rest[i] == '[') { var j = rest.IndexOf(']', i); steps.Add(rest[i..(j + 1)]); i = j + 1; }
            else i++;
        }
        return steps;
    }

    private static Type FindType(string name) =>
        AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null)
        ?? throw new InvalidOperationException("Type not found: " + name);

    /// <summary>find:TypeName or find:TypeName#2 - a live object of a game type (short names search MoonCleaners).</summary>
    private static object FindLive(string spec)
    {
        var hash = spec.IndexOf('#');
        var index = hash < 0 ? 0 : int.Parse(spec[(hash + 1)..]);
        var name = hash < 0 ? spec : spec[..hash];
        var type = name.Contains('.') ? FindType(name) : FindType("MoonCleaners." + name);
        var found = UnityEngine.Object.FindObjectsByType(Il2CppType.From(type), FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (index >= found.Length) throw new InvalidOperationException($"{found.Length} live {name} objects");
        var obj = found[index];
        return Activator.CreateInstance(type, obj.Pointer)!;
    }

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

    private static object? GetMember(object? target, Type? staticType, string name)
    {
        var type = staticType ?? target!.GetType();
        var prop = type.GetProperty(name, All);
        if (prop != null) return prop.GetValue(target);
        var field = type.GetField(name, All);
        if (field != null) return field.GetValue(target);
        // GameObject/Component child lookup by name.
        var t = TransformOf(target);
        var child = t?.Find(name);
        if (child != null) return child;
        throw new InvalidOperationException($"No member or child '{name}' on {type.Name}");
    }

    private static object? Call(string expr)
    {
        var space = expr.IndexOf(' ');
        var target = space < 0 ? expr : expr[..space];
        var args = space < 0 ? new List<string>() : SplitArgs(expr[(space + 1)..]);
        object? owner;
        Type type;
        string methodName;
        if (target.StartsWith("static:") && target.Count(c => c == ':') == 2)
        {
            // static:Full.Type.Name:Method
            var colon = target.LastIndexOf(':');
            type = FindType(target["static:".Length..colon]);
            methodName = target[(colon + 1)..];
            owner = null;
        }
        else
        {
            var dot = target.LastIndexOf('.');
            methodName = target[(dot + 1)..];
            owner = Resolve(target[..dot]);
            type = owner!.GetType();
        }
        var method = type.GetMethods(All).FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == args.Count)
            ?? throw new InvalidOperationException($"No method {methodName}/{args.Count} on {type.Name}");
        var ps = method.GetParameters();
        var values = new object?[args.Count];
        for (var i = 0; i < args.Count; i++) values[i] = Convert(args[i], ps[i].ParameterType);
        return method.Invoke(owner, values);
    }

    private static List<string> SplitArgs(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var c in s)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ' ' && !quoted) { if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); } continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    private static object? Convert(string value, Type type)
    {
        if (value == "null") return null;
        if (type.IsEnum) return Enum.Parse(type, value);
        if (type == typeof(string)) return value;
        if (type == typeof(bool)) return bool.Parse(value);
        if (type == typeof(int)) return int.Parse(value, CultureInfo.InvariantCulture);
        if (type == typeof(float)) return float.Parse(value, CultureInfo.InvariantCulture);
        if (type == typeof(double)) return double.Parse(value, CultureInfo.InvariantCulture);
        if (type == typeof(decimal)) return decimal.Parse(value, CultureInfo.InvariantCulture);
        if (type == typeof(long)) return long.Parse(value, CultureInfo.InvariantCulture);
        if (type == typeof(Vector3))
        {
            var p = value.Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            return new Vector3(p[0], p[1], p[2]);
        }
        if (type.FullName == "Il2CppSystem.Decimal")
        {
            var d = double.Parse(value, CultureInfo.InvariantCulture);
            var ctor = type.GetConstructor(new[] { typeof(double) });
            if (ctor != null) return ctor.Invoke(new object[] { d });
            var op = type.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "op_Implicit" && m.GetParameters()[0].ParameterType == typeof(decimal));
            if (op != null) return op.Invoke(null, new object[] { (decimal)d });
        }
        throw new InvalidOperationException($"Can't convert '{value}' to {type.Name}");
    }

    // ---- output -------------------------------------------------------------------------------

    private static Transform? TransformOf(object? o) => o switch
    {
        Transform t => t,
        GameObject g => g.transform,
        Component c => c.transform,
        _ => null,
    };

    private static string Describe(object? o)
    {
        if (o == null) return "null";
        if (o is UnityEngine.Object uo && uo == null) return "destroyed";
        var type = o.GetType();
        var count0 = type.GetProperty("Count") ?? type.GetProperty("Length");
        if (count0 != null && type.GetProperty("Item", new[] { typeof(int) }) is { } item0 && !(o is string))
        {
            var n = (int)count0.GetValue(o)!;
            var items = Enumerable.Range(0, Math.Min(n, 60)).Select(i => { var v = item0.GetValue(o, new object[] { i }); return v is Il2CppSystem.Object && !(v is UnityEngine.Object) ? Describe(v) : Short(v); });
            return $"{type.Name}[{n}] {{\n  {string.Join("\n  ", items)} }}";
        }
        if (o is Il2CppSystem.Object io && !(o is UnityEngine.Object))
        {
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.GetIndexParameters().Length == 0)
                .Select(p => { try { return $"{p.Name}={Short(p.GetValue(o))}"; } catch { return p.Name + "=?"; } });
            return $"{type.Name} {{ {string.Join(", ", props)} }}";
        }
        var count = type.GetProperty("Count") ?? type.GetProperty("Length");
        if (count != null && type.GetProperty("Item", new[] { typeof(int) }) is { } item)
        {
            var n = (int)count.GetValue(o)!;
            var items = Enumerable.Range(0, Math.Min(n, 50)).Select(i => Short(item.GetValue(o, new object[] { i })));
            return $"{type.Name}[{n}] {{ {string.Join(", ", items)} }}";
        }
        return Short(o);
    }

    private static string Short(object? o) => o switch
    {
        null => "null",
        UnityEngine.Object uo when uo == null => "destroyed",
        Component c => $"{c.GetIl2CppType().Name}({c.name})",
        GameObject g => $"GameObject({g.name})",
        UnityEngine.Object uo => $"{uo.GetIl2CppType().Name}({uo.name})",
        float f => f.ToString("0.###", CultureInfo.InvariantCulture),
        _ => o.ToString() ?? "?",
    };

    private static string Hex(Color c) =>
        $"#{(int)(c.r * 255):X2}{(int)(c.g * 255):X2}{(int)(c.b * 255):X2}{(int)(c.a * 255):X2}";

    private static void Dump(Transform? t, StringBuilder sb, int indent, int depth)
    {
        if (t == null) { sb.AppendLine("not a transform"); return; }
        var pad = new string(' ', indent * 2);
        var comps = string.Join(",", t.GetComponents<Component>().Select(c => c.GetIl2CppType().Name).Where(n => n != "Transform" && n != "RectTransform"));
        var rect = t.TryCast<RectTransform>();
        var geo = rect == null ? string.Format(CultureInfo.InvariantCulture, " lp=({0:0.##},{1:0.##},{2:0.##}) ls=({3:0.##},{4:0.##},{5:0.##})",
            t.localPosition.x, t.localPosition.y, t.localPosition.z, t.localScale.x, t.localScale.y, t.localScale.z) : string.Format(CultureInfo.InvariantCulture, " pos=({0:0.#},{1:0.#}) size=({2:0.#},{3:0.#}) anchor=({4:0.##},{5:0.##})-({6:0.##},{7:0.##}) pivot=({8:0.##},{9:0.##})",
            rect.anchoredPosition.x, rect.anchoredPosition.y, rect.sizeDelta.x, rect.sizeDelta.y,
            rect.anchorMin.x, rect.anchorMin.y, rect.anchorMax.x, rect.anchorMax.y, rect.pivot.x, rect.pivot.y);
        var mf = t.GetComponent<MeshFilter>();
        var mr = t.GetComponent<MeshRenderer>();
        if (mf != null && mf.sharedMesh != null)
        {
            var b = mf.sharedMesh.bounds;
            geo += string.Format(CultureInfo.InvariantCulture, " mesh={0} c=({1:0.##},{2:0.##},{3:0.##}) sz=({4:0.##},{5:0.##},{6:0.##})", mf.sharedMesh.name, b.center.x, b.center.y, b.center.z, b.size.x, b.size.y, b.size.z);
        }
        if (mr != null && mr.sharedMaterials != null) geo += " mats=" + string.Join("|", mr.sharedMaterials.Select(m => m == null ? "null" : m.name));
        var box = t.GetComponent<BoxCollider>();
        if (box != null) geo += string.Format(CultureInfo.InvariantCulture, " box c=({0:0.##},{1:0.##},{2:0.##}) sz=({3:0.##},{4:0.##},{5:0.##})", box.center.x, box.center.y, box.center.z, box.size.x, box.size.y, box.size.z);
        var text = t.GetComponent<TMPro.TMP_Text>();
        var label = text != null ? $" text=\"{text.text?.Replace("\n", "\\n")}\" size={text.fontSize:0.#} font={text.font?.name} color={Hex(text.color)} align={text.alignment}" : "";
        var image = t.GetComponent<Image>();
        if (image != null) label += $" img={image.sprite?.name ?? "none"} color={Hex(image.color)} type={image.type}";
        var raw = t.GetComponent<RawImage>();
        if (raw != null) label += $" raw={raw.texture?.name ?? "none"} color={Hex(raw.color)}";
        sb.AppendLine($"{pad}{t.name}{(t.gameObject.activeSelf ? "" : " [inactive]")} [{comps}]{geo}{label}");
        if (indent >= depth) { if (t.childCount > 0) sb.AppendLine($"{pad}  ... {t.childCount} children"); return; }
        for (var i = 0; i < t.childCount; i++) Dump(t.GetChild(i), sb, indent + 1, depth);
    }
}
