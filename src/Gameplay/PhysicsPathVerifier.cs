using AbyssScav.Domain;
using Godot;

namespace AbyssScav.Gameplay;

/// <summary>
/// Real-physics route verification: sweeps the actual player collision shape
/// (capsule radius 3 m, height 11 m — the domain hull diameter and the
/// SubmarineController hull) along the extraction→objective centerline through
/// the live physics space. Graph validity is not clearance: only zero blocking
/// hits here means the ship physically fits. Staggered a few samples per
/// physics frame by the caller; pure query helper otherwise.
/// </summary>
public static class PhysicsPathVerifier
{
    public const float ShipRadius = 3.0f;
    public const float ShipHeight = 11.0f;
    public const float SampleEveryMeters = 4.0f;

    public readonly record struct SweepJob(Vector3 From, Vector3 To, Vector3 Axis);

    /// <summary>Builds capsule overlap samples along a route polyline.</summary>
    public static List<SweepJob> BuildJobs(GeneratedWorld world)
    {
        var jobs = new List<SweepJob>();
        var prev = WorldBuilder.ToG(world.GetNode(world.RouteFromExtractionToObjective[0]).Position);
        for (var i = 1; i < world.RouteFromExtractionToObjective.Count; i++)
        {
            var next = WorldBuilder.ToG(world.GetNode(world.RouteFromExtractionToObjective[i]).Position);
            var leg = next - prev;
            var len = leg.Length();
            if (len < 0.5f) { prev = next; continue; }
            var dir = leg / len;
            var steps = Math.Max(2, (int)Math.Ceiling(len / SampleEveryMeters));
            for (var s = 0; s <= steps; s++)
            {
                jobs.Add(new SweepJob(prev + dir * (len * s / steps), prev, dir));
            }
            prev = next;
        }
        return jobs;
    }

    /// <summary>
    /// Runs one overlap sample. Returns true when the ship capsule fits
    /// (zero blocking hits excluding the caller's own bodies).
    /// </summary>
    public static bool SampleFits(PhysicsDirectSpaceState3D space, SweepJob job, Rid[] exclude, out Vector3 blockedPos, out string blocker)
    {
        blockedPos = default;
        blocker = "";
        using var capsule = new CapsuleShape3D { Radius = ShipRadius, Height = ShipHeight };
        var qp = new PhysicsShapeQueryParameters3D
        {
            Shape = capsule,
            Transform = new Transform3D(BasisAlong(job.Axis), job.From),
            CollisionMask = 1,
            CollideWithAreas = false,
            CollideWithBodies = true,
        };
        foreach (var r in exclude) qp.Exclude.Add(r);
        var hits = space.IntersectShape(qp, 1);
        if (hits.Count > 0)
        {
            blockedPos = job.From;
            if (hits[0].TryGetValue("collider", out var c) && c.VariantType != Variant.Type.Nil)
            {
                var node = c.As<Node>();
                blocker = node is null ? "?" : $"{node.Name}@{node.GetPath()}";
                if (node is Node3D n3) blockedPos = n3.GlobalPosition;
            }
            return false;
        }
        return true;
    }

    public static bool SampleFits(PhysicsDirectSpaceState3D space, SweepJob job, Rid[] exclude, out Vector3 blockedPos) =>
        SampleFits(space, job, exclude, out blockedPos, out _);

    /// <summary>Basis with capsule Y-axis aligned to the travel direction.</summary>
    public static Basis BasisAlong(Vector3 dir)
    {
        var y = dir.Normalized();
        var x = y.Cross(Vector3.Up);
        if (x.Length() < 0.05f) x = Vector3.Right;
        x = x.Normalized();
        var z = x.Cross(y).Normalized();
        return new Basis(x, y, z).Orthonormalized();
    }
}
