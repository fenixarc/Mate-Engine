using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.UI;

// Shared helpers for one-shot Editor tools that add controls to the settings menu in 'Mate Engine Main'.
// See MEBpmSyncSettingsBuilder for a complete example (insert rows under an existing control, push the rest down).
//
// Rules that the settings menu layout needs (each one broke an earlier attempt):
// - Lay out in the local space of the settings root Canvas (set `space` first), NOT world space: the canvas is saved
//   inactive with a scale of 0 (Unity only scales it while active), so world-space sizes are all zero in the Editor.
// - Section backgrounds (Category Background/Image (N)) follow their headers through LOCKED PositionConstraints.
//   Moving only the rects is undone at runtime; PushDown unlocks, corrects the offset, and re-locks each constraint.
// - Rows can be nested several levels below the section header, so PushDown moves siblings at every level from
//   the anchor up to the main menu, not just the top-level sections. GrowScroll then extends the scroll content.
// - Clone strips Mate Engine and Localization components (Localization would overwrite the new label text) and
//   clears persistent listeners. Wire the new controls to their SettingsHandler* fields afterwards, and copy the
//   neighbouring control's UiTooltip with CopyTooltip.
// - Store the added height in the new container's sizeDelta.y so a rebuild can remove it and push everything back up.
// - Register everything with Undo, provide a dry-run (apply: false) that only logs, and save the scene afterwards.
public static class MESettingsUIKit
{
    const string CategoryBackground = "Category Background";

    // Layout space: the root canvas transform of the settings menu.
    public static Transform space;

    // Sets `space` to the root canvas above t.
    public static void UseCanvasSpaceOf(Transform t, Transform fallback)
    {
        var canvases = t.GetComponentsInParent<Canvas>(true);
        space = canvases.Length > 0 ? canvases[canvases.Length - 1].transform : fallback;
    }

    // ---------- construction helpers ----------

    public static RectTransform NewContainer(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = Vector2.zero;
        rt.anchoredPosition = Vector2.zero;
        rt.localScale = Vector3.one;
        go.layer = parent.gameObject.layer;
        return rt;
    }

    // Clones template under parent, keeping its size and scale as seen in the canvas space.
    public static GameObject Clone(GameObject template, Transform parent, string name)
    {
        var tRt = (RectTransform)template.transform;
        Rect tRect = SpaceRect(tRt);
        Vector2 tScale = ScaleInSpace(tRt);

        var go = Object.Instantiate(template, parent, false);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        go.name = name;
        go.SetActive(true);
        StripProjectComponents(go);
        ClearPersistentListeners(go);

        var rt = (RectTransform)go.transform;
        Vector2 pScale = ScaleInSpace(parent);
        rt.localScale = new Vector3(tScale.x / Safe(pScale.x), tScale.y / Safe(pScale.y), 1f);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        SetSpaceSize(rt, tRect.width, tRect.height);
        rt.anchoredPosition = Vector2.zero;
        return go;
    }

    public static RectTransform MakeLabel(TMP_Text template, Transform parent, string name, string text, float w, float h)
    {
        var go = Clone(template.gameObject, parent, name);
        var rt = (RectTransform)go.transform;
        SetSpaceSize(rt, w, h);
        var tmp = go.GetComponent<TMP_Text>();
        tmp.text = text;
        tmp.alignment = TextAlignmentOptions.MidlineLeft;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        return rt;
    }

    public static InputField MakeInput(InputField template, Transform parent, string name, string placeholder, float w, float h)
    {
        var input = Clone(template.gameObject, parent, name).GetComponent<InputField>();
        var rt = (RectTransform)input.transform;
        SetSpaceSize(rt, w, h);
        input.lineType = InputField.LineType.SingleLine;
        input.text = "";

        int fontSize = Mathf.Max(8, Mathf.RoundToInt(rt.rect.height * 0.45f));
        if (input.textComponent != null) input.textComponent.fontSize = fontSize;
        if (input.placeholder is Text ph)
        {
            ph.fontSize = fontSize;
            ph.text = placeholder;
        }
        return input;
    }

    public static Button MakeButton(Button template, Transform parent, string name, string label, float w)
    {
        var button = Clone(template.gameObject, parent, name).GetComponent<Button>();
        var rt = (RectTransform)button.transform;
        var r = SpaceRect(rt);
        SetSpaceSize(rt, Mathf.Max(r.width, w), r.height);
        foreach (var t in button.GetComponentsInChildren<TMP_Text>(true))
        {
            t.text = label;
            t.enableAutoSizing = true;
        }
        return button;
    }

    public static void SetText(GameObject go, string text)
    {
        foreach (var t in go.GetComponentsInChildren<TMP_Text>(true)) t.text = text;
        foreach (var t in go.GetComponentsInChildren<Text>(true)) t.text = text;
    }

