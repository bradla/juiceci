namespace Tessera.App.ViewModels;

/// <summary>
/// Undo/redo over whole-document snapshots. Edits that arrive close together (a drag, typing
/// a title) are merged into one step so a single undo reverses the whole gesture.
/// </summary>
public sealed class UndoHistory
{
    const int Max = 100;
    readonly LinkedList<string> _undo = new();
    readonly Stack<string> _redo = new();
    string _current = "";
    DateTime _lastEdit = DateTime.MinValue;

    public TimeSpan MergeWindow { get; set; } = TimeSpan.FromMilliseconds(600);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Forgets all history; <paramref name="snapshot"/> becomes the starting state.</summary>
    public void Reset(string snapshot)
    {
        _undo.Clear();
        _redo.Clear();
        _current = snapshot;
        _lastEdit = DateTime.MinValue;
    }

    /// <summary>Records the state after an edit.</summary>
    public void Record(string snapshot)
    {
        if (snapshot == _current) return;
        var now = DateTime.UtcNow;
        if (now - _lastEdit > MergeWindow)
        {
            _undo.AddLast(_current);
            if (_undo.Count > Max) _undo.RemoveFirst();
        }
        _redo.Clear();
        _current = snapshot;
        _lastEdit = now;
    }

    /// <summary>Returns the state to restore, or null if there's nothing to undo.</summary>
    public string? Undo()
    {
        if (_undo.Last is not { } last) return null;
        _undo.RemoveLast();
        _redo.Push(_current);
        _current = last.Value;
        _lastEdit = DateTime.MinValue;
        return _current;
    }

    public string? Redo()
    {
        if (!_redo.TryPop(out var next)) return null;
        _undo.AddLast(_current);
        _current = next;
        _lastEdit = DateTime.MinValue;
        return _current;
    }
}
