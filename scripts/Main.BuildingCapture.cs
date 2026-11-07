using Godot;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
namespace PcgCitySandbox;
public partial class Main
{
    private async Task CaptureBuildings(){
        await RunSamplingAsync();await RunSettlementAsync();await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync();await RunBuildingsAsync();
        if(CurrentBuildings==null)throw new InvalidOperationException("建筑截图需要有效的建筑结果");
        var selector=(OptionButton)FindChild("PcgNodeSelector",true,false);selector.Select(BuildingStage);selector.EmitSignal(OptionButton.SignalName.ItemSelected,(long)BuildingStage);
        string dir=ProjectSettings.GlobalizePath("res://artifacts/captures");Directory.CreateDirectory(dir);
        async Task Save(string file){for(int i=0;i<6;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);using var image=GetViewport().GetTexture().GetImage();if(image.SavePng(Path.Combine(dir,file))!=Error.Ok)throw new IOException("建筑截图保存失败");GD.Print("BUILDING_CAPTURE "+file);}
        _buildingLayer.Select(1);ShowBuildings();FocusBuildings("town");await Save("buildings_overview.png");
        FocusBuildings("core");await Save("buildings_street.png");
        FocusBuildings("suburb");await Save("buildings_suburb.png");
        FocusBuildings("institution");await Save("buildings_abbey.png");
        _buildingLayer.Select(2);ShowBuildings();FocusBuildings("core");await Save("buildings_program.png");
        _buildingLayer.Select(3);ShowBuildings();_camera.TopView();var b=CurrentBuildings.Buildings.First(x=>x.ParcelId.HasValue&&x.InsideCore);var p=b.Footprint.Center;_camera.Focus=new(p.X,b.BaseY,p.Y);_camera.Size=130;_camera.UpdateTransform();await Save("buildings_access.png");
        _buildingLayer.Select(4);ShowBuildings();FocusBuildings("core");await Save("buildings_ground.png");
        GD.Print("BUILDING_CAPTURE_PASS layers=4 views=7");
    }
}
