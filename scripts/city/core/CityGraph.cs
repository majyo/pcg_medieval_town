using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PcgCitySandbox.Terrain;

namespace PcgCitySandbox.City;

public enum NodeStatus { Empty, Valid, Stale, Running, Failed, Blocked }

/// <summary>
/// Nodes in a list whose order is the run order; the wiring follows from what each node reads, writes and edits.
/// A key read by node i comes from the nearest earlier node that writes or edits it. Only the first writer of a
/// key may write it fresh; later ones must edit. Changing a node's options invalidates the node and the nodes that
/// depend on it, directly or indirectly. A failed or cancelled node publishes nothing; its dependents stay stale.
/// </summary>
public sealed class CityGraph : IKeyReader
{
    private readonly CityNode[] _nodes;
    private readonly Dictionary<IKey, int>[] _sources;
    private readonly List<int>[] _dependents;
    private readonly Dictionary<IKey, int> _lastWriter = new();
    private readonly IReadOnlyDictionary<IKey, object>?[] _outputs;
    private readonly IReadOnlyList<string>[] _notices;
    private readonly NodeStatus[] _status;
    private readonly string?[] _errors;
    private readonly double[] _seconds;
    private readonly string[] _summaries;
    private CancellationTokenSource? _running;

    public CityGraph(ITerrain terrain, params CityNode[] nodes)
    {
        Terrain = terrain;
        _nodes = nodes;
        int n = nodes.Length;
        _sources = new Dictionary<IKey, int>[n];
        _dependents = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
        _outputs = new IReadOnlyDictionary<IKey, object>?[n];
        _notices = Enumerable.Range(0, n).Select(_ => (IReadOnlyList<string>)Array.Empty<string>()).ToArray();
        _status = new NodeStatus[n];
        _errors = new string?[n];
        _seconds = new double[n];
        _summaries = new string[n];
        Wire();
    }

    public ITerrain Terrain { get; private set; }
    public IReadOnlyList<CityNode> Nodes => _nodes;
    public bool IsRunning => _running != null;

    /// <summary>Raised on the calling thread whenever any node status changes.</summary>
    public event Action? Changed;

    public NodeStatus Status(int index) => _status[index];
    public string? Error(int index) => _errors[index];
    public double Seconds(int index) => _seconds[index];
    public IReadOnlyList<string> Notices(int index) => _status[index] == NodeStatus.Valid ? _notices[index] : Array.Empty<string>();

    /// <summary>One line per written value that can summarize itself.</summary>
    public string Summary(int index)
    {
        if (_status[index] != NodeStatus.Valid || _outputs[index] == null)
            return "";
        return string.Join("；", _outputs[index]!.Values.OfType<ISummarized>().Select(s => s.Summary));
    }

    /// <summary>Summary as it was when the node finished; differs from <see cref="Summary"/> only if a stored value was mutated.</summary>
    public string SummaryWhenRun(int index) => _status[index] == NodeStatus.Valid ? _summaries[index] : "";

    public bool Has(IKey key) => _lastWriter.TryGetValue(key, out int i) && Value(i, key) != null;

    /// <summary>The final version of a key, or null while the node that last writes it is not valid.</summary>
    public T? Get<T>(Key<T> key) where T : class =>
        _lastWriter.TryGetValue(key, out int i) ? Value(i, key) as T : null;

    /// <summary>The node each declared input of node <paramref name="index"/> comes from.</summary>
    public IReadOnlyDictionary<IKey, int> Sources(int index) => _sources[index];

    public void SetTerrain(ITerrain terrain)
    {
        Terrain = terrain;
        Cancel();
        for (int i = 0; i < _nodes.Length; i++)
            MarkStale(i);
        Changed?.Invoke();
    }

    /// <summary>Marks the node and everything downstream of it stale.</summary>
    public void Invalidate(int index)
    {
        Cancel();
        var queue = new Queue<int>();
        queue.Enqueue(index);
        var seen = new HashSet<int> { index };
        while (queue.TryDequeue(out int i))
        {
            MarkStale(i);
            foreach (int d in _dependents[i])
            {
                if (seen.Add(d))
                    queue.Enqueue(d);
            }
        }

        Changed?.Invoke();
    }

    public void Cancel() => _running?.Cancel();

