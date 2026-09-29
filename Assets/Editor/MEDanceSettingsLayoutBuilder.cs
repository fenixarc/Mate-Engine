using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Localization.Components;
using UnityEngine.UI;
using K = MESettingsUIKit;

// One-shot tool that reorders the "= DANCING" settings section so each slider sits indented under the checkbox it
// belongs to (like BEAT OFFSET under BPM DANCE SYNC), and adds a SliderValueLabel so each label shows its value:
//   AVATAR CAN DANCE
//       START DANCING VOL. THRESHOLD: 0.20
//   ENABLE DANCE TRANSITIONS
//       DANCE CHANGE TIME: 15 S
//       DANCE TRANSITION TIME: 2 S
//   BPM DANCE SYNC (+ note, BEAT OFFSET)
// Spacing is measured from the original layout, so the tool refuses to run twice. If the section height changes,
// the rest of the menu is pushed with MESettingsUIKit.PushDown. Everything is registered with Undo; save the scene afterwards.
public static class MEDanceSettingsLayoutBuilder
{
    const string SectionName = "= DANCING";
    const string BpmRootName = "BPM Sync Settings";
    const string ScenePath = "Assets/MATE ENGINE - Scenes/Mate Engine Main.unity";
    const string Title = "Build Dancing Settings Layout";

    [MenuItem("MateEngine/" + Title)]
    public static void Build() => Run(apply: true, interactive: true);

    [MenuItem("MateEngine/" + Title + " (Dry Run)")]
    public static void Report() => Run(apply: false, interactive: true);

    // -executeMethod MEDanceSettingsLayoutBuilder.ReportBatch   (dry run: logs what would change)
    public static void ReportBatch() { EditorSceneManager.OpenScene(ScenePath); Run(apply: false, interactive: false); }

