using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using K = MESettingsUIKit;

// One-shot tool that adds the "BPM DANCE SYNC" checkbox (+ a sub-note and a beat offset slider) under "Enable Dance Transitions"
// in the "= DANCING" settings section, pushes everything below it down, and grows the section background.
// This is the reference example for adding settings UI; see MESettingsUIKit for the layout rules.
// Everything is registered with Undo; save the scene afterwards.
public static class MEBpmSyncSettingsBuilder
{
    const string RootName = "BPM Sync Settings";
    const string SectionName = "= DANCING";
    const string ToggleText = "BPM DANCE SYNC";
    const string NoteText = "Windows 10 2004 or newer recommended.";
    static readonly Color NoteColor = new Color(1f, 0.84313726f, 0.8627451f, 0.6f);
    const string TooltipText =
        "BPM dance sync matches the avatar's dance speed to the tempo of the music playing in your allowed apps. " +
        "On Windows 10 version 2004 or newer, it listens only to the app playing the music. " +
        "Older versions of Windows can only listen to all system audio, so game sounds, notifications or voice chat " +
        "may throw off the tempo and make the dance speed jump. " +
        "Works better with a longer Dance Change Time.";
    const float SliderMin = -100f, SliderMax = 400f;
    const string SliderTooltipText =
        "Lines the dance steps up with the beat you hear. If the avatar steps before the beat (common with Bluetooth " +
        "headphones, which delay the sound), raise it; if it steps after the beat, lower it.";
    const string ScenePath = "Assets/MATE ENGINE - Scenes/Mate Engine Main.unity";

    [MenuItem("MateEngine/Build BPM Sync Settings UI")]
    public static void Build() => Run(apply: true, interactive: true);

    // -executeMethod MEBpmSyncSettingsBuilder.ReportBatch   (dry run: logs what would change)
    public static void ReportBatch() { EditorSceneManager.OpenScene(ScenePath); Run(apply: false, interactive: false); }

