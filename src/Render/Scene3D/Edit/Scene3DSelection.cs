// brief-em3d-43 R-em3d43-2 / -3 — what can be selected in a 3D pane, in which mode. No GPU, no Avalonia.
//
// ONE VALUE SAYS WHAT IS SELECTED, whatever the mode: an object, one face of it, or one vertex of it. The
// factories normalise the fields a mode does not use, so two items that mean the same thing are EQUAL —
// the B list, the selection and the ID pass's answer are compared with ==, never field by field.
//
// A VERTEX IS NAMED BY ITS POSITION in its object, not by an index: the scene un-welds a vertex once per
// face it lies on (Scene3DBuilder), so a box corner is three scene vertices at one point, and all three are
// the same vertex to a user.

using System.Numerics;

namespace CircuitRF.Render.Scene3D.Edit;

/// <summary>What a click selects (owner decision D3: O / F / V).</summary>
public enum Scene3DSelectMode { Object, Face, Vertex }

/// <summary>One selectable thing: an object, a face of one, or a vertex of one (scene-local metres).</summary>
public readonly record struct Scene3DItem(uint Object, int Face, Vector3 Point)
{
    public static Scene3DItem OfObject(uint id) => new(id, -1, default);
    public static Scene3DItem OfFace(uint id, int face) => new(id, face, default);
    public static Scene3DItem OfVertex(uint id, Vector3 point) => new(id, -1, point);

    /// <summary>The item <paramref name="mode"/> makes of an (object, face) hit at <paramref name="point"/>.</summary>
    public static Scene3DItem In(Scene3DSelectMode mode, uint id, int face, Vector3 point) => mode switch
    {
        Scene3DSelectMode.Face   => OfFace(id, face),
        Scene3DSelectMode.Vertex => OfVertex(id, point),
        _                        => OfObject(id),
    };
}

/// <summary>One entry of a hit list: the item, how far along the ray, and where.</summary>
public readonly record struct Scene3DHit(Scene3DItem Item, float Depth, Vector3 Point);

/// <summary>The snap radius. Brief 44's snapping owns it; brief 43's vertex highlight only reads it.</summary>
public static class Scene3DSnap
{
    /// <summary>How near, in screen pixels (device-independent), a vertex must be to be a candidate — the
    /// one snap distance every editor uses (brief-em3d-44 R-em3d44-1).</summary>
    public const float RadiusPixels = GeometrySnap.RadiusPixels;
}
