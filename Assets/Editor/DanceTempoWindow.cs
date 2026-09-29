using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// Tags each built-in dance clip with its native BPM and beat position, and bakes per-DanceIndex tempos and
// beat grids for BPM sync.
public class DanceTempoWindow : EditorWindow
{
    const string DefaultControllerPath = "Assets/MATE ENGINE - Animations/AvatarAnimatorControllerV2 1.controller";
    const string TableAssetPath = "Assets/Resources/DanceTempoTable.asset";
    const string DanceStateName = "Dance";
    const string DanceSpeedParam = "DanceSpeed";
    const float SampleRate = 60f;
    const float MinBpm = 60f, MaxBpm = 180f;
    const float SnapBeats = 0.15f;   // snap a loop to whole beats when it is this close

    class Row
    {
        public AnimationClip clip;
        public string usage;
        public float bpm;
        public float estimate;
        public float confidence;
        public string note;

        public float firstBeat;
        public bool hasFirstBeat;
        public float offsetEstimate = -1f;   // seconds; < 0 = none
        public float offsetStrength;
        public float offsetEstimateBpm;      // the BPM the offset estimate was made for
    }

    AnimatorController controller;
    DanceTempoTable table;
    readonly List<Row> rows = new List<Row>();
    Vector2 scroll;

    int tapRow = -1;
    readonly List<double> taps = new List<double>();

    // Play-Mode beat marking: circular mean of the live dance's beat phase at each tap.
    int markRow = -1, markCount;
    double markCos, markSin, lastMarkTime;
    string markStatus;

    // (gender, DanceIndex) -> clip child, for Play-Mode marking.
    readonly Dictionary<(bool male, int index), ChildMotion> indexChildren = new Dictionary<(bool, int), ChildMotion>();

    [MenuItem("MateEngine/ME Dance Tempo")]
    static void Open() => GetWindow<DanceTempoWindow>("ME Dance Tempo");

    void OnEnable()
    {
        if (controller == null) controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(DefaultControllerPath);
        table = AssetDatabase.LoadAssetAtPath<DanceTempoTable>(TableAssetPath);
        Rebuild();
    }

    void OnInspectorUpdate()
    {
        if (EditorApplication.isPlaying) Repaint();
    }

