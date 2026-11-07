using Godot;
using Clipper2Lib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.Pcg;

public static class CityGroundMesh
{
    public const float Lift = .025f;
    // Clip against the actual native terrain triangles, not the coarser planning grid.
    public static CityGeometry Build(TemperateMapData map, IEnumerable<(string Id, string Kind, ParcelRegion Shape)> patches, ITerrainSurface? terrain = null,float precision=0,float tolerance=.0015f)
    {
        terrain ??= new NativeTerrainSurface(map); var result = new CityGeometry(); var refined = terrain is GradedTerrain g ? g.ModifiedNativeCells(map) : new HashSet<int>();
        var heights=new Dictionary<Vector2,float>();
        bool trace=OS.GetCmdlineUserArgs().Contains("--trace-ground");
        float Height(Vector2 p){if(heights.TryGetValue(p,out float h))return h;h=terrain.HeightAt(p);if(heights.Count<2000000)heights[p]=h;return h;}
        foreach (var patch in patches)
        {
            Color color = CityPalette.Ground(patch.Kind); var paths = patch.Shape.CopyPaths(); var bounds = patch.Shape.Bounds;
            void Refine(Vector3 a,Vector3 b,Vector3 c,int depth)
            {
                Vector3 Surface(Vector3 p)=>new(p.X,Height(new(p.X,p.Z))+Lift,p.Z);
                var ab=Surface((a+b)/2);var bc=Surface((b+c)/2);var ca=Surface((c+a)/2);var center=(a+b+c)/3;
                float error=Math.Max(Math.Abs(ab.Y-(a.Y+b.Y)/2),Math.Max(Math.Abs(bc.Y-(b.Y+c.Y)/2),Math.Max(Math.Abs(ca.Y-(c.Y+a.Y)/2),Math.Abs(Surface(center).Y-center.Y))));
                if(trace&&depth==7&&error>.003f){
                    string Explain(Vector3 v){var p=new Vector2(v.X,v.Z);return $"{v} actual={Height(p)+Lift} locked={(terrain as GradedTerrain)?.IsLocked(p)} kind={(terrain as GradedTerrain)?.KindAt(p)} road={(terrain as GradedTerrain)?.Design.SurfaceAt(p)?.RoadId}";}
                    throw new InvalidOperationException($"GROUND_DIAGNOSTIC {patch.Id} {patch.Kind} error={error} A={Explain(a)} B={Explain(b)} C={Explain(c)} MID={Explain(center)}");}
                if(error>tolerance&&depth<7){Refine(a,ab,ca,depth+1);Refine(ab,b,bc,depth+1);Refine(ca,bc,c,depth+1);Refine(ab,bc,ca,depth+1);return;}
                var n=(c-a).Cross(b-a).Normalized();if(n.Y<0)n=-n;
                result.Triangle(a,b,c,n,color,patch.Id,"ground",patch.Kind is "主路" or "街巷" or "路肩"?5:patch.Kind is "后园" or "草地" or "果园"?7:6);
            }
            int X(float x) => Math.Clamp((int)MathF.Floor((x + map.Half) / map.Spacing), 0, map.Side - 2);
            int startX = X(bounds.Position.X), endX = X(bounds.End.X), startZ = X(bounds.Position.Y), endZ = X(bounds.End.Y);
            // A road shoulder can span the entire map. Clip it to coarse tiles first,
            // so each native cell only tests a small local contour, not the whole road network.
            for (int tileZ = startZ / 24 * 24; tileZ <= endZ; tileZ += 24)
            for (int tileX = startX / 24 * 24; tileX <= endX; tileX += 24)
            {
                int tileEndX = Math.Min(map.Side - 2, tileX + 23), tileEndZ = Math.Min(map.Side - 2, tileZ + 23);
                var tileA = map.GridPosition(tileX, tileZ); var tileB = map.GridPosition(tileEndX + 1, tileEndZ + 1);
                var local = ParcelGeometry.Intersection(paths, ParcelGeometry.Rect(new Rect2(tileA, tileB - tileA)));
                if (ParcelGeometry.Area(local) < .000001) continue;
                float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
                foreach (var ring in local) foreach (var point in ring) { var p = ParcelGeometry.V(point); minX = Math.Min(minX, p.X); minZ = Math.Min(minZ, p.Y); maxX = Math.Max(maxX, p.X); maxZ = Math.Max(maxZ, p.Y); }
                for (int z = Math.Max(tileZ, X(minZ)); z <= Math.Min(tileEndZ, X(maxZ)); z++)
                for (int x = Math.Max(tileX, X(minX)); x <= Math.Min(tileEndX, X(maxX)); x++)
                {
                int divisions = refined.Contains(z*(map.Side-1)+x) ? Math.Max(1,(int)Math.Ceiling(map.Spacing / (precision>0?precision:terrain.Spacing))) : 1;
                for(int iz=0;iz<divisions;iz++)for(int ix=0;ix<divisions;ix++){
                var a = map.GridPosition(x, z)+new Vector2(ix,iz)*map.Spacing/divisions; var d = a+Vector2.One*map.Spacing/divisions; var b = new Vector2(d.X, a.Y); var c = new Vector2(a.X, d.Y);
                foreach (var tri in new[] { new[] { a, b, c }, new[] { b, d, c } })
                {
                    var clipped = ParcelGeometry.Intersection(local, new Paths64 { ParcelGeometry.Path(tri) });
                    if (ParcelGeometry.Area(clipped) < .000001) continue;
                    var shape = new ParcelRegion(clipped);
                    Vector3 P(Vector2 p) => new(p.X, Height(p) + Lift, p.Y);
                    for (int i = 0; i < shape.Triangles.Count; i += 3)
                    {
                        var p = P(shape.Triangles[i]); var q = P(shape.Triangles[i + 1]); var r = P(shape.Triangles[i + 2]);
                        if(terrain is GradedTerrain){Refine(p,q,r,0);continue;}
                        var n = (r - p).Cross(q - p).Normalized(); if (n.Y < 0) n = -n;
                        int first = result.Positions.Count;
                        result.Triangle(p, q, r, n, color, patch.Id, "ground", patch.Kind is "主路" or "街巷" or "路肩" ? 5 : patch.Kind is "后园" or "草地" or "果园" ? 7 : 6);
                        for (int j = first; terrain is not GradedTerrain && j < result.Positions.Count; j++)
                        {
                            var v = result.Positions[j]; float u = Math.Clamp((v.X - a.X) / map.Spacing, 0, 1), w = Math.Clamp((v.Z - a.Y) / map.Spacing, 0, 1);
                            var na = map.Normals[map.Index(x, z)]; var nb = map.Normals[map.Index(x + 1, z)]; var nc = map.Normals[map.Index(x, z + 1)]; var nd = map.Normals[map.Index(x + 1, z + 1)];
                            result.Normals[j] = (u + w <= 1 ? na + (nb - na) * u + (nc - na) * w : nd + (nc - nd) * (1 - u) + (nb - nd) * (1 - w)).Normalized();
                        }
                    }
                }
                }
            }
            }
        }
        return result;
    }
}
