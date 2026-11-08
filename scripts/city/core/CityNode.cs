using System;
using System.Collections.Generic;
using System.Threading;
using PcgCitySandbox.Terrain;

namespace PcgCitySandbox.City;

/// <summary>
/// One generation step: a single decision that can be tuned or replaced on its own. It declares which keys it
/// reads, writes fresh, or edits (reads the upstream version and replaces it); the graph wires nodes from these
/// declarations. Run must be a pure function of the terrain, the values it reads and its options.
/// </summary>
public abstract class CityNode
{
    private static readonly IKey[] None = Array.Empty<IKey>();

    /// <summary>Panel group heading, e.g. "路网".</summary>
    public abstract string Group { get; }
    public abstract string Title { get; }
    public virtual IReadOnlyList<IKey> Reads => None;
    public virtual IReadOnlyList<IKey> Writes => None;
    public virtual IReadOnlyList<IKey> Edits => None;
    public abstract object Options { get; }
    public abstract object CloneOptions();
    public abstract void Run(NodeContext context, object options);
}

public abstract class CityNode<TOptions> : CityNode where TOptions : class, new()
{
    public TOptions Settings { get; } = new();
    public override object Options => Settings;

    public override object CloneOptions()
    {
        var copy = new TOptions();
        foreach (var property in typeof(TOptions).GetProperties())
        {
            if (property.CanRead && property.CanWrite)
                property.SetValue(copy, property.GetValue(Settings));
        }

        return copy;
    }

    public override void Run(NodeContext context, object options) => Run(context, (TOptions)options);

    protected abstract void Run(NodeContext context, TOptions options);
}

/// <summary>
/// What a running node sees: the terrain, the upstream values it declared, and a place to put its outputs.
/// Values read here are shared with other nodes and the cache, so a node never mutates them; an edit copies
/// the upstream value and sets the copy.
/// </summary>
public sealed class NodeContext
{
    private readonly CityNode _node;
    private readonly Func<IKey, object> _upstream;
    private readonly Dictionary<IKey, object> _outputs = new();
    private readonly List<string> _notices = new();

    internal NodeContext(CityNode node, ITerrain terrain, Func<IKey, object> upstream, CancellationToken cancellation)
    {
        _node = node;
        _upstream = upstream;
        Terrain = terrain;
        Cancellation = cancellation;
    }

    public ITerrain Terrain { get; }
    public CancellationToken Cancellation { get; }

    internal IReadOnlyDictionary<IKey, object> Outputs => _outputs;
    internal IReadOnlyList<string> Notices => _notices;

    public T Get<T>(Key<T> key) where T : class
    {
        if (!Declared(_node.Reads, key) && !Declared(_node.Edits, key))
            throw new InvalidOperationException($"{_node.Title} 读取了未声明的 {key.Name}");
        return (T)_upstream(key);
    }

    public void Set<T>(Key<T> key, T value) where T : class
    {
        if (!Declared(_node.Writes, key) && !Declared(_node.Edits, key))
            throw new InvalidOperationException($"{_node.Title} 写入了未声明的 {key.Name}");
        _outputs[key] = value ?? throw new ArgumentNullException(nameof(value));
    }

    public void Notice(string message) => _notices.Add(message);

    private static bool Declared(IReadOnlyList<IKey> keys, IKey key)
    {
        foreach (var k in keys)
        {
            if (ReferenceEquals(k, key))
                return true;
        }

        return false;
    }
}

/// <summary>Options type for nodes without tunable parameters.</summary>
public sealed class NoOptions
{
}