    // Removes Mate Engine scripts and Localization components (which would overwrite the new label text).
    public static void StripProjectComponents(GameObject go)
    {
        foreach (var c in go.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (c == null) continue;
            string asm = c.GetType().Assembly.GetName().Name;
            if (asm == "Assembly-CSharp" || asm.StartsWith("Unity.Localization"))
                Object.DestroyImmediate(c);
        }
    }

    public static void ClearPersistentListeners(GameObject go)
    {
        foreach (var c in go.GetComponentsInChildren<Component>(true))
        {
            if (c == null) continue;
            var so = new SerializedObject(c);
            var it = so.GetIterator();
            bool changed = false;
            while (it.Next(true))
            {
                if (it.isArray && it.propertyPath.EndsWith("m_PersistentCalls.m_Calls") && it.arraySize > 0)
                {
                    it.arraySize = 0;
                    changed = true;
                }
            }
            if (changed) so.ApplyModifiedPropertiesWithoutUndo();
        }
    }

    // Copies the source control's UiTooltip (look, delay, hover zone, offsets) onto target and sets the text.
    // Uses tooltipText with no locKey (English-only).
    public static bool CopyTooltip(Component source, Component target, string text, StringBuilder log)
    {
        var src = source.GetComponent<UiTooltip>();
        if (src == null) { log.AppendLine($"  {source.name} has no UiTooltip"); return false; }

        var tip = target.GetComponent<UiTooltip>();
        if (tip == null) tip = Undo.AddComponent<UiTooltip>(target.gameObject);
        Undo.RecordObject(tip, "Copy tooltip");
        EditorUtility.CopySerialized(src, tip);
        tip.locKey = "";
        tip.tooltipText = text;
        EditorUtility.SetDirty(tip);
        EditorSceneManager.MarkSceneDirty(target.gameObject.scene);
        log.AppendLine($"  tooltip copied from {src.name} onto {GetPath(target.transform)}");
        return true;
    }

    // ---------- canvas-space layout helpers ----------

    public static float Safe(float v) => Mathf.Abs(v) < 1e-6f ? 1e-6f : v;

    // Maps a point from t's local rect space to its parent's local rect space.
    public static Vector2 StepUp(Transform t, Vector2 p)
    {
        if (!(t is RectTransform rt))
            return (Vector2)t.localPosition + Vector2.Scale((Vector2)t.localScale, p);

        Vector2 pivotPos;
        if (rt.parent is RectTransform prt)
        {
            Rect pr = prt.rect;
            Vector2 aMin = pr.min + Vector2.Scale(pr.size, rt.anchorMin);
            Vector2 aMax = pr.min + Vector2.Scale(pr.size, rt.anchorMax);
            pivotPos = new Vector2(Mathf.Lerp(aMin.x, aMax.x, rt.pivot.x), Mathf.Lerp(aMin.y, aMax.y, rt.pivot.y)) + rt.anchoredPosition;
        }
        else pivotPos = rt.anchoredPosition;
        return pivotPos + Vector2.Scale((Vector2)rt.localScale, p);
    }

    public static Vector2 ToSpace(Transform t, Vector2 p)
    {
        for (; t != null && t != space; t = t.parent) p = StepUp(t, p);
        return p;
    }

    public static Vector2 ScaleInSpace(Transform t)
    {
        Vector2 s = Vector2.one;
        for (; t != null && t != space; t = t.parent) s = Vector2.Scale(s, (Vector2)t.localScale);
        return s;
    }

    public static Rect SpaceRect(RectTransform rt)
    {
        Rect r = rt.rect;
        Vector2 a = ToSpace(rt, r.min), b = ToSpace(rt, r.max);
        return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
    }

    // Requires collapsed anchors (anchorMin == anchorMax), so sizeDelta equals the rect size.
    public static void SetSpaceSize(RectTransform rt, float w, float h)
    {
        Vector2 s = ScaleInSpace(rt);
        rt.sizeDelta = new Vector2(Mathf.Abs(w / Safe(s.x)), Mathf.Abs(h / Safe(s.y)));
    }

    public static void MoveInSpace(RectTransform rt, Vector2 delta)
    {
        Vector2 ps = ScaleInSpace(rt.parent);
        rt.anchoredPosition += new Vector2(delta.x / Safe(ps.x), delta.y / Safe(ps.y));
    }

    // Moves t so the top-left of its visible bounds is at (left, top). Returns the new bottom.
    public static float Place(Transform t, float left, float top)
    {
        var rt = (RectTransform)t;
        var b = SpaceBounds(rt);
        MoveInSpace(rt, new Vector2(left - b.xMin, top - b.yMax));
        return top - b.height;
    }

    // Union of the rects of all descendants that are active relative to root.
    public static Rect SpaceBounds(RectTransform root)
    {
        bool any = false;
        Rect u = default;
        foreach (var rt in root.GetComponentsInChildren<RectTransform>(true))
        {
            if (!ActiveUnder(rt, root)) continue;
            var r = SpaceRect(rt);
            if (r.width <= 0f || r.height <= 0f) continue;
            u = any ? Rect.MinMaxRect(Mathf.Min(u.xMin, r.xMin), Mathf.Min(u.yMin, r.yMin), Mathf.Max(u.xMax, r.xMax), Mathf.Max(u.yMax, r.yMax)) : r;
            any = true;
        }
        return any ? u : SpaceRect(root);
    }

