using System;
using System.Threading.Tasks;

namespace PcgCitySandbox.Pcg;

public enum SampleNodeState { Empty, Running, Valid, Stale, Failed, Cancelled }

// Standalone high-level node. The graph can later invoke this same API.
public sealed class EnvironmentSampleNode
{
    public const string TypeId = "environment_sample";
    public const string InputPort = "environment_source";
    public const string OutputPort = "environment";
    public CityEnvironmentData? LastResult { get; private set; }
    public SampleNodeState State { get; private set; }
    public long ResultRevision { get; private set; }
    public string? Error { get; private set; }
    private long _invalidation;

    public void Invalidate()
    {
        _invalidation++;
        if (State != SampleNodeState.Running) State = LastResult == null ? SampleNodeState.Empty : SampleNodeState.Stale;
    }

    public bool IsCurrent(IEnvironmentSource source, EnvironmentSampleOptions options)
    {
        if (State != SampleNodeState.Valid || LastResult == null) return false;
        if (LastResult.SourceId == source.SourceId && LastResult.SourceRevision == source.Revision && LastResult.Options == options) return true;
        Invalidate(); return false;
    }

    public async Task<CityEnvironmentData> RunAsync(IEnvironmentSource source, EnvironmentSampleOptions options, PcgContext? context = null)
    {
        if (State == SampleNodeState.Running) throw new InvalidOperationException("环境采样节点已经在运行。");
        State = SampleNodeState.Running; Error = null;
        long run = ++_invalidation;
        try
        {
            var result = await EnvironmentSampler.SampleAsync(source, options, context ?? new PcgContext());
            if (run != _invalidation) throw new InvalidOperationException("运行期间输入已失效，请重新采样。");
            LastResult = result; ResultRevision++; State = SampleNodeState.Valid;
            return result;
        }
        catch (OperationCanceledException) { State = SampleNodeState.Cancelled; Error = "采样已取消"; throw; }
        catch (Exception ex) { State = SampleNodeState.Failed; Error = ex.Message; throw; }
    }
}
