using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.City;

/// <summary>Whether a cell can be built on and, if not, the first reason that rules it out (in this order).</summary>
public enum SiteBlock : byte { None, Water, Flood, Bank, Slope, Forest }

public static class SiteBlocks
{
    public static string Label(this SiteBlock block) => block switch
    {
        SiteBlock.None => "可建设",
        SiteBlock.Water => "水域",
        SiteBlock.Flood => "易淹",
        SiteBlock.Bank => "河岸退让",
        SiteBlock.Slope => "坡度过大",
        _ => "森林",
    };

    /// <summary>Category names of a buildability <see cref="CategoryField"/>, indexed by <see cref="SiteBlock"/>.</summary>
    public static readonly IReadOnlyList<string> Names = Enum.GetValues<SiteBlock>().Select(b => b.Label()).ToArray();

    public static SiteBlock Block(this CategoryField buildable, int k) => (SiteBlock)buildable.Values[k];
    public static bool Buildable(this CategoryField buildable, int k) => buildable.Values[k] == (byte)SiteBlock.None;
}
