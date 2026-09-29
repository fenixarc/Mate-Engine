using System;
using System.Collections.Generic;
using UnityEngine;

// Native tempo of each built-in dance clip, used by BPM sync to scale the Dance state.
// Edited and baked with MateEngine/ME Dance Tempo; loaded at runtime from Resources.
[CreateAssetMenu(fileName = "DanceTempoTable", menuName = "MateEngine/Dance Tempo Table")]
public class DanceTempoTable : ScriptableObject
{
    public const string ResourcePath = "DanceTempoTable";

    [Serializable]
    public class Entry
    {
        public AnimationClip clip;
        [Tooltip("Native tempo of the clip itself, before any blend-tree TimeScale. 0 = unknown.")]
        public float bpm;
        [Tooltip("Time in the clip (seconds) of a step that lands on the beat. Only used when hasFirstBeat is set.")]
        public float firstBeat;
        public bool hasFirstBeat;
    }

    public List<Entry> clips = new List<Entry>();

    [Header("Baked (per DanceIndex threshold, clip BPM x blend-tree TimeScale)")]
    public float[] femaleIndexBpm = new float[0];
    public float[] maleIndexBpm = new float[0];

    [Header("Baked beat grid (per DanceIndex; 0 beats = no phase lock)")]
    [Tooltip("Whole beats in one loop of the clip.")]
    public float[] femaleIndexBeatsPerLoop = new float[0];
    public float[] maleIndexBeatsPerLoop = new float[0];
    [Tooltip("Normalized state time of a beat (clip first beat, blend-tree cycle offset folded in).")]
    public float[] femaleIndexFirstBeat = new float[0];
    public float[] maleIndexFirstBeat = new float[0];

    [Header("Baked loop durations (per DanceIndex, clip length / blend-tree TimeScale)")]
    public float[] femaleIndexDuration = new float[0];
    public float[] maleIndexDuration = new float[0];

    static DanceTempoTable cached;
    static bool loaded;

    public static DanceTempoTable Instance
    {
        get
        {
            if (!loaded)
            {
                cached = Resources.Load<DanceTempoTable>(ResourcePath);
                loaded = true;
            }
            return cached;
        }
    }

    // Effective BPM at a (possibly fractional) DanceIndex; 0 if unknown.
    public float GetIndexBpm(float danceIndex, bool male)
    {
        var arr = male ? maleIndexBpm : femaleIndexBpm;
        if (arr == null || arr.Length == 0) return 0f;

        int lo = Mathf.Clamp(Mathf.FloorToInt(danceIndex), 0, arr.Length - 1);
        int hi = Mathf.Min(lo + 1, arr.Length - 1);
        float a = arr[lo], b = arr[hi];
        float t = danceIndex - lo;

        if (a <= 0f) return t >= 0.5f ? b : 0f;
        if (b <= 0f || t <= 0f) return t < 0.5f ? a : b;
        return Mathf.Lerp(a, b, t);
    }

    // Loop duration (seconds at speed 1) of the clip at an integer DanceIndex. False if not baked.
    public bool TryGetIndexDuration(int danceIndex, bool male, out float duration)
    {
        var arr = male ? maleIndexDuration : femaleIndexDuration;
        duration = arr != null && danceIndex >= 0 && danceIndex < arr.Length ? arr[danceIndex] : 0f;
        return duration > 0f;
    }

    // Beat grid of the clip at an integer DanceIndex: beat k sits at normalized state time
    // firstBeatNorm + k / beatsPerLoop. False if the clip has no usable grid.
    public bool TryGetIndexBeatGrid(int danceIndex, bool male, out float beatsPerLoop, out float firstBeatNorm)
    {
        var beats = male ? maleIndexBeatsPerLoop : femaleIndexBeatsPerLoop;
        var first = male ? maleIndexFirstBeat : femaleIndexFirstBeat;
        beatsPerLoop = firstBeatNorm = 0f;
        if (beats == null || first == null || danceIndex < 0 || danceIndex >= beats.Length || danceIndex >= first.Length) return false;
        beatsPerLoop = beats[danceIndex];
        firstBeatNorm = first[danceIndex];
        return beatsPerLoop > 0f;
    }
}
