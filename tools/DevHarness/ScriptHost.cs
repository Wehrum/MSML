using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnityEngine;

namespace MoonSweepMods.DevHarness;

/// <summary>
/// Hot-loaded C# scripts: compiles a .cs file (or a snippet) with Roslyn inside the running game, loads it, and calls
/// <c>static Run(ScriptContext ctx)</c> on any class. No restart, so trying something takes seconds.
/// <para>
/// Run can return a value (printed), or be an iterator (<c>IEnumerator</c>/<c>IEnumerable</c>) that runs over several
/// frames: <c>yield return null</c> waits a frame, <c>yield return 1.5f</c> waits seconds, <c>yield return (Func&lt;bool&gt;)</c>
/// waits until true, and yielding another iterator runs it first. The command returns when it finishes.
/// </para>
/// <para>
/// Running a script again replaces the previous version: its Harmony patches (<c>ctx.Harmony</c>), per-frame hooks
/// (<c>ctx.OnUpdate</c>) and <c>ctx.OnUnload</c> cleanups are undone first. Warnings and errors logged by the game or any
/// mod while a script runs are added to its output.
/// </para>
/// </summary>
internal static class ScriptHost
{
    private static readonly Dictionary<string, ScriptContext> Loaded = new(StringComparer.OrdinalIgnoreCase);

    private static ScriptContext? _running;
    private static Stack<IEnumerator>? _coroutine;
    private static float _waitUntil;
    private static Func<bool>? _waitFor;
    private static float _deadline;
    private static string? _finished;

    public static bool Busy => _running != null;

    /// <summary>The output of a script that finished running over several frames (once).</summary>
    public static string? TakeFinished()
    {
        var f = _finished;
        _finished = null;
        return f;
    }

    // ---- entry points ------------------------------------------------------------------------------

