using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Undo / redo for every shape change. Z = undo, Y or Shift+Z = redo, R = reset to clean base (undoable).
public class ShapeHistory : MonoBehaviour
{
    public RocketModel rocket;
    public NoseEditor editor;
    public int maxSteps = 100;

    readonly List<NoseShape> undo = new(), redo = new();
    NoseShape pending;

    public int UndoCount => undo.Count;
    public int RedoCount => redo.Count;

    void OnEnable()
    {
        if (editor == null) return;
        editor.EditStarted += BeginChange;
        editor.EditFinished += EndChange;
    }

    void OnDisable()
    {
        if (editor == null) return;
        editor.EditStarted -= BeginChange;
        editor.EditFinished -= EndChange;
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb == null || UIPanels.Typing || (editor != null && editor.IsDragging)) return;
        bool shift = kb.shiftKey.isPressed;

        if (kb.zKey.wasPressedThisFrame) { if (shift) Redo(); else Undo(); }
        if (kb.yKey.wasPressedThisFrame) Redo();
        if (kb.rKey.wasPressedThisFrame) ResetToCleanBase();
    }

    // Snapshot before a change; keep it only if the shape actually changed.
    public void BeginChange() => pending = rocket.shape.Clone();

    public void EndChange()
    {
        if (pending == null) return;
        if (!pending.SameAs(rocket.shape))
        {
            undo.Add(pending);
            if (undo.Count > maxSteps) undo.RemoveAt(0);
            redo.Clear();                       // a new edit means the old "future" is gone
        }
        pending = null;
    }

    // Use this for changes that don't come from dragging: reset now, dashboard edits and trials later.
    public void Record(System.Action change)
    {
        BeginChange();
        change();
        rocket.Rebuild();
        EndChange();
    }

    public void ResetToCleanBase() =>
        Record(() => rocket.shape = NoseShape.CleanBase(rocket.shape.points.Count));

    public void Undo()
    {
        if (undo.Count == 0) return;
        redo.Add(rocket.shape.Clone());
        Apply(Pop(undo));
    }

    public void Redo()
    {
        if (redo.Count == 0) return;
        undo.Add(rocket.shape.Clone());
        Apply(Pop(redo));
    }

    static NoseShape Pop(List<NoseShape> list)
    {
        var s = list[^1];
        list.RemoveAt(list.Count - 1);
        return s;
    }

    void Apply(NoseShape s)
    {
        rocket.shape = s.Clone();
        rocket.Rebuild();
    }
}