    public static bool ActiveUnder(Transform t, Transform root)
    {
        for (; t != null; t = t.parent)
        {
            if (!t.gameObject.activeSelf) return false;
            if (t == root) return true;
        }
        return true;
    }

    // The nearest active sibling of anchor whose top is below y (the next row down), or null.
    public static RectTransform NextBelow(Transform parent, RectTransform anchor, float y)
    {
        RectTransform best = null;
        float bestTop = float.MinValue;
        foreach (Transform t in parent)
        {
            if (t == anchor || !(t is RectTransform rt) || !t.gameObject.activeSelf) continue;
            var r = SpaceBounds(rt);
            if (r.height <= 0f || r.yMax > y + 0.01f) continue;
            if (r.yMax > bestTop) { bestTop = r.yMax; best = rt; }
        }
        return best;
    }

    // ---------- pushing the menu down ----------

    // Moves everything below insertY down by amount (negative = up): siblings at every level from the anchor's
    // parent up to the main menu, then fixes the section backgrounds and their locked constraints.
    // skip is the newly built container (it must not move). With apply = false it only logs.
    public static void PushDown(Transform mainMenu, RectTransform anchor, RectTransform skip, float insertY, float amount, bool apply, StringBuilder log)
    {
        var bg = mainMenu.Find(CategoryBackground);
        // Measured before anything moves: moving the rows first would skew the constraint offsets it is read from.
        float k = bg != null ? ConstraintRatio(bg, log) : 0f;

        var moved = new HashSet<Transform>();
        for (Transform level = anchor; level != null && level != mainMenu; level = level.parent)
        {
            var levelParent = level.parent;
            if (levelParent == null) break;
            foreach (Transform sib in levelParent)
            {
                if (sib == level || sib == skip || sib.name == CategoryBackground || !(sib is RectTransform rt)) continue;
                var r = SpaceBounds(rt);
                if (r.height <= 0f || r.center.y >= insertY) continue;
                moved.Add(sib);
                log.AppendLine($"  move {GetPath(sib)} by {amount:0.#}");
                if (apply)
                {
                    Undo.RecordObject(rt, "Move settings row");
                    MoveInSpace(rt, Vector2.down * amount);
                }
            }
            if (levelParent == mainMenu) break;
        }

        if (bg == null) { log.AppendLine("  no Category Background"); return; }

        foreach (Transform t in bg)
        {
            if (!(t is RectTransform img)) continue;
            var r = SpaceRect(img);
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
            if (grows) img.sizeDelta += new Vector2(0f, amount / Safe(ScaleInSpace(img).y));
            // Keep the at-rest layout in step with the constrained position.
            MoveInSpace(img, Vector2.down * imgMove);

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

    // World units per canvas unit, from the existing locked constraints (median).
    static float ConstraintRatio(Transform bg, StringBuilder log)
    {
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
        return k;
    }

    static float PivotY(RectTransform rt) => ToSpace(rt, new Vector2(0f, 0f)).y;

    static bool IsUnderAny(Transform t, HashSet<Transform> set)
    {
        for (; t != null; t = t.parent) if (set.Contains(t)) return true;
        return false;
    }

    // Extends the settings scroll content by amount (canvas units; negative shrinks).
    public static void GrowScroll(RectTransform section, float amount)
    {
        var scroll = section.GetComponentInParent<ScrollRect>(true);
        if (scroll == null || scroll.content == null) return;
        var vlg = scroll.content.GetComponent<VerticalLayoutGroup>();
        float local = amount / Safe(ScaleInSpace(scroll.content).y);
        if (vlg != null)
        {
            Undo.RecordObject(vlg, "Grow settings scroll");
            var p = vlg.padding;
            vlg.padding = new RectOffset(p.left, p.right, p.top, Mathf.Max(0, p.bottom + Mathf.CeilToInt(local)));
            EditorUtility.SetDirty(vlg);
        }
        else
        {
            Undo.RecordObject(scroll.content, "Grow settings scroll");
            scroll.content.sizeDelta += new Vector2(0f, local);
        }
    }

    // ---------- lookup helpers ----------

    public static T Find<T>() where T : Object => FindAll<T>().FirstOrDefault();

    public static IEnumerable<T> FindAll<T>() where T : Object =>
        Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);

    public static Transform FindAncestor(Transform t, string name)
    {
        for (; t != null; t = t.parent) if (t.name.Trim() == name) return t;
        return null;
    }

    public static string GetPath(Transform t) => t.parent == null ? t.name : GetPath(t.parent) + "/" + t.name;

    public static string Fmt(Rect r) => $"x[{r.xMin:0.#},{r.xMax:0.#}] y[{r.yMin:0.#},{r.yMax:0.#}]";
}
