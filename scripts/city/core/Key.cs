using System;
using PcgCitySandbox.Terrain;

namespace PcgCitySandbox.City;

/// <summary>A named slot for one piece of generated data. Nodes read and write keys, never each other.</summary>
public interface IKey
{
    string Name { get; }
    string Label { get; }

    /// <summary>What the value is, in a sentence or two, for the interface's hover text.</summary>
    string Description { get; }

    Type Type { get; }
}

/// <summary>
/// Typed key. The data type is the contract between the node that writes the key and everything that reads it:
/// any node writing the same key can replace another without touching readers, the view or the checks.
/// </summary>
public sealed class Key<T> : IKey where T : class
{
    public Key(string name, string label, string description)
    {
        Name = name;
        Label = label;
        Description = description;
    }

    public string Name { get; }
    public string Label { get; }
    public string Description { get; }
    public Type Type => typeof(T);

    public override string ToString() => Name;
}

/// <summary>Data that can describe itself in one line for the stage panel and the headless checks.</summary>
public interface ISummarized
{
    string Summary { get; }
}

/// <summary>Read access to the latest valid value of every key, and the terrain they were generated from.</summary>
public interface IKeyReader
{
    ITerrain Terrain { get; }
    T? Get<T>(Key<T> key) where T : class;
}
