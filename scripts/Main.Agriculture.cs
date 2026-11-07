using Godot;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
namespace PcgCitySandbox;
public partial class Main
{
    private async Task CaptureLandscape(){
        await RunSamplingAsync();await RunSettlementAsync();await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync();
        if(CurrentParcels==null||CurrentRoads==null)throw new InvalidOperationException("田园截图需要有效生成链");
        var selector=(OptionButton)FindChild("PcgNodeSelector",true,false);selector.Select(ParcelStage);selector.EmitSignal(OptionButton.SignalName.ItemSelected,(long)ParcelStage);FocusSettlement();_parcelLayer.Select(2);ShowParcels();
        string dir=ProjectSettings.GlobalizePath("res://artifacts/captures");Directory.CreateDirectory(dir);
        async Task Save(string file){for(int i=0;i<5;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);using var image=GetViewport().GetTexture().GetImage();if(image.SavePng(Path.Combine(dir,file))!=Error.Ok)throw new IOException("田园截图保存失败");}
        await Save("agriculture_overview.png");
        var field=CurrentRoads.Landscape.Where(f=>f.Pattern=="arable").OrderByDescending(f=>f.Shape.Area).First();var center=field.Shape.Center;
        _camera.TopView();_camera.Focus=new Vector3(center.X,15,center.Y);_camera.Size=Math.Max(field.Shape.Bounds.Size.X*2.1f,field.Shape.Bounds.Size.Y*1.6f);_camera.UpdateTransform();
        await Save("agriculture_detail.png");
        var orchard=CurrentRoads.Landscape.Where(f=>f.Pattern=="orchard").OrderBy(f=>f.Shape.Center.DistanceSquaredTo(CurrentSettlement!.Core)).FirstOrDefault();
        if(orchard!=null){var neighbor=CurrentRoads.Landscape.Where(f=>f.Pattern=="arable").OrderBy(f=>f.Shape.Center.DistanceSquaredTo(orchard.Shape.Center)).First();var bounds=orchard.Shape.Bounds.Merge(neighbor.Shape.Bounds);var focus=bounds.GetCenter();_camera.Focus=new Vector3(focus.X,15,focus.Y);_camera.Size=Math.Max(bounds.Size.X*1.9f,bounds.Size.Y*1.6f);_camera.UpdateTransform();await Save("agriculture_separation.png");}
        GD.Print($"AGRICULTURE_CAPTURE_PASS groups={CurrentRoads.Landscape.Count} arable={CurrentRoads.Landscape.Count(f=>f.Pattern=="arable")}");
    }
}
