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
    }

    public List<Entry> clips = new List<Entry>();

    [Header("Baked (per DanceIndex threshold, clip BPM x blend-tree TimeScale)")]
    public float[] femaleIndexBpm = new float[0];
    public float[] maleIndexBpm = new float[0];

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
}
