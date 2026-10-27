using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoonCleaners;
using Steamworks;

namespace MSML;

/// <summary>
/// Messages between mods on crewmates' machines. They travel on a Steam channel of their own that the game (and
/// players without MSML) never read. Messages are reliable and arrive in order per sender.
/// <para>
/// Handlers run on the main thread. A typical pattern: the host keeps the truth and sends it to everyone; crewmates
/// ask the host for changes with <see cref="SendToHost"/>.
/// </para>
/// </summary>
public static class CrewMessages
{
    /// <summary>Steam channel used for MSML messages (the game uses 0-2).</summary>
    public const int Channel = 77;

    private const string MemberKey = "msml";
    private static readonly Dictionary<string, List<Action<ulong, byte[]>>> Handlers = new();
    private static MoonCrewSession? _session;
    private static ulong _markedLobby;

    /// <summary>True while in a crew (hosting or joined).</summary>
    public static bool InCrew => MoonSteamLobby.InLobby && MoonSteamLobby.lobby != 0;

    /// <summary>True when not in a crew, or hosting one: this machine has the final say.</summary>
    public static bool IsHost => !InCrew || MoonSteamLobby.IsHost;

    /// <summary>This player's Steam id.</summary>
    public static ulong Me => SteamUser.GetSteamID().m_SteamID;

    /// <summary>The crew host's Steam id (this player when not in a crew).</summary>
    public static ulong Host => InCrew ? SteamMatchmaking.GetLobbyOwner(new CSteamID(MoonSteamLobby.lobby)).m_SteamID : Me;

    /// <summary>Crewmates (not including this player) who have MSML, so can receive messages.</summary>
    public static List<ulong> Crewmates()
    {
        var result = new List<ulong>();
        if (!InCrew) return result;
        var lobby = new CSteamID(MoonSteamLobby.lobby);
        var me = Me;
        var count = SteamMatchmaking.GetNumLobbyMembers(lobby);
        for (var i = 0; i < count; i++)
        {
            var member = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
            if (member.m_SteamID == me) continue;
            if (!string.IsNullOrEmpty(SteamMatchmaking.GetLobbyMemberData(lobby, member, MemberKey))) result.Add(member.m_SteamID);
        }
        return result;
    }

    /// <summary>Calls <paramref name="handler"/> (sender, data) for every message sent with this topic.</summary>
    public static void On(string topic, Action<ulong, byte[]> handler)
    {
        if (string.IsNullOrEmpty(topic) || Encoding.UTF8.GetByteCount(topic) > 255) throw new ArgumentException("Topics are 1-255 bytes.", nameof(topic));
        if (!Handlers.TryGetValue(topic, out var list)) Handlers[topic] = list = new List<Action<ulong, byte[]>>();
        list.Add(handler);
        Patches.Apply();
    }

    /// <summary>Sends a message to one crewmate. Returns false if it couldn't be sent.</summary>
    public static bool Send(string topic, byte[] data, ulong to)
    {
        if (!InCrew || to == 0 || to == Me) return false;
        var session = Session();
        if (session == null) return false;
        var bytes = Encode(topic, data);
        try { return session.SendBytes(to, new Il2CppStructArray<byte>(bytes), bytes.Length, Channel, true); }
        catch (Exception e) { Log.Error($"Sending '{topic}' to a crewmate failed: {e.Message}"); return false; }
    }

    /// <summary>Sends a message to every crewmate with MSML.</summary>
    public static void SendToCrew(string topic, byte[] data)
    {
        foreach (var member in Crewmates()) Send(topic, data, member);
    }

    /// <summary>Sends a message to the host (nothing when hosting or not in a crew).</summary>
    public static bool SendToHost(string topic, byte[] data) => !IsHost && Send(topic, data, Host);

    /// <summary>
    /// Host only: a crewmate with MSML has loaded the moon and is ready for the host's state (raised again if their
    /// first message may have been missed). Send what they need now rather than on the next regular update.
    /// </summary>
    public static event Action<ulong>? CrewmateReady;

    private const string ReadyTopic = "msml.ready";
    // Crewmate: when (after loading) to say it's ready, again in case the host wasn't listening yet.
    private static readonly float[] ReadyAt = { 0.5f, 3f, 8f };
    private static (ulong Lobby, IntPtr World) _readyFor;
    private static float _readySince;
    private static int _readySent;