    // -executeMethod MEBpmSyncSettingsBuilder.BuildBatch
    public static void BuildBatch()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);
        if (Run(apply: true, interactive: false)) EditorSceneManager.SaveScene(scene);
    }

    static bool Run(bool apply, bool interactive)
    {
        var log = new StringBuilder($"[BPM Sync UI] {(apply ? "BUILD" : "DRY RUN")}\n");
        var toggles = K.Find<SettingsHandlerToggles>();
        var anchor = toggles != null ? toggles.enableDanceSwitchToggle : null;
        if (anchor == null) return Fail("SettingsHandlerToggles.enableDanceSwitchToggle not found. Open 'Mate Engine Main' first.", interactive);

        var section = K.FindAncestor(anchor.transform, SectionName) as RectTransform;
        if (section == null) return Fail($"'{SectionName}' was not found above the Enable Dance Transitions toggle.", interactive);
        var mainMenu = section.parent;
        K.UseCanvasSpaceOf(section, mainMenu);

        var anchorRt = (RectTransform)anchor.transform;
        var parent = (RectTransform)anchorRt.parent;
        log.AppendLine($"anchor: {K.GetPath(anchorRt)}");

        if (apply)
        {
            Undo.SetCurrentGroupName("Build BPM Sync Settings UI");
        }
        int undoGroup = Undo.GetCurrentGroup();

        var existing = parent.Find(RootName) as RectTransform;
        if (existing != null)
        {
            if (interactive && !EditorUtility.DisplayDialog("Build BPM Sync Settings UI",
                    "The BPM sync checkbox already exists. Remove it and rebuild?", "Rebuild", "Cancel")) return false;
            float previous = existing.sizeDelta.y * K.ScaleInSpace(existing).y;
            float insertY = K.SpaceBounds(anchorRt).yMin;
            log.AppendLine($"removing previous build ({previous:0.#} units)");
            if (apply)
            {
                K.PushDown(mainMenu, anchorRt, existing, insertY, -previous, true, log);
                K.GrowScroll(section, -previous);
                Undo.DestroyObjectImmediate(existing.gameObject);
            }
        }

        // Measure the anchor row and the spacing to the row below it.
        Rect anchorBounds = K.SpaceBounds(anchorRt);
        float rowH = anchorBounds.height;
        float gap = rowH * 0.35f;
        var below = K.NextBelow(parent, anchorRt, anchorBounds.yMin);
        if (below != null) gap = Mathf.Max(2f, anchorBounds.yMin - K.SpaceBounds(below).yMax);
        log.AppendLine($"anchor bounds {K.Fmt(anchorBounds)}, gap {gap:0.#}, next row: {(below ? below.name : "none")}");

        var label = anchor.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
        if (label == null) return Fail("The Enable Dance Transitions toggle has no TMP label to copy.", interactive);

        var sliders = K.Find<SettingsHandlerSliders>();
        var sliderTemplate = sliders != null ? sliders.soundThresholdSlider : null;
        if (sliderTemplate == null) return Fail("SettingsHandlerSliders.soundThresholdSlider (the slider row to copy) was not found.", interactive);
        Rect sliderTemplateBounds = K.SpaceBounds((RectTransform)sliderTemplate.transform);

        if (!apply)
        {
            float estimate = rowH + gap + rowH * 0.6f + gap * 0.6f + sliderTemplateBounds.height;
            log.AppendLine($"would add about {estimate:0.#} units");
            K.PushDown(mainMenu, anchorRt, null, anchorBounds.yMin, estimate, false, log);
            Debug.Log(log.ToString());
            return true;
        }

        // Build: container, toggle, sub-note.
        var root = K.NewContainer(RootName, parent);
        var toggleGo = K.Clone(anchor.gameObject, root, "BPM Sync Toggle");
        var toggle = toggleGo.GetComponent<Toggle>();
        toggle.isOn = true;
        K.SetText(toggleGo, ToggleText);
        float cursor = K.Place(toggleGo.transform, anchorBounds.xMin, anchorBounds.yMin - gap);

        var toggleLabel = toggleGo.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
        Rect labelRect = toggleLabel != null ? K.SpaceRect((RectTransform)toggleLabel.transform) : K.SpaceBounds((RectTransform)toggleGo.transform);
        float noteH = labelRect.height * 0.7f;
        var note = K.MakeLabel(label, root, "BPM Sync Note", NoteText, Mathf.Max(labelRect.width, anchorBounds.width), noteH);
        var noteText = note.GetComponent<TMP_Text>();
        noteText.fontSize = label.fontSize * 0.7f;
        noteText.enableAutoSizing = false;
        noteText.color = NoteColor;
        cursor = K.Place(note, labelRect.xMin, cursor - gap * 0.2f);

        // Beat offset slider, indented under the checkbox like the note (it is a BPM-sync sub-setting).
        var sliderGo = K.Clone(sliderTemplate.gameObject, root, "BPM Beat Offset Slider");
        var slider = sliderGo.GetComponent<Slider>();
        slider.wholeNumbers = true;
        slider.minValue = SliderMin;
        slider.maxValue = SliderMax;
        slider.SetValueWithoutNotify(SettingsHandlerSliders.DefaultBpmBeatOffsetMs);
        var sliderLabel = sliderGo.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
        if (sliderLabel != null) sliderLabel.text = $"BEAT OFFSET: {SettingsHandlerSliders.DefaultBpmBeatOffsetMs:0} MS";
        var sliderRt = (RectTransform)sliderGo.transform;
        Rect sliderRect = K.SpaceRect(sliderRt);
        float indent = labelRect.xMin - sliderTemplateBounds.xMin;
        if (indent > 0f && indent < sliderRect.width * 0.5f) K.SetSpaceSize(sliderRt, sliderRect.width - indent, sliderRect.height);
        cursor = K.Place(sliderGo.transform, labelRect.xMin, cursor - gap * 0.6f);

        float added = anchorBounds.yMin - cursor;
        log.AppendLine($"added {added:0.#} units");
        K.PushDown(mainMenu, anchorRt, root, anchorBounds.yMin, added, true, log);
        K.GrowScroll(section, added);
        root.sizeDelta = new Vector2(0f, added / K.Safe(K.ScaleInSpace(root).y));

        Undo.RecordObject(toggles, "Wire BPM sync toggle");
        toggles.enableBpmSyncToggle = toggle;
        K.CopyTooltip(anchor, toggle, TooltipText, log);
        EditorUtility.SetDirty(toggles);

        Undo.RecordObject(sliders, "Wire BPM beat offset slider");
        sliders.bpmBeatOffsetSlider = slider;
        sliders.bpmBeatOffsetLabel = sliderLabel;
        K.CopyTooltip(sliderTemplate, slider, SliderTooltipText, log);
        EditorUtility.SetDirty(sliders);

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(section.gameObject.scene);
        if (interactive) Selection.activeGameObject = root.gameObject;
        Debug.Log(log.ToString());
        return true;
    }

    // -executeMethod MEBpmSyncSettingsBuilder.AddTooltipBatch
    // Adds/updates only the hover tooltip on an already-built BPM checkbox (leaves the rest of the layout alone).
    public static void AddTooltipBatch()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);
        var toggles = K.Find<SettingsHandlerToggles>();
        if (toggles == null || toggles.enableDanceSwitchToggle == null || toggles.enableBpmSyncToggle == null)
        {
            Debug.LogError("[BPM Sync UI] Enable Dance Transitions or BPM Dance Sync toggle is not wired on SettingsHandlerToggles.");
            return;
        }
        var log = new StringBuilder("[BPM Sync UI] TOOLTIP\n");
        if (K.CopyTooltip(toggles.enableDanceSwitchToggle, toggles.enableBpmSyncToggle, TooltipText, log))
            EditorSceneManager.SaveScene(scene);
        Debug.Log(log.ToString());
    }

    static bool Fail(string msg, bool interactive)
    {
        Debug.LogError("[BPM Sync UI] " + msg);
        if (interactive) EditorUtility.DisplayDialog("Build BPM Sync Settings UI", msg, "OK");
        return false;
    }
}
