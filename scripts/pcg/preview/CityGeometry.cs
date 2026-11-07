using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.Pcg;

// Per-face ownership survives batching, picking and view changes.
public sealed class CityGeometry
{
    public readonly List<Vector3> Positions = new(), Normals = new();
    public readonly List<Color> Colors = new();
    public readonly List<Vector2> Attributes = new();
    public readonly List<string> Owners = new(), Parts = new();
    public int Triangles => Positions.Count / 3;
    public void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 normal, Color color, string owner, string part, int role = 0, float edge = 1)
    {
        var cross = (b - a).Cross(c - a); if (cross.LengthSquared() < 1e-12f) return;
        if (cross.Dot(normal) > 0) (b, c) = (c, b);
        foreach (var p in new[] { a, b, c }) { Positions.Add(p); Normals.Add(normal.Normalized()); Colors.Add(color); Owners.Add(owner); Parts.Add(part); Attributes.Add(new(role, edge)); }
    }
    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n, Color color, string owner, string part, int role = 0)
    { Triangle(a, b, c, n, color, owner, part, role); Triangle(a, c, d, n, color, owner, part, role); }
    public void Append(CityGeometry other)
    { Positions.AddRange(other.Positions); Normals.AddRange(other.Normals); Colors.AddRange(other.Colors); Attributes.AddRange(other.Attributes); Owners.AddRange(other.Owners); Parts.AddRange(other.Parts); }
    public ArrayMesh ToMesh(Func<string, bool>? partFilter = null,bool lod=false,bool smooth=false)
    {
        int[] ids = Enumerable.Range(0, Positions.Count).Where(i => partFilter == null || partFilter(Parts[i])).ToArray();
        var mesh = new ArrayMesh(); if (ids.Length == 0) return mesh;
        var a = new Godot.Collections.Array(); a.Resize((int)Mesh.ArrayType.Max);
        Vector3 KeyPosition(Vector3 p)=>lod||smooth?new(MathF.Round(p.X*10000)/10000,MathF.Round(p.Y*10000)/10000,MathF.Round(p.Z*10000)/10000):p;
        var meshNormals=Normals;
        if(lod||smooth){var shared=new Dictionary<Vector3,Vector3>();foreach(int i in ids){var key=KeyPosition(Positions[i]);shared[key]=shared.GetValueOrDefault(key)+Normals[i];}meshNormals=Normals.ToList();foreach(int i in ids)meshNormals[i]=shared[KeyPosition(Positions[i])].Normalized();}
        var unique=new Dictionary<(Vector3,Vector3,Color,Vector2),int>();var source=new List<int>();var indices=new int[ids.Length];
        for(int j=0;j<ids.Length;j++){int i=ids[j];var key=(KeyPosition(Positions[i]),meshNormals[i],Colors[i],Attributes[i]);if(!unique.TryGetValue(key,out int index)){index=source.Count;unique[key]=index;source.Add(i);}indices[j]=index;}
        a[(int)Mesh.ArrayType.Vertex] = source.Select(i => Positions[i]).ToArray(); a[(int)Mesh.ArrayType.Normal] = source.Select(i => meshNormals[i]).ToArray();
        a[(int)Mesh.ArrayType.Color] = source.Select(i => Colors[i]).ToArray(); a[(int)Mesh.ArrayType.TexUV2] = source.Select(i => Attributes[i]).ToArray();a[(int)Mesh.ArrayType.Index]=indices;
        if(lod){using var importer=new ImporterMesh();importer.AddSurface(Mesh.PrimitiveType.Triangles,a);importer.GenerateLods(25,60,new Godot.Collections.Array());mesh.Dispose();return importer.GetMesh();}
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, a); return mesh;
    }
    public static CityGeometry Buildings(BuildingSet data)
    {
        var source = BuildingMeshBuilder.Build(data); var result = new CityGeometry(); var byId = data.Buildings.ToDictionary(b => b.Id);
        for (int i = 0; i < source.Positions.Count; i += 3)
        {
            var b = byId[source.Owners[i]]; var color = source.Colors[i];
            if (source.Parts[i] == "roof") color = b.PrecinctId != null ? new("626d71") : new("776d5e");
            result.Triangle(source.Positions[i], source.Positions[i + 1], source.Positions[i + 2], source.Normals[i], color, $"B{b.Id}", source.Parts[i], CityPalette.Role(b.Program));
        }
        return result;
    }
    public static CityGeometry Props(ScenePlacementSet data)
    {
        var source = SceneMeshes.Props(data); var result = new CityGeometry();
        for (int i = 0; i < source.Positions.Count; i += 3) result.Triangle(source.Positions[i], source.Positions[i + 1], source.Positions[i + 2], source.Normals[i], source.Colors[i], source.Owners[i], "prop");
        return result;
    }
}