    /// <summary>
    /// Runs every node that is not valid, in list order. A node whose inputs are not all valid is skipped (marked
    /// blocked); independent nodes still run. Returns true when every node ends valid.
    /// </summary>
    public async Task<bool> Run()
    {
        Cancel();
        var cancellation = new CancellationTokenSource();
        _running = cancellation;
        try
        {
            for (int i = 0; i < _nodes.Length; i++)
            {
                if (_status[i] == NodeStatus.Valid)
                    continue;
                if (_sources[i].Values.Any(s => _status[s] != NodeStatus.Valid))
                {
                    _status[i] = NodeStatus.Blocked;
                    Changed?.Invoke();
                    continue;
                }

                if (!await RunNode(i, cancellation.Token) && cancellation.IsCancellationRequested)
                    return false;
            }

            return _status.All(s => s == NodeStatus.Valid);
        }
        finally
        {
            if (_running == cancellation)
                _running = null;
            cancellation.Dispose();
        }
    }

    private void MarkStale(int i)
    {
        if (_status[i] is NodeStatus.Valid or NodeStatus.Failed or NodeStatus.Blocked)
            _status[i] = NodeStatus.Stale;
    }

    private object? Value(int node, IKey key) =>
        _status[node] == NodeStatus.Valid && _outputs[node] != null && _outputs[node]!.TryGetValue(key, out var v) ? v : null;

    private void Wire()
    {
        var names = new Dictionary<string, IKey>();
        var writer = new Dictionary<IKey, int>();
        for (int i = 0; i < _nodes.Length; i++)
        {
            var node = _nodes[i];
            foreach (var key in node.Reads.Concat(node.Writes).Concat(node.Edits))
            {
                if (names.TryGetValue(key.Name, out var other) && !ReferenceEquals(other, key))
                    throw new InvalidOperationException($"两个不同的键同名：{key.Name}");
                names[key.Name] = key;
            }

            _sources[i] = new Dictionary<IKey, int>();
            foreach (var key in node.Reads.Concat(node.Edits))
            {
                if (!writer.TryGetValue(key, out int source))
                    throw new InvalidOperationException($"{node.Title} 需要 {key.Name}，但前面没有节点写入它");
                _sources[i][key] = source;
                if (!_dependents[source].Contains(i))
                    _dependents[source].Add(i);
            }

            foreach (var key in node.Writes)
            {
                if (writer.TryGetValue(key, out int first))
                    throw new InvalidOperationException($"{node.Title} 与 {_nodes[first].Title} 都写入 {key.Name}；后者应声明为编辑");
                writer[key] = i;
            }

            foreach (var key in node.Edits)
                writer[key] = i;
        }

        foreach (var (key, i) in writer)
            _lastWriter[key] = i;
    }

    private async Task<bool> RunNode(int index, CancellationToken cancellation)
    {
        var node = _nodes[index];
        var options = node.CloneOptions();
        var sources = _sources[index];
        var context = new NodeContext(node, Terrain, key => Value(sources[key], key)!, cancellation);
        var previous = _status[index];
        _status[index] = NodeStatus.Running;
        Changed?.Invoke();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Task.Run(() => node.Run(context, options), cancellation);
            cancellation.ThrowIfCancellationRequested();
            var missing = node.Writes.Concat(node.Edits).Where(k => !context.Outputs.ContainsKey(k)).Select(k => k.Name).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException($"没有写入 {string.Join("、", missing)}");
            _outputs[index] = context.Outputs;
            _notices[index] = context.Notices;
            _status[index] = NodeStatus.Valid;
            _summaries[index] = Summary(index);
            _errors[index] = null;
            _seconds[index] = timer.Elapsed.TotalSeconds;
            Changed?.Invoke();
            return true;
        }
        catch (OperationCanceledException)
        {
            _status[index] = previous == NodeStatus.Empty ? NodeStatus.Empty : NodeStatus.Stale;
            Changed?.Invoke();
            return false;
        }
        catch (Exception ex)
        {
            _status[index] = NodeStatus.Failed;
            _errors[index] = ex.Message;
            Godot.GD.PushWarning($"{node.Title}: {ex}");
            Changed?.Invoke();
            return false;
        }
    }
}