    // -executeMethod MEDanceSettingsLayoutBuilder.BuildBatch
    public static void BuildBatch()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);
        if (Run(apply: true, interactive: false)) EditorSceneManager.SaveScene(scene);
    }

    struct Row
    {
        public RectTransform rt;
        public bool sub;
        public Row(Component c, bool sub) { rt = (RectTransform)c.transform; this.sub = sub; }
    }

    static bool Run(bool apply, bool interactive)
    {
        var log = new StringBuilder($"[Dancing UI] {(apply ? "BUILD" : "DRY RUN")}\n");
        var toggles = K.Find<SettingsHandlerToggles>();
        var sliders = K.Find<SettingsHandlerSliders>();
        if (toggles == null || sliders == null) return Fail("SettingsHandlerToggles/SettingsHandlerSliders not found. Open 'Mate Engine Main' first.", interactive);

        var dancing = toggles.enableDancingToggle;
        var transitions = toggles.enableDanceSwitchToggle;
        var threshold = sliders.soundThresholdSlider;
        var changeTime = sliders.danceSwitchTimeSlider;
        var transitionTime = sliders.danceTransitionTimeSlider;
        if (dancing == null || transitions == null || threshold == null || changeTime == null || transitionTime == null)
            return Fail("One of the dancing toggles/sliders is not wired on SettingsHandlerToggles/SettingsHandlerSliders.", interactive);

        var section = K.FindAncestor(transitions.transform, SectionName) as RectTransform;
        if (section == null) return Fail($"'{SectionName}' was not found above the Enable Dance Transitions toggle.", interactive);
        var mainMenu = section.parent;
        K.UseCanvasSpaceOf(section, mainMenu);

        var parent = transitions.transform.parent;
        foreach (var c in new Component[] { dancing, threshold, changeTime, transitionTime })
            if (c.transform.parent != parent) return Fail($"{K.GetPath(c.transform)} is not a sibling of Enable Dance Transitions.", interactive);
        var bpm = parent.Find(BpmRootName) as RectTransform;
        var bpmToggle = toggles.enableBpmSyncToggle;

        var dancingB = K.SpaceBounds((RectTransform)dancing.transform);
        var transitionsB = K.SpaceBounds((RectTransform)transitions.transform);
        var thresholdB = K.SpaceBounds((RectTransform)threshold.transform);
        var changeB = K.SpaceBounds((RectTransform)changeTime.transform);

        if (thresholdB.center.y > transitionsB.center.y)
            return Fail("The dancing section is already in the new order (the threshold slider is above Enable Dance Transitions).", interactive);

        // Spacing from the original layout: toggle -> toggle, toggle -> slider (the gap BPM sync kept under its
        // anchor), and slider -> slider.
        float gapToggles = dancingB.yMin - transitionsB.yMax;
        float gapToSlider = bpmToggle != null ? transitionsB.yMin - K.SpaceBounds((RectTransform)bpmToggle.transform).yMax : gapToggles;
        float gapSliders = thresholdB.yMin - changeB.yMax;
        log.AppendLine($"gaps: toggle->toggle {gapToggles:0.#}, toggle->slider {gapToSlider:0.#}, slider->slider {gapSliders:0.#}");

        // Sub-rows are indented to the start of the checkbox label, like BEAT OFFSET.
        // Top-level rows only move vertically.
        var label = dancing.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
        float subLeft = label != null ? K.SpaceRect((RectTransform)label.transform).xMin : dancingB.xMin + 20f;

        var rows = new List<Row>
        {
            new Row(dancing, false), new Row(threshold, true),
            new Row(transitions, false), new Row(changeTime, true), new Row(transitionTime, true),
        };
        if (bpm != null) rows.Add(new Row(bpm, false));

        float top = rows.Max(r => K.SpaceBounds(r.rt).yMax);
        float oldBottom = rows.Min(r => K.SpaceBounds(r.rt).yMin);

        if (apply) Undo.SetCurrentGroupName(Title);
        int undoGroup = Undo.GetCurrentGroup();

        // Stack the rows from the top of the section.
        float cursor = top;
        Row? prev = null;
        foreach (var row in rows)
        {
            float gap = 0f;
            if (prev.HasValue)
                gap = row.sub ? (prev.Value.sub ? gapSliders : gapToggles) : (prev.Value.sub ? gapToSlider : gapToggles);
            cursor -= gap;

            if (row.sub)
            {
                // Narrow the slider bar by the indent so its right edge stays in line with the rows above.
                var bounds = K.SpaceBounds(row.rt);
                float indent = subLeft - bounds.xMin;
                var bar = K.SpaceRect(row.rt);
                log.AppendLine($"  {row.rt.name}: indent {indent:0.#}, width {bar.width:0.#} -> {bar.width - indent:0.#}");
                if (apply && indent > 0f && indent < bar.width * 0.5f)
                {
                    Undo.RecordObject(row.rt, "Indent settings slider");
                    K.SetSpaceSize(row.rt, bar.width - indent, bar.height);
                }
            }

            var before = K.SpaceBounds(row.rt);
            float x = row.sub ? subLeft : before.xMin;
            log.AppendLine($"  {row.rt.name}: top {before.yMax:0.#} -> {cursor:0.#}, left {before.xMin:0.#} -> {x:0.#}");
            if (apply)
            {
                Undo.RecordObject(row.rt, "Move settings row");
                cursor = K.Place(row.rt, x, cursor);
            }
            else cursor -= before.height;
            prev = row;
        }

        // Keep the rest of the menu (and the section background) in step with the new section height.
        float grow = cursor - oldBottom;
        log.AppendLine($"section bottom {oldBottom:0.#} -> {cursor:0.#} (grows by {-grow:0.#})");
        if (!Mathf.Approximately(grow, 0f))
        {
            var skip = new HashSet<Transform>(rows.Select(r => (Transform)r.rt));
            K.PushDown(mainMenu, (RectTransform)transitions.transform, skip, oldBottom, -grow, apply, log);
            if (apply) K.GrowScroll(section, -grow);
        }

        AddValueLabel(threshold, "0.00", "", apply, log);
        AddValueLabel(changeTime, "0", " S", apply, log);
        AddValueLabel(transitionTime, "0", " S", apply, log);

        if (!apply)
        {
            Debug.Log(log.ToString());
            return true;
        }

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(section.gameObject.scene);
        if (interactive) Selection.activeGameObject = section.gameObject;
        Debug.Log(log.ToString());
        return true;
    }

    static void AddValueLabel(Slider slider, string format, string suffix, bool apply, StringBuilder log)
    {
        var text = slider.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
        var localizer = text != null ? text.GetComponent<LocalizeStringEvent>() : null;
        log.AppendLine($"  value label on {slider.name}: text={(text ? text.name : "none")}, localized={localizer != null}, format '{format}{suffix}'");
        if (!apply || text == null) return;

        var valueLabel = slider.GetComponent<SliderValueLabel>();
        if (valueLabel == null) valueLabel = Undo.AddComponent<SliderValueLabel>(slider.gameObject);
        Undo.RecordObject(valueLabel, "Wire slider value label");
        valueLabel.slider = slider;
        valueLabel.label = text;
        valueLabel.localizer = localizer;
        valueLabel.valueFormat = format;
        valueLabel.suffix = suffix;
        EditorUtility.SetDirty(valueLabel);
    }

    static bool Fail(string msg, bool interactive)
    {
        Debug.LogError("[Dancing UI] " + msg);
        if (interactive) EditorUtility.DisplayDialog(Title, msg, "OK");
        return false;
    }
}
