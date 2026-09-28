using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// Tags each built-in dance clip with its native BPM and bakes per-DanceIndex tempos for BPM sync.
public class DanceTempoWindow : EditorWindow
{
    const string DefaultControllerPath = "Assets/MATE ENGINE - Animations/AvatarAnimatorControllerV2 1.controller";
    const string TableAssetPath = "Assets/Resources/DanceTempoTable.asset";
    const string DanceStateName = "Dance";
    const string DanceSpeedParam = "DanceSpeed";
    const float SampleRate = 60f;
    const float MinBpm = 60f, MaxBpm = 180f;

    class Row
    {
        public AnimationClip clip;
        public string usage;
        public float bpm;
        public float estimate;
        public float confidence;
        public string note;
    }

    AnimatorController controller;
    DanceTempoTable table;
    readonly List<Row> rows = new List<Row>();
    Vector2 scroll;

    int tapRow = -1;
    readonly List<double> taps = new List<double>();

    [MenuItem("MateEngine/ME Dance Tempo")]
    static void Open() => GetWindow<DanceTempoWindow>("ME Dance Tempo");

    void OnEnable()
    {
        if (controller == null) controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(DefaultControllerPath);
        table = AssetDatabase.LoadAssetAtPath<DanceTempoTable>(TableAssetPath);
        Rebuild();
    }

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "BPM is the clip's own tempo (before blend-tree TimeScale). Check each estimate: select Preview, play the clip in the Inspector, " +
            "and press Tap on the beat (4+ taps). Bake writes the table and sets up the DanceSpeed parameter on the Dance state.",
            MessageType.Info);

        EditorGUI.BeginChangeCheck();
        controller = (AnimatorController)EditorGUILayout.ObjectField("Controller", controller, typeof(AnimatorController), false);
        if (EditorGUI.EndChangeCheck()) Rebuild();

        EditorGUILayout.ObjectField("Table", table, typeof(DanceTempoTable), false);

        if (controller == null) return;
        if (rows.Count == 0)
        {
            EditorGUILayout.HelpBox($"No '{DanceStateName}' state with Female/Male 1D blend trees found.", MessageType.Warning);
            return;
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Re-estimate all")) { foreach (var r in rows) Estimate(r); }
        if (GUILayout.Button("Use estimates where empty"))
        {
            foreach (var r in rows) if (r.bpm <= 0f && r.estimate > 0f) r.bpm = Round1(r.estimate);
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();
        scroll = EditorGUILayout.BeginScrollView(scroll);
        for (int i = 0; i < rows.Count; i++) DrawRow(i);
        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space();
        if (GUILayout.Button("Bake", GUILayout.Height(28))) Bake();
    }

    void DrawRow(int i)
    {
        var r = rows[i];
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField($"{r.clip.name}  ({r.clip.length:0.00}s)", EditorStyles.boldLabel);
        if (GUILayout.Button("Preview", GUILayout.Width(70))) Selection.activeObject = r.clip;
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField(r.usage, EditorStyles.miniLabel);

        EditorGUILayout.BeginHorizontal();
        r.bpm = EditorGUILayout.FloatField("BPM", r.bpm);
        string est = r.estimate > 0f ? $"est {r.estimate:0.0} ({r.confidence:0.00})" : "no estimate";
        GUILayout.Label(est, GUILayout.Width(130));
        GUI.enabled = r.estimate > 0f;
        if (GUILayout.Button("Use", GUILayout.Width(40))) r.bpm = Round1(r.estimate);
        GUI.enabled = true;
        if (GUILayout.Button(tapRow == i && taps.Count > 0 ? $"Tap ({taps.Count})" : "Tap", GUILayout.Width(70))) Tap(i);
        EditorGUILayout.EndHorizontal();

        if (r.bpm > 0f)
        {
            float beats = r.clip.length * r.bpm / 60f;
            EditorGUILayout.LabelField($"Loop = {beats:0.00} beats", EditorStyles.miniLabel);
        }
        if (!string.IsNullOrEmpty(r.note)) EditorGUILayout.LabelField(r.note, EditorStyles.miniLabel);

        EditorGUILayout.EndVertical();
    }

    void Tap(int i)
    {
        double now = EditorApplication.timeSinceStartup;
        if (tapRow != i || (taps.Count > 0 && now - taps[taps.Count - 1] > 2.0)) taps.Clear();
        tapRow = i;
        taps.Add(now);
        if (taps.Count > 9) taps.RemoveAt(0);
        if (taps.Count >= 4)
        {
            double avg = (taps[taps.Count - 1] - taps[0]) / (taps.Count - 1);
            if (avg > 0.0) rows[i].bpm = Round1((float)(60.0 / avg));
        }
    }

    // ---------- Controller discovery ----------

    void Rebuild()
    {
        rows.Clear();
        tapRow = -1;
        taps.Clear();
        if (controller == null) return;

        var byClip = new Dictionary<AnimationClip, Row>();
        foreach (var (gender, tree) in FindGenderTrees(controller))
        {
            foreach (var child in tree.children)
            {
                if (!(child.motion is AnimationClip clip)) continue;
                if (!byClip.TryGetValue(clip, out var row))
                {
                    row = new Row { clip = clip, bpm = LookupBpm(clip) };
                    Estimate(row);
                    byClip[clip] = row;
                    rows.Add(row);
                }
                row.usage += (row.usage == null ? "" : ", ") + $"{gender} #{Mathf.RoundToInt(child.threshold)} x{child.timeScale:0.##}";
            }
        }
    }

    static AnimatorState FindState(AnimatorStateMachine sm, string name)
    {
        foreach (var s in sm.states) if (s.state.name == name) return s.state;
        foreach (var sub in sm.stateMachines)
        {
            var found = FindState(sub.stateMachine, name);
            if (found != null) return found;
        }
        return null;
    }

    static AnimatorState FindDanceState(AnimatorController c)
    {
        if (c.layers.Length == 0) return null;
        return FindState(c.layers[0].stateMachine, DanceStateName);
    }

    static List<(string gender, BlendTree tree)> FindGenderTrees(AnimatorController c)
    {
        var result = new List<(string, BlendTree)>();
        var state = FindDanceState(c);
        if (state == null || !(state.motion is BlendTree root)) return result;

        if (root.blendType == BlendTreeType.Simple1D)
        {
            result.Add(("Female", root));
            return result;
        }
        foreach (var child in root.children)
        {
            if (!(child.motion is BlendTree bt) || bt.blendType != BlendTreeType.Simple1D) continue;
            bool male = child.directBlendParameter == "isMale" || bt.name.ToLowerInvariant().Contains("male") && !bt.name.ToLowerInvariant().Contains("female");
            result.Add((male ? "Male" : "Female", bt));
        }
        return result;
    }

    float LookupBpm(AnimationClip clip)
    {
        if (table == null) return 0f;
        foreach (var e in table.clips) if (e.clip == clip) return e.bpm;
        return 0f;
    }

    // ---------- Tempo estimate from hip bounce ----------

    static void Estimate(Row r)
    {
        r.estimate = 0f;
        r.confidence = 0f;
        r.note = null;

        var binding = EditorCurveBinding.FloatCurve("", typeof(Animator), "RootT.y");
        var curve = AnimationUtility.GetEditorCurve(r.clip, binding);
        if (curve == null || curve.length < 2) { r.note = "No RootT.y curve; tap the tempo instead."; return; }

        int n = Mathf.FloorToInt(r.clip.length * SampleRate);
        if (n < 16) { r.note = "Clip too short to estimate."; return; }

        var raw = new float[n];
        for (int i = 0; i < n; i++) raw[i] = curve.Evaluate(i / SampleRate);

        // High-pass: subtract a circular moving average one slowest-beat wide, so slow drift
        // (crouching, stepping around) doesn't swamp the per-beat bounce.
        int half = Mathf.Min(n / 2, Mathf.RoundToInt(SampleRate * 60f / MinBpm / 2f));
        var x = new float[n];
        float energy = 0f;
        for (int i = 0; i < n; i++)
        {
            float avg = 0f;
            for (int k = -half; k <= half; k++) avg += raw[((i + k) % n + n) % n];
            x[i] = raw[i] - avg / (2 * half + 1);
            energy += x[i] * x[i];
        }
        if (energy < 1e-8f) { r.note = "Hips don't move; tap the tempo instead."; return; }

        // Circular autocorrelation: the clip loops, so wrap-around is valid.
        int minLag = Mathf.Max(1, Mathf.FloorToInt(SampleRate * 60f / MaxBpm));
        int maxLag = Mathf.Min(n - 1, Mathf.CeilToInt(SampleRate * 60f / MinBpm));
        if (maxLag <= minLag) { r.note = "Clip too short to estimate."; return; }

        var ac = new float[maxLag + 2];
        for (int lag = minLag - 1; lag <= maxLag + 1; lag++)
        {
            if (lag < 1 || lag >= n) continue;
            float sum = 0f;
            for (int i = 0; i < n; i++) sum += x[i] * x[(i + lag) % n];
            ac[lag] = sum / energy;
        }

        // Best local maximum in range (a peak at the range edge isn't a real period).
        int best = -1;
        for (int lag = Mathf.Max(minLag, 2); lag <= maxLag && lag + 1 < n; lag++)
        {
            if (ac[lag] < ac[lag - 1] || ac[lag] < ac[lag + 1]) continue;
            if (best < 0 || ac[lag] > ac[best]) best = lag;
        }
        if (best < 0 || ac[best] <= 0f) { r.note = "No clear bounce period; tap the tempo instead."; return; }

        // Parabolic peak refinement, clamped to half a sample.
        float period = best;
        {
            float a = ac[best - 1], b = ac[best], c = ac[best + 1];
            float denom = a - 2f * b + c;
            if (denom < -1e-6f) period = best + Mathf.Clamp(0.5f * (a - c) / denom, -0.5f, 0.5f);
        }

        float bpm = 60f * SampleRate / period;

        // Short loops should hold a whole number of beats; snap when close.
        float beats = r.clip.length * bpm / 60f;
        int whole = Mathf.RoundToInt(beats);
        if (whole >= 1 && Mathf.Abs(beats - whole) < 0.15f) bpm = whole * 60f / r.clip.length;
        else if (beats < 16f) r.note = $"Loop isn't a whole number of beats at {bpm:0.0} BPM; double-check by tapping.";

        r.estimate = bpm;
        r.confidence = Mathf.Clamp01(ac[best]);
    }

    // ---------- Bake ----------

    void Bake()
    {
        if (table == null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TableAssetPath));
            table = CreateInstance<DanceTempoTable>();
            AssetDatabase.CreateAsset(table, TableAssetPath);
        }

        Undo.RecordObject(table, "Bake Dance Tempo");
        table.clips.Clear();
        foreach (var r in rows) table.clips.Add(new DanceTempoTable.Entry { clip = r.clip, bpm = r.bpm });

        table.femaleIndexBpm = new float[0];
        table.maleIndexBpm = new float[0];
        foreach (var (gender, tree) in FindGenderTrees(controller))
        {
            int count = 0;
            foreach (var child in tree.children) count = Mathf.Max(count, Mathf.RoundToInt(child.threshold) + 1);
            var arr = new float[count];
            foreach (var child in tree.children)
            {
                int idx = Mathf.RoundToInt(child.threshold);
                if (idx < 0 || !(child.motion is AnimationClip clip)) continue;
                float bpm = LookupRowBpm(clip);
                arr[idx] = bpm > 0f ? bpm * child.timeScale : 0f;
            }
            if (gender == "Male") table.maleIndexBpm = arr; else table.femaleIndexBpm = arr;
        }
        EditorUtility.SetDirty(table);

        SetupController();
        AssetDatabase.SaveAssets();
        Debug.Log($"[DanceTempo] Baked {rows.Count} clips into {TableAssetPath}.");
    }

    float LookupRowBpm(AnimationClip clip)
    {
        foreach (var r in rows) if (r.clip == clip) return r.bpm;
        return 0f;
    }

    void SetupController()
    {
        bool hasParam = false;
        foreach (var p in controller.parameters) if (p.name == DanceSpeedParam) { hasParam = true; break; }
        if (!hasParam)
        {
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = DanceSpeedParam,
                type = AnimatorControllerParameterType.Float,
                defaultFloat = 1f
            });
        }

        var state = FindDanceState(controller);
        if (state != null && (!state.speedParameterActive || state.speedParameter != DanceSpeedParam))
        {
            Undo.RecordObject(state, "Dance speed parameter");
            state.speedParameterActive = true;
            state.speedParameter = DanceSpeedParam;
            EditorUtility.SetDirty(state);
        }
        EditorUtility.SetDirty(controller);
    }

    static float Round1(float v) => Mathf.Round(v * 10f) / 10f;

    // Batch entry point: -executeMethod DanceTempoWindow.BakeEstimatesBatch
    // Fills clips that have no BPM yet with a confident auto-estimate, logs every row, and bakes.
    // Low-confidence clips stay at 0 (no speed scaling) until someone taps them in.
    const float BatchMinConfidence = 0.5f;

    public static void BakeEstimatesBatch()
    {
        var w = CreateInstance<DanceTempoWindow>();
        try
        {
            w.OnEnable();
            foreach (var r in w.rows)
            {
                if (r.bpm <= 0f && r.estimate > 0f && r.confidence >= BatchMinConfidence) r.bpm = Round1(r.estimate);
                Debug.Log($"[DanceTempo] {r.clip.name}\tlen={r.clip.length:0.000}\tbpm={r.bpm:0.0}\test={r.estimate:0.0}\tconf={r.confidence:0.00}\t{r.usage}\t{r.note}");
            }
            if (w.controller != null && w.rows.Count > 0) w.Bake();
        }
        finally { DestroyImmediate(w); }
    }
}