    void OnGUI()
    {
        if (EditorApplication.isPlaying) DrawLiveStatus();

        EditorGUILayout.HelpBox(
            "BPM is the clip's own tempo (before blend-tree TimeScale). Check each estimate: select Preview, play the clip in the Inspector, " +
            "and press Tap on the beat (4+ taps).\n" +
            "Beat is the clip time of a step that lands on the beat (est = lowest hip point). Best: in Play Mode, while the avatar dances " +
            "this clip, press Mark on each step it takes (4+ taps).\n" +
            "Bake writes the table (live in Play Mode) and sets up the DanceSpeed parameter on the Dance state.",
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
            foreach (var r in rows)
            {
                if (r.bpm <= 0f && r.estimate > 0f) r.bpm = Round1(r.estimate);
                UpdateOffsetEstimate(r);
                if (!r.hasFirstBeat && r.offsetEstimate >= 0f) { r.firstBeat = Round3(r.offsetEstimate); r.hasFirstBeat = true; }
            }
        }
        EditorGUILayout.EndHorizontal();

        if (EditorApplication.isPlaying)
            EditorGUILayout.LabelField("Now dancing: " + DescribeLive(), EditorStyles.miniLabel);
        if (!string.IsNullOrEmpty(markStatus)) EditorGUILayout.LabelField(markStatus, EditorStyles.miniLabel);

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
        UpdateOffsetEstimate(r);
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
            int whole = Mathf.RoundToInt(beats);
            string grid = Mathf.Abs(beats - whole) <= 1e-3f ? "" :
                Mathf.Abs(beats - whole) <= SnapBeats && whole >= 1 ? $"  (Bake snaps to {whole} = {whole * 60f / r.clip.length:0.00} BPM)" :
                "  (not whole beats: the beat lock re-aligns at each loop)";
            EditorGUILayout.LabelField($"Loop = {beats:0.00} beats{grid}", EditorStyles.miniLabel);

            float period = 60f / r.bpm;
            EditorGUILayout.BeginHorizontal();
            r.hasFirstBeat = EditorGUILayout.ToggleLeft("Beat at", r.hasFirstBeat, GUILayout.Width(62));
            GUI.enabled = r.hasFirstBeat;
            r.firstBeat = EditorGUILayout.FloatField(r.firstBeat, GUILayout.Width(55));
            GUILayout.Label("s", GUILayout.Width(12));
            if (GUILayout.Button("1/2", GUILayout.Width(32))) r.firstBeat = Wrap(r.firstBeat + period * 0.5f, period);
            if (GUILayout.Button("-1/8", GUILayout.Width(36))) r.firstBeat = Wrap(r.firstBeat - period * 0.125f, period);
            if (GUILayout.Button("+1/8", GUILayout.Width(36))) r.firstBeat = Wrap(r.firstBeat + period * 0.125f, period);
            GUI.enabled = true;
            string oest = r.offsetEstimate >= 0f ? $"est {r.offsetEstimate:0.000} ({r.offsetStrength:0.00})" : "no est";
            GUILayout.Label(oest, GUILayout.Width(105));
            GUI.enabled = r.offsetEstimate >= 0f;
            if (GUILayout.Button("Use", GUILayout.Width(40))) { r.firstBeat = Round3(r.offsetEstimate); r.hasFirstBeat = true; }
            GUI.enabled = EditorApplication.isPlaying;
            if (GUILayout.Button(markRow == i && markCount > 0 ? $"Mark ({markCount})" : "Mark", GUILayout.Width(70))) Mark(i);
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
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

    // ---------- Play-Mode live status ----------

    // The avatar's BPM sync debug fields, so the avatar doesn't have to be selected (its Inspector repainting every
    // frame costs a lot of FPS and makes frame times uneven).
    void DrawLiveStatus()
    {
        var a = FindLiveAvatar();
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField("Live BPM sync", EditorStyles.boldLabel);
        if (a == null)
        {
            EditorGUILayout.LabelField("No active avatar.");
        }
        else
        {
            string music = a.detectedMusicBpm > 0f ? $"{a.detectedMusicBpm:0.0} BPM" : "-";
            EditorGUILayout.LabelField("Music", $"{music}   tempo conf {a.bpmConfidence:0.00}   capture {a.bpmCaptureMode}");
            EditorGUILayout.LabelField("Beat lock", $"{a.beatLockState}   phase conf {a.beatPhaseConfidence:0.00}   error {a.beatPhaseErrorMs:+0;-0;0} ms");
            string dance = a.isDancing ? $"{a.observedDanceBpm:0.0} beats/min   speed x{DanceSpeed(a.animator):0.00}" : "not dancing";
            EditorGUILayout.LabelField("Dance", dance);
        }
        EditorGUILayout.EndVertical();
    }

    AvatarAnimatorController cachedAvatar;

    AvatarAnimatorController FindLiveAvatar()
    {
        if (cachedAvatar != null && cachedAvatar.isActiveAndEnabled) return cachedAvatar;
        cachedAvatar = null;
        foreach (var a in FindObjectsByType<AvatarAnimatorController>(FindObjectsSortMode.None))
        {
            if (!a.isActiveAndEnabled) continue;
            if (cachedAvatar == null || a.isDancing) cachedAvatar = a;
        }
        return cachedAvatar;
    }

    static float DanceSpeed(Animator anim)
    {
        if (anim == null) return 1f;
        foreach (var p in anim.parameters)
            if (p.name == DanceSpeedParam && p.type == AnimatorControllerParameterType.Float) return anim.GetFloat(DanceSpeedParam);
        return 1f;
    }

    // ---------- Play-Mode beat marking ----------

    // The dancing avatar's current clip and its normalized clip time, if it is settled on one built-in dance.
    bool TryGetLiveClip(out AnimationClip clip, out float clipNorm, out string why)
    {
        clip = null;
        clipNorm = 0f;
        why = "not in Play Mode";
        if (!EditorApplication.isPlaying) return false;

        why = "no avatar is dancing";
        int danceHash = Animator.StringToHash(DanceStateName);
        foreach (var a in FindObjectsByType<AvatarAnimatorController>(FindObjectsSortMode.None))
        {
            var anim = a.animator;
            if (!a.isActiveAndEnabled || !a.isDancing || anim == null || !anim.isActiveAndEnabled) continue;
            var st = anim.GetCurrentAnimatorStateInfo(0);
            if (st.shortNameHash != danceHash || anim.IsInTransition(0)) { why = "not settled in the Dance state"; continue; }
            float index = anim.GetFloat("DanceIndex");
            int whole = Mathf.RoundToInt(index);
            if (Mathf.Abs(index - whole) > 1e-4f) { why = "switching dances"; continue; }
            if (!indexChildren.TryGetValue((a.enableHusbandoMode, whole), out var child) || !(child.motion is AnimationClip c))
            {
                why = $"DanceIndex {whole} has no clip in this controller";
                continue;
            }
            clip = c;
            float stateNorm = st.normalizedTime - Mathf.Floor(st.normalizedTime);
            clipNorm = Wrap(stateNorm + child.cycleOffset, 1f);
            return true;
        }
        return false;
    }

    string DescribeLive() => TryGetLiveClip(out var clip, out float norm, out string why) ? $"{clip.name} @ {norm * clip.length:0.00}s" : why;

    void Mark(int i)
    {
        var r = rows[i];
        if (!TryGetLiveClip(out var clip, out float clipNorm, out string why)) { markStatus = "Mark: " + why; return; }
        if (clip != r.clip) { markStatus = $"Mark: the avatar is dancing {clip.name}, not {r.clip.name}"; return; }
        if (r.bpm <= 0f) { markStatus = "Mark: set the BPM first"; return; }

        double now = EditorApplication.timeSinceStartup;
        if (markRow != i || now - lastMarkTime > 3.0) { markCos = markSin = 0; markCount = 0; }
        markRow = i;
        lastMarkTime = now;

        // Beat phase of this tap in the clip's own grid (beat 0 at clip time 0).
        double beatPos = clipNorm * r.clip.length * r.bpm / 60.0;
        double angle = 2.0 * Mathf.PI * (beatPos - System.Math.Floor(beatPos));
        markCos += System.Math.Cos(angle);
        markSin += System.Math.Sin(angle);
        markCount++;

        if (markCount >= 4)
        {
            double mean = System.Math.Atan2(markSin, markCos) / (2.0 * Mathf.PI);
            if (mean < 0) mean += 1.0;
            double spread = System.Math.Sqrt(markCos * markCos + markSin * markSin) / markCount;
            r.firstBeat = Round3((float)(mean * 60.0 / r.bpm));
            r.hasFirstBeat = true;
            markStatus = $"Mark: {r.clip.name} beat at {r.firstBeat:0.000}s from {markCount} taps (consistency {spread:0.00}; 1 = perfect)";
        }
        else markStatus = $"Mark: {markCount} tap(s), keep tapping on each step";
    }

    // ---------- Controller discovery ----------

    void Rebuild()
    {
        rows.Clear();
        indexChildren.Clear();
        tapRow = markRow = -1;
        taps.Clear();
        markCount = 0;
        if (controller == null) return;

        var byClip = new Dictionary<AnimationClip, Row>();
        foreach (var (gender, tree) in FindGenderTrees(controller))
        {
            foreach (var child in tree.children)
            {
                if (!(child.motion is AnimationClip clip)) continue;
                indexChildren[(gender == "Male", Mathf.RoundToInt(child.threshold))] = child;
                if (!byClip.TryGetValue(clip, out var row))
                {
                    row = new Row { clip = clip };
                    LoadEntry(row);
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

    void LoadEntry(Row row)
    {
        if (table == null) return;
        foreach (var e in table.clips)
        {
            if (e.clip != row.clip) continue;
            row.bpm = e.bpm;
            row.firstBeat = e.firstBeat;
            row.hasFirstBeat = e.hasFirstBeat;
            return;
        }
    }

    // ---------- Tempo and beat estimates from hip bounce ----------

    // Hip height with slow drift (crouching, stepping around) removed; null if unusable.
    static float[] HipSignal(AnimationClip clip, out string note)
    {
        note = null;
        var binding = EditorCurveBinding.FloatCurve("", typeof(Animator), "RootT.y");
        var curve = AnimationUtility.GetEditorCurve(clip, binding);
        if (curve == null || curve.length < 2) { note = "No RootT.y curve; tap the tempo instead."; return null; }

        int n = Mathf.FloorToInt(clip.length * SampleRate);
        if (n < 16) { note = "Clip too short to estimate."; return null; }

        var raw = new float[n];
        for (int i = 0; i < n; i++) raw[i] = curve.Evaluate(i / SampleRate);

        // High-pass: subtract a circular moving average one slowest-beat wide, so slow drift
        // doesn't swamp the per-beat bounce.
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
        if (energy < 1e-8f) { note = "Hips don't move; tap the tempo instead."; return null; }
        return x;
    }

    static void Estimate(Row r)
    {
        r.estimate = 0f;
        r.confidence = 0f;
        r.note = null;
        r.offsetEstimateBpm = -1f;

        var x = HipSignal(r.clip, out r.note);
        if (x == null) return;
        int n = x.Length;
        float energy = 0f;
        for (int i = 0; i < n; i++) energy += x[i] * x[i];

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

    // Beat position from the hip bounce at the clip's tempo: the beat is the lowest hip point (the knee dip).
    static void UpdateOffsetEstimate(Row r)
    {
        float bpm = r.bpm > 0f ? r.bpm : r.estimate;
        if (Mathf.Approximately(bpm, r.offsetEstimateBpm)) return;
        r.offsetEstimateBpm = bpm;
        r.offsetEstimate = -1f;
        r.offsetStrength = 0f;
        if (bpm <= 0f) return;

        var x = HipSignal(r.clip, out _);
        if (x == null) return;
        int n = x.Length;

        // One DFT bin at the beat frequency: x ~ A cos(w i - phi) gives Z = sum x e^{-i w i} ~ (A n / 2) e^{-i phi}.
        double w = 2.0 * System.Math.PI * bpm / 60.0 / SampleRate;
        double zRe = 0, zIm = 0, energy = 0;
        for (int i = 0; i < n; i++)
        {
            zRe += x[i] * System.Math.Cos(w * i);
            zIm -= x[i] * System.Math.Sin(w * i);
            energy += x[i] * x[i];
        }
        if (energy <= 1e-12) return;
        double phi = -System.Math.Atan2(zIm, zRe);
        double periodSamples = SampleRate * 60.0 / bpm;
        double lowest = (phi + System.Math.PI) / w;
        lowest -= System.Math.Floor(lowest / periodSamples) * periodSamples;

        r.offsetEstimate = (float)(lowest / SampleRate);
        // 1 = the hips move as a pure sinusoid at the beat rate.
        double rms = System.Math.Sqrt(energy / n);
        r.offsetStrength = Mathf.Clamp01((float)(System.Math.Sqrt(2.0) * System.Math.Sqrt(zRe * zRe + zIm * zIm) / (n * rms)));
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

        // A loop must hold whole beats for the beat grid to repeat; snap BPMs that are close.
        foreach (var r in rows)
        {
            if (r.bpm <= 0f) continue;
            float beats = r.clip.length * r.bpm / 60f;
            int whole = Mathf.RoundToInt(beats);
            if (whole < 1 || Mathf.Abs(beats - whole) > SnapBeats || Mathf.Abs(beats - whole) <= 1e-3f) continue;
            float snapped = Mathf.Round(whole * 60f / r.clip.length * 1000f) / 1000f;
            Debug.Log($"[DanceTempo] {r.clip.name}: BPM {r.bpm:0.###} -> {snapped:0.###} ({whole} beats per loop)");
            r.bpm = snapped;
        }

        Undo.RecordObject(table, "Bake Dance Tempo");
        table.clips.Clear();
        foreach (var r in rows)
            table.clips.Add(new DanceTempoTable.Entry { clip = r.clip, bpm = r.bpm, firstBeat = r.firstBeat, hasFirstBeat = r.hasFirstBeat });

        table.femaleIndexBpm = table.maleIndexBpm = new float[0];
        table.femaleIndexBeatsPerLoop = table.maleIndexBeatsPerLoop = new float[0];
        table.femaleIndexFirstBeat = table.maleIndexFirstBeat = new float[0];
        table.femaleIndexDuration = table.maleIndexDuration = new float[0];
        foreach (var (gender, tree) in FindGenderTrees(controller))
        {
            int count = 0;
            foreach (var child in tree.children) count = Mathf.Max(count, Mathf.RoundToInt(child.threshold) + 1);
            var bpmArr = new float[count];
            var beatsArr = new float[count];
            var firstArr = new float[count];
            var durArr = new float[count];
            foreach (var child in tree.children)
            {
                int idx = Mathf.RoundToInt(child.threshold);
                if (idx < 0 || !(child.motion is AnimationClip clip)) continue;
                // Loop length in the blend tree (for the dance-switch time-warp compensation); independent of BPM.
                if (child.timeScale > 0f) durArr[idx] = clip.length / child.timeScale;
                var row = FindRow(clip);
                if (row == null || row.bpm <= 0f) continue;
                bpmArr[idx] = row.bpm * child.timeScale;
                if (!row.hasFirstBeat || clip.length <= 0f) continue;
                // Beat k at clip time firstBeat + k * 60 / bpm; state time = clip time - cycle offset (normalized).
                beatsArr[idx] = clip.length * row.bpm / 60f;
                firstArr[idx] = Wrap(row.firstBeat / clip.length - child.cycleOffset, 1f);
            }
            if (gender == "Male") { table.maleIndexBpm = bpmArr; table.maleIndexBeatsPerLoop = beatsArr; table.maleIndexFirstBeat = firstArr; table.maleIndexDuration = durArr; }
            else { table.femaleIndexBpm = bpmArr; table.femaleIndexBeatsPerLoop = beatsArr; table.femaleIndexFirstBeat = firstArr; table.femaleIndexDuration = durArr; }
        }
        EditorUtility.SetDirty(table);

        SetupController();
        AssetDatabase.SaveAssets();
        Debug.Log($"[DanceTempo] Baked {rows.Count} clips into {TableAssetPath}.");
    }

    Row FindRow(AnimationClip clip)
    {
        foreach (var r in rows) if (r.clip == clip) return r;
        return null;
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
    static float Round3(float v) => Mathf.Round(v * 1000f) / 1000f;
    static float Wrap(float v, float period) => v - Mathf.Floor(v / period) * period;

    // Batch entry point: -executeMethod DanceTempoWindow.BakeEstimatesBatch
    // Fills clips that have no BPM yet with a confident auto-estimate, and clips without a beat position with a
    // confident hip estimate; logs every row and bakes. Low-confidence clips stay untagged until someone taps them in.
    const float BatchMinConfidence = 0.5f;
    const float BatchMinOffsetStrength = 0.5f;

    public static void BakeEstimatesBatch()
    {
        var w = CreateInstance<DanceTempoWindow>();
        try
        {
            w.OnEnable();
            foreach (var r in w.rows)
            {
                if (r.bpm <= 0f && r.estimate > 0f && r.confidence >= BatchMinConfidence) r.bpm = Round1(r.estimate);
                UpdateOffsetEstimate(r);
                if (!r.hasFirstBeat && r.offsetEstimate >= 0f && r.offsetStrength >= BatchMinOffsetStrength)
                {
                    r.firstBeat = Round3(r.offsetEstimate);
                    r.hasFirstBeat = true;
                }
                Debug.Log($"[DanceTempo] {r.clip.name}\tlen={r.clip.length:0.000}\tbpm={r.bpm:0.0}\test={r.estimate:0.0}\tconf={r.confidence:0.00}\t" +
                          $"beat={(r.hasFirstBeat ? r.firstBeat.ToString("0.000") : "-")}\tbeatEst={r.offsetEstimate:0.000}\tstrength={r.offsetStrength:0.00}\t{r.usage}\t{r.note}");
            }
            if (w.controller != null && w.rows.Count > 0) w.Bake();
        }
        finally { DestroyImmediate(w); }
    }
}
