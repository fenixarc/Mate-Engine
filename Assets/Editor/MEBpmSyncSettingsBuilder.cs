using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.UI;
using G = MEGeminiSettingsBuilder;

// One-shot tool that adds the "BPM DANCE SYNC" checkbox (+ a sub-note) under "Enable Dance Transitions"
// in the "= DANCING" settings section, pushes everything below it down, and grows the section background.
// Section backgrounds (Category Background/Image (N)) follow their headers through locked PositionConstraints,
// so their offsets are unlocked, corrected, and re-locked here. Layout is done in the settings canvas's local
// space (see MEGeminiSettingsBuilder). Everything is registered with Undo; save the scene afterwards.
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
        "may throw off the tempo and make the dance speed jump.";
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
        var toggles = G.Find<SettingsHandlerToggles>();
        var anchor = toggles != null ? toggles.enableDanceSwitchToggle : null;
        if (anchor == null) return Fail("SettingsHandlerToggles.enableDanceSwitchToggle not found. Open 'Mate Engine Main' first.", interactive);

        var section = G.FindAncestor(anchor.transform, SectionName) as RectTransform;
        if (section == null) return Fail($"'{SectionName}' was not found above the Enable Dance Transitions toggle.", interactive);
        var mainMenu = section.parent;
        var canvases = section.GetComponentsInParent<Canvas>(true);
        G.space = canvases.Length > 0 ? canvases[canvases.Length - 1].transform : mainMenu;

        var anchorRt = (RectTransform)anchor.transform;
        var parent = (RectTransform)anchorRt.parent;
        log.AppendLine($"anchor: {G.GetPath(anchorRt)}");

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
            float previous = existing.sizeDelta.y * G.ScaleInSpace(existing).y;
            float insertY = G.SpaceBounds(anchorRt).yMin;
            log.AppendLine($"removing previous build ({previous:0.#} units)");
            if (apply)
            {
                PushDown(mainMenu, anchorRt, existing, insertY, -previous, true, log);
                G.GrowScroll(section, -previous);
                Undo.DestroyObjectImmediate(existing.gameObject);
            }
        }

        // Measure the anchor row and the spacing to the row below it.
        Rect anchorBounds = G.SpaceBounds(anchorRt);
        float rowH = anchorBounds.height;
        float gap = rowH * 0.35f;
        var below = NextBelow(parent, anchorRt, anchorBounds.yMin);
        if (below != null) gap = Mathf.Max(2f, anchorBounds.yMin - G.SpaceBounds(below).yMax);
        log.AppendLine($"anchor bounds {Fmt(anchorBounds)}, gap {gap:0.#}, next row: {(below ? below.name : "none")}");

        var label = anchor.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
        if (label == null) return Fail("The Enable Dance Transitions toggle has no TMP label to copy.", interactive);

        if (!apply)
        {
            float estimate = rowH + gap + rowH * 0.6f;
            log.AppendLine($"would add about {estimate:0.#} units");
            PushDown(mainMenu, anchorRt, null, anchorBounds.yMin, estimate, false, log);
            Debug.Log(log.ToString());
            return true;
        }

        // Build: container, toggle, sub-note.
        var root = G.NewContainer(RootName, parent);
        var toggleGo = G.Clone(anchor.gameObject, root, "BPM Sync Toggle");
        var toggle = toggleGo.GetComponent<Toggle>();
        toggle.isOn = true;
        G.SetText(toggleGo, ToggleText);
        float cursor = G.Place(toggleGo.transform, anchorBounds.xMin, anchorBounds.yMin - gap);

        var toggleLabel = toggleGo.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
        Rect labelRect = toggleLabel != null ? G.SpaceRect((RectTransform)toggleLabel.transform) : G.SpaceBounds((RectTransform)toggleGo.transform);
        float noteH = labelRect.height * 0.7f;
        var note = G.MakeLabel(label, root, "BPM Sync Note", NoteText, Mathf.Max(labelRect.width, anchorBounds.width), noteH);
        var noteText = note.GetComponent<TMP_Text>();
        noteText.fontSize = label.fontSize * 0.7f;
        noteText.enableAutoSizing = false;
        noteText.color = NoteColor;
        cursor = G.Place(note, labelRect.xMin, cursor - gap * 0.2f);

        float added = anchorBounds.yMin - cursor;
        log.AppendLine($"added {added:0.#} units");
        PushDown(mainMenu, anchorRt, root, anchorBounds.yMin, added, true, log);
        G.GrowScroll(section, added);
        root.sizeDelta = new Vector2(0f, added / G.Safe(G.ScaleInSpace(root).y));

        Undo.RecordObject(toggles, "Wire BPM sync toggle");
        toggles.enableBpmSyncToggle = toggle;
        CopyTooltip(anchor, toggle, log);
        EditorUtility.SetDirty(toggles);

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
        var toggles = G.Find<SettingsHandlerToggles>();
        if (toggles == null || toggles.enableDanceSwitchToggle == null || toggles.enableBpmSyncToggle == null)
        {
            Debug.LogError("[BPM Sync UI] Enable Dance Transitions or BPM Dance Sync toggle is not wired on SettingsHandlerToggles.");
            return;
        }
        var log = new StringBuilder("[BPM Sync UI] TOOLTIP\n");
        if (CopyTooltip(toggles.enableDanceSwitchToggle, toggles.enableBpmSyncToggle, log))
            EditorSceneManager.SaveScene(scene);
        Debug.Log(log.ToString());
    }

    // Copies the source toggle's UiTooltip (look, delay, hover zone, offsets) onto target and sets the BPM text.
    // Uses tooltipText with no locKey, like the rest of the BPM UI, which is English-only.
    static bool CopyTooltip(Toggle source, Toggle target, StringBuilder log)
    {
        var src = source.GetComponent<UiTooltip>();
        if (src == null) { log.AppendLine("  source toggle has no UiTooltip"); return false; }

        var tip = target.GetComponent<UiTooltip>();
        if (tip == null) tip = Undo.AddComponent<UiTooltip>(target.gameObject);
        Undo.RecordObject(tip, "BPM sync tooltip");
        EditorUtility.CopySerialized(src, tip);
        tip.locKey = "";
        tip.tooltipText = TooltipText;
        EditorUtility.SetDirty(tip);
        EditorSceneManager.MarkSceneDirty(target.gameObject.scene);
        log.AppendLine($"  tooltip copied from {src.name} onto {G.GetPath(target.transform)}");
        return true;
    }

    // Moves everything below insertY down by amount (negative = up): siblings at every level from the anchor's
    // parent up to the main menu, then fixes the section backgrounds and their locked constraints.
    static void PushDown(Transform mainMenu, RectTransform anchor, RectTransform skip, float insertY, float amount, bool apply, StringBuilder log)
    {
        var moved = new HashSet<Transform>();
        for (Transform level = anchor; level != null && level != mainMenu; level = level.parent)
        {
            var levelParent = level.parent;
            if (levelParent == null) break;
            foreach (Transform sib in levelParent)
            {
                if (sib == level || sib == skip || sib.name == "Category Background" || !(sib is RectTransform rt)) continue;
                var r = G.SpaceBounds(rt);
                if (r.height <= 0f || r.center.y >= insertY) continue;
                moved.Add(sib);
                log.AppendLine($"  move {G.GetPath(sib)} by {amount:0.#}");
                if (apply)
                {
                    Undo.RecordObject(rt, "Move settings row");
                    G.MoveInSpace(rt, Vector2.down * amount);
                }
            }
            if (levelParent == mainMenu) break;
        }

        var bg = mainMenu.Find("Category Background");
        if (bg == null) { log.AppendLine("  no Category Background"); return; }

        // World units per canvas unit, from the existing locked constraints.
        var ratios = new List<float>();
        foreach (Transform t in bg)
        {
            var pc = t.GetComponent<PositionConstraint>();
            if (pc == null || pc.sourceCount == 0 || !(t is RectTransform rt)) continue;
            var src = pc.GetSource(0).sourceTransform as RectTransform;
            if (src == null) continue;
            float d = PivotY(rt) - PivotY(src);
            if (Mathf.Abs(d) > 1f) ratios.Add(pc.translationOffset.y / d);
        }
        ratios.Sort();
        float k = ratios.Count > 0 ? ratios[ratios.Count / 2] : 0f;
        log.AppendLine($"  constraint world/canvas ratio k = {k:0.####} (from {ratios.Count} constraints)");

        foreach (Transform t in bg)
        {
            if (!(t is RectTransform img)) continue;
            var r = G.SpaceRect(img);
            if (r.height <= 0f) continue;

            bool grows = r.yMax >= insertY && r.yMin <= insertY;
            float imgMove = grows ? amount * 0.5f : (r.center.y < insertY ? amount : 0f);
            var pc = img.GetComponent<PositionConstraint>();
            var src = pc != null && pc.sourceCount > 0 ? pc.GetSource(0).sourceTransform : null;
            float srcMove = src != null && IsUnderAny(src, moved) ? amount : 0f;
            if (!grows && Mathf.Approximately(imgMove, 0f)) continue;

            float offsetDelta = -(imgMove - srcMove) * k;
            log.AppendLine($"  bg {img.name}: {(grows ? "grow" : "shift")} imgMove={imgMove:0.#} srcMove={srcMove:0.#} " +
                           $"src={(src ? src.name : "none")} offsetY {(pc ? pc.translationOffset.y : 0f):0.###} -> {(pc ? pc.translationOffset.y + offsetDelta : 0f):0.###}");
            if (!apply) continue;

            Undo.RecordObject(img, "Adjust section background");
            if (grows) img.sizeDelta += new Vector2(0f, amount / G.Safe(G.ScaleInSpace(img).y));
            // Keep the at-rest layout in step with the constrained position.
            G.MoveInSpace(img, Vector2.down * imgMove);

            if (pc != null && !Mathf.Approximately(offsetDelta, 0f))
            {
                Undo.RecordObject(pc, "Adjust background constraint");
                pc.locked = false;
                var off = pc.translationOffset;
                off.y += offsetDelta;
                pc.translationOffset = off;
                pc.locked = true;
                EditorUtility.SetDirty(pc);
            }
        }
    }

    static float PivotY(RectTransform rt) => G.ToSpace(rt, new Vector2(0f, 0f)).y;

    static bool IsUnderAny(Transform t, HashSet<Transform> set)
    {
        for (; t != null; t = t.parent) if (set.Contains(t)) return true;
        return false;
    }

    static RectTransform NextBelow(Transform parent, RectTransform anchor, float y)
    {
        RectTransform best = null;
        float bestTop = float.MinValue;
        foreach (Transform t in parent)
        {
            if (t == anchor || !(t is RectTransform rt) || !t.gameObject.activeSelf) continue;
            var r = G.SpaceBounds(rt);
            if (r.height <= 0f || r.yMax > y + 0.01f) continue;
            if (r.yMax > bestTop) { bestTop = r.yMax; best = rt; }
        }
        return best;
    }

    static string Fmt(Rect r) => $"x[{r.xMin:0.#},{r.xMax:0.#}] y[{r.yMin:0.#},{r.yMax:0.#}]";

    static bool Fail(string msg, bool interactive)
    {
        Debug.LogError("[BPM Sync UI] " + msg);
        if (interactive) EditorUtility.DisplayDialog("Build BPM Sync Settings UI", msg, "OK");
        return false;
    }
}
