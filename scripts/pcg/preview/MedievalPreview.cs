using Godot;
using System;
using PcgCitySandbox.Pcg;
namespace PcgCitySandbox;
public partial class MedievalPreview:Node3D
{
    public void Display(SettlementLayout? layout,RoadNetwork? roads=null,bool includePrecinctFills=true,bool respectDepth=false){foreach(Node n in GetChildren()){RemoveChild(n);n.QueueFree();}Visible=layout?.Town!=null;if(!Visible)return;var town=layout!.Town!;var env=layout.Environment;
        Vector3 P(Vector2 p){env.TryHeightAt(p,out float h);return new(p.X,h+(respectDepth?.09f:3.5f),p.Y);}
        var surface=new SurfaceTool();surface.Begin(Mesh.PrimitiveType.Triangles);int vertices=0;
        void Fill(ParcelRegion region,Color color){foreach(var p in region.Triangles){surface.SetColor(color);surface.AddVertex(P(p));vertices++;}}
        var fieldLines=new ImmediateMesh();int lineVertices=0;
        void FieldLine(Vector2 a,Vector2 b,Color color){if(lineVertices==0)fieldLines.SurfaceBegin(Mesh.PrimitiveType.Lines);fieldLines.SurfaceSetColor(color);fieldLines.SurfaceAddVertex(P(a)+Vector3.Up*.2f);fieldLines.SurfaceAddVertex(P(b)+Vector3.Up*.2f);lineVertices+=2;}
        if(layout.Region is {} plan)foreach(var field in roads?.Landscape??plan.Landscape){
            Fill(field.Shape,field.Pattern switch{"orchard"=>new Color(.35f,.55f,.25f,.46f),"pasture"=>new Color(.47f,.65f,.36f,.3f),_=>new Color(.72f,.65f,.37f,.42f)});
            foreach(var ring in field.Shape.Rings)for(int i=0;i<ring.Count;i++)FieldLine(ring[i],ring[(i+1)%ring.Count],new Color(.45f,.44f,.28f,.35f));
            if(field.CultivationLines!=null)foreach(var line in field.CultivationLines)for(int i=1;i<line.Count;i++){
                // Orchard rows are dashed; open-field cultivation lines remain continuous.
                if(field.Pattern=="orchard"&&i%2==0)continue;
                FieldLine(line[i-1],line[i],field.Pattern=="orchard"?new Color(.21f,.38f,.17f,.65f):new Color(.45f,.39f,.2f,.48f));
            }
        }
        if(lineVertices>0){fieldLines.SurfaceEnd();AddChild(new MeshInstance3D{Mesh=fieldLines,CastShadow=GeometryInstance3D.ShadowCastingSetting.Off,MaterialOverride=new StandardMaterial3D{VertexColorUseAsAlbedo=true,ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,Transparency=BaseMaterial3D.TransparencyEnum.Alpha,NoDepthTest=!respectDepth,RenderPriority=13}});}
        Fill(town.WallReserve,new Color(.45f,.46f,.43f,.48f));Fill(town.WallFootprint,new Color(.75f,.72f,.64f,1));
        void Label(string text,Vector2 p,bool detail=false){var label=new Label3D{Text=text,Position=P(p)+Vector3.Up*4,FontSize=40,PixelSize=.5f,NoDepthTest=true,RenderPriority=55,OutlineRenderPriority=54,Billboard=BaseMaterial3D.BillboardModeEnum.Enabled};label.SetMeta("detail",detail);AddChild(label);}
        foreach(var gate in town.Gates){Fill(gate.Passage,new Color(.96f,.76f,.28f,.65f));Label(gate.Id,gate.Position);}
        foreach(var precinct in town.Precincts){if(includePrecinctFills)Fill(precinct.Shape,precinct.Id=="church"?new Color(.55f,.72f,.85f,.85f):new Color(.73f,.56f,.47f,.85f));Label(precinct.Name,precinct.Shape.Center+(precinct.Id=="abbey"?new Vector2(0,-90):Vector2.Zero),precinct.Id!="abbey");}
        Label("城内核心",layout.Core);
        if(layout.Region is {} region){foreach(var district in region.Districts)if(district.Role==TownDistrictRole.GateSuburb)Label(district.Name,district.Anchor);}

        if(vertices>0)AddChild(new MeshInstance3D{Mesh=surface.Commit(),CastShadow=GeometryInstance3D.ShadowCastingSetting.Off,MaterialOverride=new StandardMaterial3D{VertexColorUseAsAlbedo=true,ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,Transparency=BaseMaterial3D.TransparencyEnum.Alpha,NoDepthTest=!respectDepth,CullMode=BaseMaterial3D.CullModeEnum.Disabled,RenderPriority=12}});
    }
    public override void _Process(double delta){if(!Visible)return;var camera=GetViewport().GetCamera3D();if(camera==null)return;foreach(Node n in GetChildren())if(n is Label3D l){l.Visible=!(bool)l.GetMeta("detail")||camera.Size<1400;l.PixelSize=camera.Size/Math.Max(1,GetViewport().GetVisibleRect().Size.Y)*.4f;}}
}