    // Crewmate: tells the host it's ready a few times after loading a moon (each new crew or moon starts again).
    private static void SayReady()
    {
        if (IsHost || Moon.World is not { } world) return;
        var key = (MoonSteamLobby.lobby, world.Pointer);
        var now = UnityEngine.Time.unscaledTime;
        if (_readyFor != key) { _readyFor = key; _readySince = now; _readySent = 0; }
        if (_readySent >= ReadyAt.Length || now - _readySince < ReadyAt[_readySent]) return;
        _readySent++;
        SendToHost(ReadyTopic, Array.Empty<byte>());
    }

    private static void OnReady(ulong sender, byte[] data)
    {
        if (!IsHost) return;
        foreach (var handler in CrewmateReady?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action<ulong>)handler)(sender); }
            catch (Exception e) { Log.Error("A CrewmateReady handler failed: " + e); }
        }
    }

    private static MoonCrewSession? Session()
    {
        if (_session == null || _session.WasCollected) _session = Moon.Find<MoonCrewSession>();
        return _session;
    }

    private static byte[] Encode(string topic, byte[] data)
    {
        var name = Encoding.UTF8.GetBytes(topic);
        var bytes = new byte[1 + name.Length + data.Length];
        bytes[0] = (byte)name.Length;
        name.CopyTo(bytes, 1);
        data.CopyTo(bytes, 1 + name.Length);
        return bytes;
    }

    // ---- receiving ---------------------------------------------------------------------------

    private static readonly Il2CppStructArray<IntPtr> Incoming = new(32);

    internal static void Poll()
    {
        if (!InCrew) { _markedLobby = 0; return; }
        MarkMember();
        SayReady();
        if (Handlers.Count == 0) return;
        int count;
        try { count = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, Incoming, Incoming.Length); }
        catch (Exception e) { Log.Error("Receiving crew messages failed: " + e.Message); return; }
        for (var i = 0; i < count; i++)
        {
            var ptr = Incoming[i];
            try
            {
                var message = SteamNetworkingMessage_t.FromIntPtr(ptr);
                var sender = message.m_identityPeer.GetSteamID64();
                var bytes = new byte[message.m_cbSize];
                System.Runtime.InteropServices.Marshal.Copy(message.m_pData, bytes, 0, bytes.Length);
                Dispatch(sender, bytes);
            }
            catch (Exception e) { Log.Error("A crew message couldn't be read: " + e.Message); }
            finally { SteamNetworkingMessage_t.Release(ptr); }
        }
    }

    private static void Dispatch(ulong sender, byte[] bytes)
    {
        if (bytes.Length < 1 || bytes.Length < 1 + bytes[0]) return;
        var topic = Encoding.UTF8.GetString(bytes, 1, bytes[0]);
        if (!Handlers.TryGetValue(topic, out var list)) return;
        var data = new byte[bytes.Length - 1 - bytes[0]];
        Array.Copy(bytes, 1 + bytes[0], data, 0, data.Length);
        foreach (var handler in list)
        {
            try { handler(sender, data); }
            catch (Exception e) { Log.Error($"A handler for crew message '{topic}' failed: {e}"); }
        }
    }

    // Tells crewmates this player has MSML (lobby member data), once per crew.
    private static void MarkMember()
    {
        if (_markedLobby == MoonSteamLobby.lobby) return;
        _markedLobby = MoonSteamLobby.lobby;
        try { SteamMatchmaking.SetLobbyMemberData(new CSteamID(MoonSteamLobby.lobby), MemberKey, Msml.ApiVersion); }
        catch (Exception e) { Log.Error("Marking this player as having MSML failed: " + e.Message); }
    }

    internal static class Patches
    {
        private static bool _applied;

        internal static void Apply()
        {
            if (_applied) return;
            _applied = true;
            new Harmony("msml.api.crewmessages").PatchAll(typeof(Patches));
            On(ReadyTopic, OnReady);
        }

        [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.Update))]
        [HarmonyPrefix]
        private static void BeforeWorldUpdate()
        {
            try { Poll(); }
            catch (Exception e) { Log.Error("Crew messages failed: " + e); }
        }
    }
}
