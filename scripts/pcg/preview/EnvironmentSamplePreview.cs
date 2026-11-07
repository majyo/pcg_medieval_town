using Godot;
using System;
using PcgCitySandbox.Pcg;

namespace PcgCitySandbox;

public enum EnvironmentPreviewLayer { Hidden, Candidates, Elevation, Slope, Water, Vegetation, Restrictions }

public partial class EnvironmentSamplePreview : Node3D
{
    private CityEnvironmentData? _data;
    private MeshInstance3D _surface = null!, _lines = null!, _boundary = null!;
    private Vector3[] _positions = Array.Empty<Vector3>();
    private int[] _indices = Array.Empty<int>();
    public EnvironmentPreviewLayer Layer { get; private set; }
    public bool ShowGrid { get; private set; }
    public EnvironmentRestriction RestrictionFilter { get; private set; }

    public override void _Ready()
    {
        StandardMaterial3D Material(int priority) => new()
        {
            VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, NoDepthTest = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, RenderPriority = priority
        };
        _surface = new MeshInstance3D { Name = "SampleCells", MaterialOverride = Material(10) };
        _lines = new MeshInstance3D { Name = "SampleGrid", MaterialOverride = Material(11) };
        _boundary = new MeshInstance3D { Name = "SampleBounds", MaterialOverride = Material(12) };
        foreach (var mesh in new[] { _surface, _lines, _boundary })
        { mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off; AddChild(mesh); }
        Visible = false;
    }

    public void SetResult(CityEnvironmentData data)
    {
        _data = data;
        int count = data.CellsX * data.CellsZ;
        _positions = new Vector3[count * 4]; _indices = new int[count * 6];
        for (int z = 0; z < data.CellsZ; z++)
        for (int x = 0; x < data.CellsX; x++)
        {
            int k = z * data.CellsX + x, v = k * 4, t = k * 6;
            _positions[v] = Point(x, z); _positions[v + 1] = Point(x + 1, z);
            _positions[v + 2] = Point(x, z + 1); _positions[v + 3] = Point(x + 1, z + 1);
            _indices[t] = v; _indices[t + 1] = v + 1; _indices[t + 2] = v + 2;
            _indices[t + 3] = v + 1; _indices[t + 4] = v + 3; _indices[t + 5] = v + 2;
        }
        BuildLines(); SetLayer(Layer); SetGrid(ShowGrid);
    }

    private Vector3 Point(int x, int z)
    {
        var d = _data!; var p = d.VertexPosition(x, z);
        return new Vector3(p.X, (d.VertexHeight(x, z) ?? d.Summary.MinHeight) + 0.6f, p.Y);
    }

    public void SetLayer(EnvironmentPreviewLayer layer)
    {
        Layer = layer; Visible = _data != null && layer != EnvironmentPreviewLayer.Hidden;
        if (_data == null || layer == EnvironmentPreviewLayer.Hidden) return;
        var d = _data; var colors = new Color[_positions.Length];
        for (int z = 0; z < d.CellsZ; z++)
        for (int x = 0; x < d.CellsX; x++)
        {
            int k = z * d.CellsX + x;
            Color color = layer == EnvironmentPreviewLayer.Restrictions && RestrictionFilter != EnvironmentRestriction.None &&
                !d.Cell(x, z).Reasons.HasFlag(RestrictionFilter) ? new Color("b3c1ad") : CellColor(d, x, z, layer); color.A = 0.80f;
            for (int n = 0; n < 4; n++) colors[k * 4 + n] = color;
        }
        var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _positions; arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = _indices;
        var mesh = new ArrayMesh(); mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        Replace(_surface, mesh);
    }

    private static Color CellColor(CityEnvironmentData d, int x, int z, EnvironmentPreviewLayer layer)
    {
        var c = d.Cell(x, z); Color unknown = new("91949d");
        switch (layer)
        {
            case EnvironmentPreviewLayer.Candidates:
                return c.State switch { BuildCandidateState.Candidate => new("6fbf86"), BuildCandidateState.Blocked => new("d57468"), _ => unknown };
            case EnvironmentPreviewLayer.Elevation:
                float? h = d.VertexHeight(x, z);
                return h.HasValue ? new Color("315e83").Lerp(new Color("f0db95"), Mathf.Clamp((h.Value - d.Summary.MinHeight) / Math.Max(0.01f, d.Summary.MaxHeight - d.Summary.MinHeight), 0, 1)) : unknown;
            case EnvironmentPreviewLayer.Slope:
                return c.MaxSlope.HasValue ? new Color("6fbf86").Lerp(new Color("e8cc71"), Mathf.Clamp(c.MaxSlope.Value / 12, 0, 1))
                    .Lerp(new Color("d86962"), Mathf.Clamp((c.MaxSlope.Value - 12) / 18, 0, 1)) : unknown;
            case EnvironmentPreviewLayer.Water: return c.Water.HasValue ? c.Water.Value ? new("408dc4") : new("c8c7a2") : unknown;
            case EnvironmentPreviewLayer.Vegetation: return c.Vegetation.HasValue ? new Color("ded3a8").Lerp(new Color("366f50"), c.Vegetation.Value) : unknown;
            default:
                if (c.Reasons.HasFlag(EnvironmentRestriction.Occupied)) return new("ab78ce");
                if (c.Reasons.HasFlag(EnvironmentRestriction.NoBuildZone)) return new("ed9f5c");
                if (c.Reasons.HasFlag(EnvironmentRestriction.Water)) return new("408dc4");
                if (c.Reasons.HasFlag(EnvironmentRestriction.TooSteep)) return new("d86962");
                return c.State == BuildCandidateState.Unknown ? unknown : new("6fbf86");
        }
    }

    public void SetGrid(bool visible) { ShowGrid = visible; _lines.Visible = visible; }
    public void SetRestrictionFilter(EnvironmentRestriction filter) { RestrictionFilter = filter; SetLayer(Layer); }

    private void BuildLines()
    {
        var d = _data!;
        using var grid = new SurfaceTool(); using var border = new SurfaceTool();
        grid.Begin(Mesh.PrimitiveType.Lines); border.Begin(Mesh.PrimitiveType.Lines);
        void Edge(int ax, int az, int bx, int bz, bool outer)
        {
            var st = outer ? border : grid;
            st.SetColor(outer ? new Color(1, 0.85f, 0.4f, 1) : new Color(0.09f, 0.16f, 0.13f, 0.28f));
            st.AddVertex(Point(ax, az)); st.AddVertex(Point(bx, bz));
        }
        for (int z = 0; z <= d.CellsZ; z++) for (int x = 0; x < d.CellsX; x++) Edge(x, z, x + 1, z, z == 0 || z == d.CellsZ);
        for (int x = 0; x <= d.CellsX; x++) for (int z = 0; z < d.CellsZ; z++) Edge(x, z, x, z + 1, x == 0 || x == d.CellsX);
        // A one-cell region has only boundary segments.
        Replace(_lines, d.CellsX == 1 && d.CellsZ == 1 ? null : grid.Commit()); Replace(_boundary, border.Commit());
    }
    private static void Replace(MeshInstance3D target, Mesh? mesh) { var old = target.Mesh; target.Mesh = mesh; old?.Dispose(); }
}
