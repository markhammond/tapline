namespace Tapline.Client;

/// <summary>
///     A replica of a watch: its members — each a table (<see cref="TableReplica" />) or a value — as of its last coherent
///     cut,
///     every member applied before the handler hears of it.
/// </summary>
public sealed class GroupReplica
{
    private readonly List<object?> _members = [];

    internal bool ResyncSent;

    /// <summary>Its members so far (by index, as the watch lists them).</summary>
    public int Count => _members.Count;

    /// <summary>Its last cut was the router's degraded form: its error follows.</summary>
    public bool Degraded { get; internal set; }

    /// <summary>The member at <paramref name="index" />, if it's a table.</summary>
    public TableReplica? TableAt(int index)
    {
        return index < _members.Count ? _members[index] as TableReplica : null;
    }

    /// <summary>The member at <paramref name="index" />, if it's a value (empty otherwise).</summary>
    public ReadOnlyMemory<byte> ValueAt(int index)
    {
        return index < _members.Count && _members[index] is byte[] value ? value : default;
    }

    internal void Clear()
    {
        _members.Clear();
    }

    internal TableReplica TableFor(int index)
    {
        Grow(index);
        if (_members[index] is not TableReplica table) _members[index] = table = new TableReplica();
        return table;
    }

    internal void SetValue(int index, byte[] value)
    {
        Grow(index);
        _members[index] = value;
    }

    private void Grow(int index)
    {
        while (_members.Count <= index) _members.Add(null);
    }
}