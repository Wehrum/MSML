// What crewmates rely on, checked on one machine: every typed belt can be found again by its ends (crew messages
// name belts that way: ids differ between machines), and Better Belts' booster-power message (host -> crew) reads back
// onto the same boosters with the same power. Prints PASS/FAIL.
//   .\tools\cs.ps1 tools\scripts\crew-sync-test.cs

using System.Reflection;
using MSML;

public static class CrewSyncTest
{
    public static IEnumerator Run(ScriptContext ctx)
    {
        int pass = 0, fail = 0;
        void Check(bool ok, string what) { if (ok) pass++; else fail++; ctx.Log($"{(ok ? "PASS" : "FAIL")} {what}"); }

        // Belts by their ends.
        var typed = Belts.Types.SelectMany(t => Belts.OfType(t)).ToList();
        var lost = typed.Where(b => !Belts.EndsOf(b, out var s, out var e) || Belts.FindByEnds(s, e) != b).ToList();
        Check(typed.Count > 0 && lost.Count == 0, $"{typed.Count - lost.Count} of {typed.Count} typed belts found by their ends" +
            (lost.Count > 0 ? $" (not: {string.Join(", ", lost.Take(10))})" : ""));

        // Better Belts' booster power, encoded as the host sends it and read back as a crewmate would.
        var pressure = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "MSML.BetterBelts")?.GetTypes().FirstOrDefault(t => t.Name == "Pressure");
        if (pressure == null) { ctx.Log("skip: Better Belts isn't loaded"); }
        else
        {
            const BindingFlags any = BindingFlags.NonPublic | BindingFlags.Static;
            var own = (Dictionary<int, float>)pressure.GetField("BoosterPower", any).GetValue(null);
            var host = (System.Collections.IDictionary)pressure.GetField("HostBoosterPower", any).GetValue(null);
            var data = (byte[])pressure.GetMethod("EncodePower", any).Invoke(null, null);
            host.Clear();
            var found = (int)pressure.GetMethod("ReadPower", any).Invoke(null, new object[] { data });
            var same = own.Count(b => host.Contains(b.Key) && Math.Abs((float)host[b.Key].GetType().GetField("Item1").GetValue(host[b.Key]) - b.Value) < 1e-4f);
            Check(own.Count > 0 && found == own.Count && same == own.Count, $"booster power message: {own.Count} boosters, {data.Length} bytes, {found} found by their ends, {same} with the same power");
            host.Clear();   // single player: the host's word isn't used, but don't leave it behind
        }

        ctx.Log($"{pass} passed, {fail} failed");
        yield break;
    }
}
