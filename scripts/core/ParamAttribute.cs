using System;

namespace PcgCitySandbox.Core;

/// <summary>Marks an options property as user-editable; the stage panel builds its control from this.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ParamAttribute : Attribute
{
    public ParamAttribute(string label, double min = 0, double max = 0, double step = 1)
    {
        Label = label;
        Min = min;
        Max = max;
        Step = step;
    }

    public string Label { get; }
    public double Min { get; }
    public double Max { get; }
    public double Step { get; }
}
