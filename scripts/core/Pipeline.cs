using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PcgCitySandbox.Core;

public enum StageStatus { Empty, Valid, Stale, Running, Failed }

/// <summary>Read access to the base map and to results of earlier stages.</summary>
public sealed class StageInputs
{
    private readonly Pipeline _pipeline;
    private readonly int _index;

    internal StageInputs(Pipeline pipeline, int index, CancellationToken cancellation)
    {
        _pipeline = pipeline;
        _index = index;
        Cancellation = cancellation;
    }

    public TemperateMapData Map => _pipeline.Map;
    public CancellationToken Cancellation { get; }

    public T Get<T>() where T : class
    {
        for (int i = _index - 1; i >= 0; i--)
        {
            if (_pipeline.Result(i) is T result)
                return result;
        }

        throw new InvalidOperationException($"缺少上游结果 {typeof(T).Name}");
    }
}

/// <summary>
/// One generation step. Run must be a pure function of the map, upstream results and options.
/// Options are copied before each run, so the UI may edit the live object at any time.
/// </summary>
public abstract class Stage
{
    public abstract string Title { get; }
    public abstract object Options { get; }
    public abstract object Run(StageInputs inputs, object options);
    public abstract object CloneOptions();
}

public abstract class Stage<TOptions, TResult> : Stage
    where TOptions : class, new()
    where TResult : class
{
    public TOptions Settings { get; } = new();
    public override object Options => Settings;

    public override object CloneOptions() => Clone(Settings);

    public override object Run(StageInputs inputs, object options) => Run(inputs, (TOptions)options);

    protected abstract TResult Run(StageInputs inputs, TOptions options);

    private static TOptions Clone(TOptions source)
    {
        var copy = new TOptions();
        foreach (var property in typeof(TOptions).GetProperties())
        {
            if (property.CanRead && property.CanWrite)
                property.SetValue(copy, property.GetValue(source));
        }

        return copy;
    }
}

/// <summary>
/// Linear pipeline: map -> stage 0 -> stage 1 -> ... Changing a stage's options invalidates it and
/// every later stage. A failed or cancelled stage publishes nothing; its downstream stays stale.
/// </summary>
public sealed class Pipeline
{
    private readonly Stage[] _stages;
    private readonly object?[] _results;
    private readonly StageStatus[] _status;
    private readonly string?[] _errors;
    private readonly double[] _seconds;
    private CancellationTokenSource? _running;

    public Pipeline(TemperateMapData map, params Stage[] stages)
    {
        Map = map;
        _stages = stages;
        _results = new object?[stages.Length];
        _status = new StageStatus[stages.Length];
        _errors = new string?[stages.Length];
        _seconds = new double[stages.Length];
    }

    public TemperateMapData Map { get; private set; }
    public IReadOnlyList<Stage> Stages => _stages;
    public bool IsRunning => _running != null;

    /// <summary>Raised on the calling thread whenever any stage status changes.</summary>
    public event Action? Changed;

    public object? Result(int index) => _status[index] == StageStatus.Valid ? _results[index] : null;
    public StageStatus Status(int index) => _status[index];
    public string? Error(int index) => _errors[index];
    public double Seconds(int index) => _seconds[index];

    public T? Get<T>() where T : class
    {
        for (int i = _stages.Length - 1; i >= 0; i--)
        {
            if (Result(i) is T result)
                return result;
        }

        return null;
    }

    public void SetMap(TemperateMapData map)
    {
        Map = map;
        Invalidate(0);
    }

    public void Invalidate(int from)
    {
        Cancel();
        for (int i = from; i < _stages.Length; i++)
        {
            if (_status[i] is StageStatus.Valid or StageStatus.Failed)
                _status[i] = StageStatus.Stale;
        }

        Changed?.Invoke();
    }

    public void Cancel() => _running?.Cancel();

    /// <summary>Runs every stage that is not valid, in order, up to and including <paramref name="last"/>.</summary>
    public async Task<bool> Run(int last = int.MaxValue)
    {
        Cancel();
        var cancellation = new CancellationTokenSource();
        _running = cancellation;
        last = Math.Min(last, _stages.Length - 1);
        try
        {
            for (int i = 0; i <= last; i++)
            {
                if (_status[i] == StageStatus.Valid)
                    continue;

                if (!await RunStage(i, cancellation.Token))
                    return false;
            }

            return true;
        }
        finally
        {
            if (_running == cancellation)
                _running = null;
            cancellation.Dispose();
        }
    }

    private async Task<bool> RunStage(int index, CancellationToken cancellation)
    {
        var stage = _stages[index];
        var options = stage.CloneOptions();
        var inputs = new StageInputs(this, index, cancellation);
        var previous = _status[index];
        _status[index] = StageStatus.Running;
        Changed?.Invoke();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await Task.Run(() => stage.Run(inputs, options), cancellation);
            cancellation.ThrowIfCancellationRequested();
            _results[index] = result;
            _status[index] = StageStatus.Valid;
            _errors[index] = null;
            _seconds[index] = timer.Elapsed.TotalSeconds;
            Changed?.Invoke();
            return true;
        }
        catch (OperationCanceledException)
        {
            _status[index] = previous == StageStatus.Empty ? StageStatus.Empty : StageStatus.Stale;
            Changed?.Invoke();
            return false;
        }
        catch (Exception ex)
        {
            _status[index] = StageStatus.Failed;
            _errors[index] = ex.Message;
            Godot.GD.PushWarning($"{stage.Title}: {ex}");
            Changed?.Invoke();
            return false;
        }
    }
}
