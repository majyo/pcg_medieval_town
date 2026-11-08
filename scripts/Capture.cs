using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using PcgCitySandbox.CameraControl;

namespace PcgCitySandbox;

/// <summary>Saves a fixed set of camera views of the current scene as PNG files.</summary>
public static class Capture
{
    /// <summary>Fixed overview and town views, then the given close-ups, then views that set up the scene themselves.</summary>
    public static async Task Save(Node host, MapCamera camera, Vector3 core,
        IEnumerable<(string Name, Vector3 Focus, float Pitch, float Yaw, float Distance)> closeups,
        IEnumerable<(string Name, Action Setup)> custom, string directory)
    {
        var path = ProjectSettings.GlobalizePath($"res://{directory}");
        Directory.CreateDirectory(path);
        var views = new List<(string Name, Action Setup)>
        {
            ("overview", camera.ResetView),
            ("top", camera.TopView),
            ("town", () => camera.Frame(core, 0.6f, -0.5f, 850)),
            ("street", () => camera.Frame(core, 0.5f, 0.9f, 250)),
            ("close", () => camera.Frame(core, 0.25f, 0.9f, 70)),
        };
        views.AddRange(closeups.Select(c => (c.Name, (Action)(() => camera.Frame(c.Focus, c.Pitch, c.Yaw, c.Distance)))));
        views.AddRange(custom);
        var viewport = host.GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(viewport, true);
        foreach (var (name, setup) in views)
        {
            setup();
            for (int i = 0; i < 4; i++)
                await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            host.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(path, name + ".png"));

            // Average render time over a few settled frames.
            double gpu = 0, cpu = 0;
            const int frames = 30;
            for (int i = 0; i < frames; i++)
            {
                await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                gpu += RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport);
                cpu += RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport);
            }

            GD.Print($"VIEW {name,-8} gpu {gpu / frames:0.00} ms  cpu {cpu / frames:0.00} ms");
        }

        GD.Print($"CAPTURED {path}");
    }
}
