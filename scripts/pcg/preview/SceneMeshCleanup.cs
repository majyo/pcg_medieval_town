using Godot;

namespace PcgCitySandbox.Pcg;

internal static class SceneMeshCleanup
{
    // Release the managed resource references as well as the native nodes. Large
    // transient mesh buffers should not wait for an unrelated generation-2 GC.
    public static void DisposeMeshes(Node root)
    {
        foreach(var child in root.GetChildren())DisposeMeshes(child);
        if(root is MeshInstance3D mesh)mesh.Mesh?.Dispose();
        if(root is CollisionShape3D collision)collision.Shape?.Dispose();
        if(root is MultiMeshInstance3D instances){instances.Multimesh?.Mesh?.Dispose();instances.Multimesh?.Dispose();}
    }
}