    public static string RunFile(string path, string[] args)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Script not found", path);
        var name = Path.GetFileNameWithoutExtension(path);
        return Start(name, () => ScriptCompiler.CompileFile(name, path), args);
    }

    /// <summary>An expression (printed) or statements (end with <c>return x;</c> to print something).</summary>
    public static string Eval(string code)
    {
        code = code.Trim();
        var statements = code.Contains(';') || code.StartsWith("{");
        var body = statements ? code + (code.Contains("return") ? "" : "\nreturn null;") : $"return {code};";
        var source = "public static class EvalScript {\npublic static object Run(MoonSweepMods.DevHarness.ScriptContext ctx) {\n" + body + "\n}\n}";
        return Start("eval", () => ScriptCompiler.CompileSource("eval", source), Array.Empty<string>());
    }

    public static string Unload(string name)
    {
        if (!Loaded.TryGetValue(name, out var ctx)) return "not loaded: " + name;
        ctx.Dispose();
        Loaded.Remove(name);
        return "unloaded " + name;
    }

    public static string List() =>
        Loaded.Count == 0 ? "no scripts loaded" : string.Join("\n", Loaded.Values.Select(c => $"{c.Name}: {c.Describe()}"));

    /// <summary>Per-frame work: script hooks, and the running coroutine.</summary>
    public static void Tick()
    {
        foreach (var ctx in Loaded.Values.ToList()) ctx.RunHooks();
        if (_running != null) Step();
    }

    // ---- compile + run ------------------------------------------------------------------------------

    private static string Start(string name, Func<Assembly> compile, string[] args)
    {
        if (_running != null) throw new InvalidOperationException($"Script '{_running.Name}' is still running.");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var assembly = compile();
        var compileMs = sw.ElapsedMilliseconds;

        var entry = assembly.GetTypes()
            .Select(t => t.GetMethod("Run", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .FirstOrDefault(m => m != null)
            ?? throw new InvalidOperationException("No static Run(ScriptContext ctx) method found.");

        if (Loaded.TryGetValue(name, out var previous)) previous.Dispose();
        var ctx = new ScriptContext(name, args);
        Loaded[name] = ctx;
        ctx.Log($"[compiled in {compileMs} ms]");

        object? result;
        ctx.BeginCapture();
        try
        {
            result = entry.GetParameters().Length == 0 ? entry.Invoke(null, null) : entry.Invoke(null, new object[] { ctx });
        }
        catch (TargetInvocationException e)
        {
            ctx.Log("EXCEPTION: " + e.InnerException);
            ctx.EndCapture();
            return ctx.TakeOutput();
        }
        finally
        {
            if (!(_running == ctx)) ctx.EndCapture();
        }

        var iterator = result switch
        {
            IEnumerator e => e,
            IEnumerable e and not string => e.GetEnumerator(),
            _ => null,
        };
        if (iterator == null)
        {
            if (result != null) ctx.Log(Harness.Describe(result));
            if (!ctx.HasHooks && name == "eval") Loaded.Remove(name);
            return ctx.TakeOutput();
        }

        // Runs over several frames; the batch resumes when it finishes (see TakeFinished).
        _running = ctx;
        _coroutine = new Stack<IEnumerator>();
        _coroutine.Push(iterator);
        _waitUntil = 0;
        _waitFor = null;
        _deadline = Time.unscaledTime + ctx.Timeout;
        ctx.BeginCapture();
        Step();
        return "";
    }

    private static void Step()
    {
        var ctx = _running!;
        try
        {
            if (Time.unscaledTime > _deadline) throw new TimeoutException($"Script ran longer than {ctx.Timeout} s (set ctx.Timeout).");
            if (Time.unscaledTime < _waitUntil) return;
            if (_waitFor != null && !_waitFor()) return;
            _waitFor = null;

            while (_coroutine!.Count > 0)
            {
                var top = _coroutine.Peek();
                if (!top.MoveNext()) { _coroutine.Pop(); continue; }
                switch (top.Current)
                {
                    case null: return;
                    case float f: _waitUntil = Time.unscaledTime + f; return;
                    case double d: _waitUntil = Time.unscaledTime + (float)d; return;
                    case int i: _waitUntil = Time.unscaledTime + i; return;
                    case Func<bool> until: _waitFor = until; return;
                    case IEnumerator nested: _coroutine.Push(nested); continue;
                    case IEnumerable nested and not string: _coroutine.Push(nested.GetEnumerator()); continue;
                    default: ctx.Log(Harness.Describe(top.Current)); return;
                }
            }
        }
        catch (Exception e)
        {
            ctx.Log("EXCEPTION: " + e);
        }
        if (_coroutine!.Count > 0 && ctx.Alive) return;
        ctx.EndCapture();
        _finished = ctx.TakeOutput();
        _running = null;
        _coroutine = null;
    }
}

/// <summary>The Roslyn side, kept separate so the rest of the harness works even if Roslyn can't load.</summary>
internal static class ScriptCompiler
{
    private static List<MetadataReference>? _references;
    private static int _counter;

    public static Assembly CompileFile(string name, string path) =>
        Compile(name, new[] { CSharpSyntaxTree.ParseText(Source(File.ReadAllText(path)), Parse, path) });

    public static Assembly CompileSource(string name, string source) =>
        Compile(name, new[] { CSharpSyntaxTree.ParseText(Source(source), Parse, name + ".cs") });

    private static readonly CSharpParseOptions Parse = new(LanguageVersion.Latest);

    // Debug info (PDB) needs the source text's encoding.
    private static Microsoft.CodeAnalysis.Text.SourceText Source(string text) => Microsoft.CodeAnalysis.Text.SourceText.From(text, Encoding.UTF8);

    private const string GlobalUsings = @"
global using System;
global using System.Collections;
global using System.Collections.Generic;
global using System.Linq;
global using UnityEngine;
global using MoonCleaners;
global using HarmonyLib;
global using MoonSweepMods.DevHarness;
global using static MoonSweepMods.DevHarness.Dev;
global using Object = UnityEngine.Object;
global using Random = UnityEngine.Random;
";

    private static Assembly Compile(string name, SyntaxTree[] trees)
    {
        var all = trees.Append(CSharpSyntaxTree.ParseText(Source(GlobalUsings), Parse, "GlobalUsings.cs")).ToArray();
        var assemblyName = $"devscript_{name}_{++_counter}";
        var compilation = CSharpCompilation.Create(assemblyName, all, References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                optimizationLevel: OptimizationLevel.Debug,
                nullableContextOptions: NullableContextOptions.Disable));
        using var dll = new MemoryStream();
        using var pdb = new MemoryStream();
        var emitted = compilation.Emit(dll, pdb, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(debugInformationFormat: Microsoft.CodeAnalysis.Emit.DebugInformationFormat.PortablePdb));
        if (!emitted.Success)
        {
            var errors = emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(15)
                .Select(d =>
                {
                    var span = d.Location.GetLineSpan();
                    var line = span.Path == "GlobalUsings.cs" ? "" : $"{Path.GetFileName(span.Path)}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): ";
                    return line + d.Id + " " + d.GetMessage();
                });
            throw new InvalidOperationException("Compile failed:\n" + string.Join("\n", errors));
        }
        // Load from a file like any mod: Harmony's IL2CPP patching crashed the runtime with a script loaded from bytes.
        // Each compile gets a unique name, so versions never clash.
        var folder = Path.Combine(Paths.CachePath, "devscripts");
        Directory.CreateDirectory(folder);
        if (_counter == 1)
            foreach (var old in Directory.GetFiles(folder)) try { File.Delete(old); } catch (IOException) { }
        var path = Path.Combine(folder, assemblyName + ".dll");
        File.WriteAllBytes(path, dll.ToArray());
        File.WriteAllBytes(Path.ChangeExtension(path, ".pdb"), pdb.ToArray());
        return Assembly.LoadFrom(path);
    }

    /// <summary>Everything a script may use: loaded assemblies, the game's interop assemblies, BepInEx and .NET.</summary>
    private static List<MetadataReference> References()
    {
        if (_references != null) return _references;
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a.IsDynamic || string.IsNullOrEmpty(a.Location)) continue;
            byName.TryAdd(a.GetName().Name!, a.Location);
        }
        var folders = new[]
        {
            Path.Combine(Paths.BepInExRootPath, "interop"),
            Path.Combine(Paths.BepInExRootPath, "core"),
            System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
        };
        foreach (var folder in folders.Where(Directory.Exists))
            foreach (var file in Directory.GetFiles(folder, "*.dll"))
            {
                var simple = Path.GetFileNameWithoutExtension(file);
                if (!byName.ContainsKey(simple) && IsManaged(file)) byName[simple] = file;
            }
        _references = byName.Values.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        return _references;
    }

    private static bool IsManaged(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            return pe.HasMetadata;
        }
        catch { return false; }
    }
}

