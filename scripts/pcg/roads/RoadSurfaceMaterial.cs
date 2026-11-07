using Godot;
using System;

namespace PcgCitySandbox.Pcg;

public static class RoadSurfaceMaterial
{
    // Small deterministic built-in texture array, packed albedo + roughness.
    // Replaced as one material resource without rerunning any planning node.
    public static ShaderMaterial Create()
    {
        var images=new Godot.Collections.Array<Image>();const int size=128;
        for(int layer=0;layer<5;layer++){
            var pixels=new byte[size*size*4];var baseColor=layer switch{3=>new Color("8e9186"),4=>new Color("a1a396"),2=>new Color("85745b"),_=>new Color("9c8968")};
            for(int y=0;y<size;y++)for(int x=0;x<size;x++){
                uint h;unchecked{h=(uint)(x*374761393+y*668265263+layer*73856093);h=(h^(h>>13))*1274126177;h^=h>>16;}
                float noise=(h&65535)/65535f;float value=.90f+noise*.17f;
                if(layer>=3){int width=layer==4?32:16,height=16;int row=y/height;int u=(x+(row%2)*width/2)%width,v=y%height;bool mortar=u<1||v<1;value*=mortar?.78f:1.00f;}
                else if(layer==0){float rut=Math.Min(Math.Abs(x-35),Math.Abs(x-94));value*=rut<5?.80f:1;}
                var color=baseColor*value;int i=(x+y*size)*4;pixels[i]=(byte)Math.Clamp((int)(color.R*255),0,255);pixels[i+1]=(byte)Math.Clamp((int)(color.G*255),0,255);pixels[i+2]=(byte)Math.Clamp((int)(color.B*255),0,255);pixels[i+3]=(byte)(layer>=3?210:245);
            }
            var bitmap=Image.CreateFromData(size,size,false,Image.Format.Rgba8,pixels);bitmap.GenerateMipmaps();images.Add(bitmap);
        }
        var texture=new Texture2DArray();var error=texture.CreateFromImages(images);foreach(var img in images)img.Dispose();if(error!=Error.Ok)throw new InvalidOperationException("路面纹理数组生成失败");
        var material=new ShaderMaterial{Shader=GD.Load<Shader>("res://shaders/road_surface.gdshader")};material.SetShaderParameter("pavements",texture);return material;
    }
}
