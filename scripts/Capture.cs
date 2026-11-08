using System;
using System.IO;
using System.Threading.Tasks;
using Godot;
using PcgCitySandbox.CameraControl;

namespace PcgCitySandbox;

/// <summary>Saves a fixed set of camera views of the current scene as PNG files.</summary>
public static class Capture
{
    public static async Task Save(Node host, MapCamera camera, Vector3 core, string directory)
    {
        var path = ProjectSettings.GlobalizePath($"res://{directory}");
        Directory.CreateDirectory(path);
        var views = new (string Name, Action Setup)[]
        {
            ("overview", camera.ResetView),
            ("top", camera.TopView),
            ("town", () => camera.Frame(core, 0.6f, -0.5f, 850)),
            ("street", () => camera.Frame(core, 0.5f, 0.9f, 250)),
            ("close", () => camera.Frame(core, 0.25f, 0.9f, 70)),
        };
        foreach (var (name, setup) in views)
        {
            setup();
            for (int i = 0; i < 4; i++)
                await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            host.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(path, name + ".png"));
        }

        GD.Print($"CAPTURED {path}");
    }
}