/// <summary>What a script gets: output, a Harmony instance, per-frame hooks and cleanups, all undone on reload.</summary>
public sealed class ScriptContext : ILogListener
{
    private readonly StringBuilder _output = new();
    private readonly List<Action> _hooks = new();
    private readonly List<Action> _unload = new();
    private Harmony? _harmony;
    private bool _capturing;

    internal ScriptContext(string name, string[] args)
    {
        Name = name;
        Args = args;
    }

    /// <summary>The script's file name (without .cs), or "eval".</summary>
    public string Name { get; }

    /// <summary>Extra words after the file name: <c>cs script.cs a b c</c>.</summary>
    public string[] Args { get; }

    /// <summary>Seconds an iterator script may run before it's stopped. Default 120.</summary>
    public float Timeout { get; set; } = 120f;

    /// <summary>Harmony patches made through this are removed when the script is reloaded or unloaded.</summary>
    public Harmony Harmony => _harmony ??= new Harmony("devscript." + Name);

    internal bool Alive { get; private set; } = true;
    internal bool HasHooks => _hooks.Count > 0 || _unload.Count > 0 || _harmony != null;

    /// <summary>Adds a line to the script's output.</summary>
    public void Log(object? value) => _output.AppendLine(value as string ?? Harness.Describe(value));

    /// <summary>Runs every frame until the script is reloaded or unloaded. Exceptions are logged and the hook removed.</summary>
    public void OnUpdate(Action hook) => _hooks.Add(hook);

    /// <summary>Runs when the script is reloaded or unloaded (restore things you changed).</summary>
    public void OnUnload(Action cleanup) => _unload.Add(cleanup);

    internal void RunHooks()
    {
        foreach (var hook in _hooks.ToList())
        {
            try { hook(); }
            catch (Exception e)
            {
                _hooks.Remove(hook);
                Plugin.Logger.LogError($"Script {Name}: OnUpdate hook failed and was removed: {e}");
            }
        }
    }

    internal string Describe() =>
        $"{_hooks.Count} hook(s), {(_harmony == null ? 0 : _harmony.GetPatchedMethods().Count())} patched method(s)";

    internal void Dispose()
    {
        Alive = false;
        foreach (var cleanup in _unload)
        {
            try { cleanup(); }
            catch (Exception e) { Plugin.Logger.LogError($"Script {Name}: OnUnload failed: {e}"); }
        }
        _unload.Clear();
        _hooks.Clear();
        try { _harmony?.UnpatchSelf(); }
        catch (Exception e) { Plugin.Logger.LogError($"Script {Name}: unpatching failed: {e}"); }
        _harmony = null;
        EndCapture();
    }

    internal string TakeOutput()
    {
        var s = _output.ToString().TrimEnd();
        _output.Clear();
        return s;
    }

    // ---- log capture: warnings and errors (from anything) while the script runs ---------------------

    internal void BeginCapture()
    {
        if (_capturing) return;
        _capturing = true;
        BepInEx.Logging.Logger.Listeners.Add(this);
    }

    internal void EndCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        BepInEx.Logging.Logger.Listeners.Remove(this);
    }

    public LogLevel LogLevelFilter => LogLevel.Warning | LogLevel.Error | LogLevel.Fatal;

    public void LogEvent(object sender, LogEventArgs e)
    {
        if ((e.Level & LogLevelFilter) == 0) return;
        _output.AppendLine($"[{e.Level}:{e.Source.SourceName}] {e.Data}");
    }

    void IDisposable.Dispose() { }
}
