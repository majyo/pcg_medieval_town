using Godot;
using System;
using System.IO;
using System.Threading.Tasks;
using PcgCitySandbox.Pcg;
namespace PcgCitySandbox;
public partial class Main
{
    private async Task VerifySettlementInterface(){
        void Check(bool b,string m){if(!b)throw new InvalidOperationException("SETTLEMENT_UI_FAILED: "+m);}
        var selector=(OptionButton)FindChild("PcgNodeSelector",true,false);selector.Select(1);selector.EmitSignal(OptionButton.SignalName.ItemSelected,1L);
        Check(_layoutPanel.Visible,"node selector");Check(_layoutRadius.Value==300&&_minimumArea.Value==20000&&_layoutSeed.Value==73129,$"default numeric settings: radius={_layoutRadius.Value} min={_minimumArea.Value} seed={_layoutSeed.Value}");var env=SampledEnvironment;long upstream=_sampleNode.ResultRevision;
        _layoutRun.EmitSignal(Godot.Button.SignalName.Pressed);
        int frames=0;while(_busy&&frames++<5000)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        Check(CurrentSettlement!=null&&_layoutPreview.Visible,"run callback and preview");Check(ReferenceEquals(env,SampledEnvironment)&&_sampleNode.ResultRevision==upstream,"upstream reused");
        Check(CurrentSettlement!.Region?.Districts.Count>=4&&CurrentSettlement.Region.Landscape.Count>0,"regional districts and landscape");
        long revision=_layoutNode.ResultRevision;FocusSettlement();
        string dir=ProjectSettings.GlobalizePath("res://artifacts/captures");Directory.CreateDirectory(dir);
        for(int layer=1;layer<=4;layer++){
            _layoutLayer.Select(layer);_layoutLayer.EmitSignal(OptionButton.SignalName.ItemSelected,(long)layer);Check(_layoutPreview.Layer==layer,"layer callback");
            for(int i=0;i<4;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
            using var image=GetViewport().GetTexture().GetImage();Check(image.SavePng(Path.Combine(dir,$"settlement_{layer}.png"))==Error.Ok,"capture");
        }
        var original=CurrentSettlement!;
        async Task Pick(Vector2 position,int mode){
            _layoutPick.Select(mode);original.Environment.TryHeightAt(position,out float h);
            var screen=_camera.UnprojectPosition(new Vector3(position.X,h,position.Y));
            GetViewport().PushInput(new InputEventMouseButton{Position=screen,GlobalPosition=screen,ButtonIndex=MouseButton.Left,Pressed=true},true);
            GetViewport().PushInput(new InputEventMouseButton{Position=screen,GlobalPosition=screen,ButtonIndex=MouseButton.Left,Pressed=false},true);
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        }
        await Pick(original.Entries[0].Position,3);Check(_manualEntries.Text.Length>0&&CurrentSettlement==null,$"map entry selection invalidates result: {_layoutState.Text}");
        await RunSettlementAsync();Check(CurrentSettlement?.Entries.Count==1,"authored entry rerun");
        _manualEntries.Text="";InvalidateSettlement();await RunSettlementAsync();
        await Pick(CurrentSettlement!.Core,2);Check(_manualCore.ButtonPressed&&CurrentSettlement==null,"map core selection");
        await RunSettlementAsync();Check(CurrentSettlement!=null,"manual core rerun");
        _manualCore.ButtonPressed=false;await RunSettlementAsync();_layoutPick.Select(0);revision=_layoutNode.ResultRevision;
        _layoutIntents.ButtonPressed=true;Check(_layoutPreview.Intents&&_layoutNode.ResultRevision==revision,"display does not regenerate");
        _layoutRadius.Value=320;Check(CurrentSettlement==null&&_layoutState.Text.Contains("旧结果"),"parameter invalidation");
        await RunSettlementAsync();Check(CurrentSettlement!=null,"local rerun");
        var task=RunSettlementAsync();_layoutCancel.EmitSignal(Godot.Button.SignalName.Pressed);await task;Check(_layoutNode.State==SampleNodeState.Cancelled&&CurrentSettlement==null,"cancel callback");
        _layoutRadius.Value=300;await RunSettlementAsync();_sampleSpacing.Value=20;Check(CurrentSettlement==null,"upstream parameter invalidation");
        _sampleSpacing.Value=10;await RunSamplingAsync();await RunSettlementAsync();Check(CurrentSettlement!=null,"sampling then layout");
        _wallEnabled.ButtonPressed=false;Check(CurrentSettlement==null,"wall toggle invalidates");await RunSettlementAsync();Check(CurrentSettlement?.Town?.WallFootprint.Area==0,"wall disabled explicitly");
        _wallEnabled.ButtonPressed=true;await RunSettlementAsync();Check(CurrentSettlement?.Town?.Gates.Count>0&&CurrentSettlement.Town.WallFootprint.Area>0,"wall and gate reservations restored");
        Generate();Check(CurrentSettlement==null&&_layoutState.Text.Contains("旧结果"),"map rebuild invalidation");
        await RunSamplingAsync();await RunSettlementAsync();
        _layoutIntents.ButtonPressed=false;_layoutLayer.Select(0);ShowSettlement();selector.Select(0);selector.EmitSignal(OptionButton.SignalName.ItemSelected,0L);
        GD.Print("SETTLEMENT_UI_PASS controls=ok layers=3 rerun=ok cancel=ok upstream_stale=ok map_stale=ok");
    }
}


