using System.Collections.Generic;
using UnityEngine;

namespace MSML;

/// <summary>
/// The game's own sound clips, by name (e.g. "garage-flow-loop", "vehicle-boost", "build-dismantle",
/// "capsule-launch"), played in the world.
/// </summary>
public static class GameSounds
{
    private static readonly Dictionary<string, AudioClip> Clips = new();

    /// <summary>The game's clip with this name, or null.</summary>
    public static AudioClip? Clip(string name)
    {
        if (Clips.TryGetValue(name, out var clip) && clip != null && !clip.WasCollected) return clip;
        foreach (var c in Resources.FindObjectsOfTypeAll<AudioClip>())
            if (c != null && c.name == name) return Clips[name] = c;
        return null;
    }

    /// <summary>Plays a clip once at a point in the world (3D: fades with distance).</summary>
    public static void PlayAt(string name, Vector3 position, float volume = 1f, float maxDistance = 30f, float pitch = 1f)
    {
        var clip = Clip(name);
        if (clip == null) return;
        var go = new GameObject("MSML sound " + name);
        go.transform.position = position;
        var source = Setup(go.AddComponent<AudioSource>(), clip, volume, maxDistance);
        source.pitch = pitch;
        source.Play();
        Object.Destroy(go, clip.length / Mathf.Max(0.1f, pitch) + 0.1f);
    }

    /// <summary>A looping 3D sound attached to <paramref name="parent"/> at <paramref name="position"/>; change its volume to fade it.</summary>
    public static AudioSource? Loop(Transform parent, Vector3 position, string name, float volume = 1f, float maxDistance = 20f)
    {
        var clip = Clip(name);
        if (clip == null) return null;
        var go = new GameObject("MSML loop " + name);
        go.transform.SetParent(parent, false);
        go.transform.position = position;
        var source = Setup(go.AddComponent<AudioSource>(), clip, volume, maxDistance);
        source.loop = true;
        source.time = Random.Range(0f, clip.length);
        source.Play();
        return source;
    }

    private static AudioSource Setup(AudioSource source, AudioClip clip, float volume, float maxDistance)
    {
        source.clip = clip;
        source.volume = volume;
        source.spatialBlend = 1f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = 1.5f;
        source.maxDistance = maxDistance;
        source.dopplerLevel = 0f;
        source.playOnAwake = false;
        return source;
    }
}
