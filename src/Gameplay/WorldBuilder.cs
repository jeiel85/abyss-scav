using AbyssScav.Domain;
using Godot;
using SysVec = System.Numerics.Vector3;

namespace AbyssScav.Gameplay;

/// <summary>
/// Builds the physical trench from a validated <see cref="GeneratedWorld"/>.
/// Corridor walls follow every route segment faithfully using the segment's own
/// clearance radius (graph validity never implies clearance: geometry below
/// re-derives it). Scatter rock stays clear of all segments so the marked route
/// is always navigable. Limited visibility comes from fog + headlight.
/// </summary>
public static class WorldBuilder
{
    public static readonly Color InkBlue = new("#071622");
    public static readonly Color Metal = new("#29414b");
    public static readonly Color Cyan = new("#71d9d1");
    public static readonly Color Parchment = new("#dae4df");
    public static readonly Color Amber = new("#dfa44d");

    public static void Build(Node3D parent, GeneratedWorld world, ContentCatalog catalog)
    {
        var env = new WorldEnvironment();
        var e = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = InkBlue,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.10f, 0.22f, 0.26f),
            AmbientLightEnergy = 0.55f,
            FogEnabled = true,
            FogLightColor = new Color(0.03f, 0.09f, 0.11f),
            FogDensity = 0.028f,
            FogSkyAffect = 0.2f,
            TonemapMode = Godot.Environment.ToneMapper.Aces,
            GlowEnabled = true,
            GlowIntensity = 0.35f,
        };
        env.Environment = e;
        env.Name = "AbyssEnvironment";
        parent.AddChild(env);

        var terrainBody = new StaticBody3D { Name = "Terrain" };
        parent.AddChild(terrainBody);

        var wallMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.09f, 0.15f, 0.17f),
            Metallic = 0.25f,
            Roughness = 0.85f,
        };
        // Subtle depth tint shader on top of the rock material.
        var shader = LoadWaterShader();
        Material rockMat = wallMat;
        if (shader is not null)
        {
            var sm = new ShaderMaterial { Shader = shader };
            sm.SetShaderParameter("tint", new Color(0.04f, 0.10f, 0.12f));
            // Keep collision visuals honest: shader only tints, geometry is real boxes.
            rockMat = wallMat;
            _ = sm;
        }

        var lookup = world.Nodes.ToDictionary(n => n.Id);
        var trim = ComputeJunctionTrim(world);
        var volumes = new List<(Vector3 A, Vector3 B, float Clear, int Index)>();
        for (var si = 0; si < world.Segments.Count; si++)
        {
            var sg = world.Segments[si];
            if (lookup.TryGetValue(sg.FromNodeId, out var na) && lookup.TryGetValue(sg.ToNodeId, out var nb))
            {
                volumes.Add((ToG(na.Position), ToG(nb.Position), Math.Max(sg.ClearanceRadiusMeters, DomainConstants.MinClearanceMeters), si));
            }
        }
        SetClipVolumes(volumes);
        for (var si = 0; si < world.Segments.Count; si++)
        {
            var seg = world.Segments[si];
            if (!lookup.TryGetValue(seg.FromNodeId, out var a) ||
                !lookup.TryGetValue(seg.ToNodeId, out var b))
            {
                continue;
            }
            SetClipOwnIndex(si);
            BuildCorridor(terrainBody, seg.FromNodeId, seg.ToNodeId, ToG(a.Position), ToG(b.Position), seg.ClearanceRadiusMeters, trim, rockMat);
        }
        SetClipVolumes(new List<(Vector3 A, Vector3 B, float Clear, int Index)>());

        var rng = new Random((int)(world.RunSeed & 0x7fffffff) + 77);
        AddScatterRock(terrainBody, world, rockMat, rng);
        AddNodeLandmarks(parent, world);
        AddJunctionChambers(parent, world, trim);
        AddLootMarkers(parent, world);
        AddDockingMarkers(parent, world);
        AddExtractionRing(parent, world);
    }

    private static Shader? LoadWaterShader()
    {
        const string path = "res://shaders/abyss_water.gdshader";
        if (!ResourceLoader.Exists(path))
        {
            return null;
        }
        return ResourceLoader.Load<Shader>(path);
    }

    /// <summary>
    /// Per-node wall trim: the largest clearance of any incident segment.
    /// Corridor walls stop this far before each junction so one segment's walls
    /// can never protrude into another segment's traversable volume. Degree-1
    /// dead ends keep trim 0 (closed tip) since nothing crosses there.
    /// </summary>
    public static Dictionary<string, float> ComputeJunctionTrim(GeneratedWorld world)
    {
        var degree = new Dictionary<string, int>(StringComparer.Ordinal);
        var maxClear = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var n in world.Nodes)
        {
            degree[n.Id] = 0;
            maxClear[n.Id] = 0f;
        }
        foreach (var s in world.Segments)
        {
            if (degree.ContainsKey(s.FromNodeId)) degree[s.FromNodeId]++;
            if (degree.ContainsKey(s.ToNodeId)) degree[s.ToNodeId]++;
            if (maxClear.ContainsKey(s.FromNodeId))
                maxClear[s.FromNodeId] = Math.Max(maxClear[s.FromNodeId], s.ClearanceRadiusMeters);
            if (maxClear.ContainsKey(s.ToNodeId))
                maxClear[s.ToNodeId] = Math.Max(maxClear[s.ToNodeId], s.ClearanceRadiusMeters);
        }
        var trim = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var n in world.Nodes)
        {
            trim[n.Id] = degree[n.Id] > 1 ? maxClear[n.Id] : 0f;
        }
        return trim;
    }

    private static void BuildCorridor(StaticBody3D body, string fromId, string toId, Vector3 a, Vector3 b, float clearance, Dictionary<string, float> trim, Material mat)
    {
        var dir = b - a;
        var fullLength = dir.Length();
        if (fullLength < 1f)
        {
            return;
        }
        dir = dir.Normalized();
        var half = Math.Max(clearance, DomainConstants.MinClearanceMeters);
        trim.TryGetValue(fromId, out var trimA);
        trim.TryGetValue(toId, out var trimB);
        // Walls span node-center to node-center minus junction trims, so bends
        // and branches stay open. Short legs keep a minimum stub when possible.
        var start = a + dir * Math.Min(trimA, fullLength * 0.4f);
        var end = b - dir * Math.Min(trimB, fullLength * 0.4f);
        var span = end - start;
        var length = span.Length();
        if (length < 4f)
        {
            return;
        }
        var mid = (start + end) * 0.5f;
        var side = dir.Cross(Vector3.Up);
        if (side.Length() < 0.05f)
        {
            side = Vector3.Right;
        }
        side = side.Normalized();
        var up = side.Cross(dir).Normalized();

        const float thick = 6f;
        // Side walls are thin along `side` and tall along `up`: inner faces sit
        // exactly at +/-half, leaving the full navigable box open. (A previous
        // revision swapped these extents and reached 3 m past center.)
        // Every wall is clipped against all other segments' traversable volumes
        // (own segment excluded): graph validity never implied that unrelated
        // walls stay out of another corridor, so geometry re-derives it here.
        var others = _clipVolumes;
        var tag = fromId.Replace('.', '_') + "_" + toId.Replace('.', '_');
        AddClippedWall(body, tag, mid + side * (half + thick * 0.5f), dir, up, side, length, half * 2f + thick * 2f, thick, others, mat);
        AddClippedWall(body, tag, mid - side * (half + thick * 0.5f), dir, up, side, length, half * 2f + thick * 2f, thick, others, mat);
        AddClippedWall(body, tag, mid + up * (half + thick * 0.5f), dir, side, up, length, half * 2f, thick, others, mat);
        AddClippedWall(body, tag, mid - up * (half + thick * 0.5f), dir, side, up, length, half * 2f, thick, others, mat);

        // Rivet ribs every ~40 m give speed feedback and industrial read.
        var ribs = (int)(length / 40f);
        for (var i = 1; i < ribs; i++)
        {
            var p = start + dir * (length * i / ribs);
            AddRib(body, p, dir, side, up, half, mat);
        }
    }

    /// <summary>Traversable volumes of all segments for the world being built.</summary>
    private static List<(Vector3 A, Vector3 B, float Clear, int Index)> _clipVolumes = new();

    internal static void SetClipVolumes(List<(Vector3 A, Vector3 B, float Clear, int Index)> volumes) =>
        _clipVolumes = volumes;

    private static int _clipOwnIndex;

    internal static void SetClipOwnIndex(int index) => _clipOwnIndex = index;

    /// <summary>
    /// Emits a wall minus any run that would intrude into another segment's
    /// traversable volume. Samples the wall centerline every 2 m against every
    /// other segment (capsule radius = its clearance + wall half-thickness +
    /// margin) and builds the surviving runs as sub-boxes (min 6 m each).
    /// </summary>
    private static void AddClippedWall(StaticBody3D body, string tag, Vector3 center, Vector3 dir, Vector3 axisA, Vector3 axisB, float len, float wA, float wB, List<(Vector3 A, Vector3 B, float Clear, int Index)> others, Material mat)
    {
        const float step = 2f;
        const float wallHalf = 3f;
        const float margin = 0.5f;
        const float minRun = 6f;
        var n = Math.Max(2, (int)Math.Ceiling(len / step));
        var clear = new bool[n + 1];
        for (var i = 0; i <= n; i++) clear[i] = true;
        for (var i = 0; i <= n; i++)
        {
            var p = center - dir * (len * 0.5f) + dir * (len * i / n);
            foreach (var (sa, sb, sc, idx) in others)
            {
                if (idx == _clipOwnIndex) continue; // own corridor: walls belong here.
                if (DistanceToSegment(p, sa, sb) < sc + wallHalf + margin)
                {
                    clear[i] = false;
                    break;
                }
            }
        }
        var runStart = -1;
        void Flush(int endExclusive)
        {
            if (runStart < 0) return;
            var t0 = len * runStart / n - len * 0.5f;
            var t1 = len * (endExclusive - 1) / n - len * 0.5f;
            var runLen = t1 - t0;
            if (runLen >= minRun)
            {
                AddWall(body, center + dir * ((t0 + t1) * 0.5f), dir, axisA, axisB, runLen, wA, wB, mat, tag);
            }
            runStart = -1;
        }
        for (var i = 0; i <= n; i++)
        {
            if (clear[i])
            {
                if (runStart < 0) runStart = i;
            }
            else Flush(i);
        }
        Flush(n + 1);
    }

    private static void AddWall(StaticBody3D body, Vector3 center, Vector3 dir, Vector3 axisA, Vector3 axisB, float len, float wA, float wB, Material mat, string tag)
    {
        var col = new CollisionShape3D();
        var box = new BoxShape3D { Size = new Vector3(wA, wB, len) };
        col.Shape = box;
        var holder = new StaticBody3D { Name = "Wall_" + tag };
        // Build orientation: -Z along corridor.
        var basis = new Basis(axisA, axisB, -dir).Orthonormalized();
        holder.Transform = new Transform3D(basis, center);
        col.Shape = box;
        var mesh = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = box.Size },
            MaterialOverride = mat,
        };
        holder.AddChild(col);
        holder.AddChild(mesh);
        body.AddChild(holder);
    }

    private static void AddRib(StaticBody3D body, Vector3 p, Vector3 dir, Vector3 side, Vector3 up, float half, Material mat)
    {
        var holder = new StaticBody3D { Name = "Rib" };
        var basis = new Basis(side, up, -dir).Orthonormalized();
        holder.Transform = new Transform3D(basis, p);
        var col = new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(half * 2f + 2f, 1.2f, 1.2f) },
            Position = new Vector3(0f, half + 0.4f, 0f),
        };
        var mesh = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = ((BoxShape3D)col.Shape).Size },
            MaterialOverride = mat,
            Position = col.Position,
        };
        holder.AddChild(col);
        holder.AddChild(mesh);
        body.AddChild(holder);
    }

    private static void AddScatterRock(StaticBody3D body, GeneratedWorld world, Material mat, Random rng)
    {
        // Decorative mass outside the route: sample points, keep those far
        // from every segment so clearance is never violated by decoration.
        var segs = new List<(Vector3 A, Vector3 B, float C)>();
        var lookup = world.Nodes.ToDictionary(n => n.Id);
        foreach (var s in world.Segments)
        {
            if (lookup.TryGetValue(s.FromNodeId, out var a) && lookup.TryGetValue(s.ToNodeId, out var b))
            {
                segs.Add((ToG(a.Position), ToG(b.Position), s.ClearanceRadiusMeters));
            }
        }
        var placed = 0;
        for (var i = 0; i < 220 && placed < 60; i++)
        {
            var p = new Vector3(
                (float)(rng.NextDouble() * 700.0 - 350.0),
                (float)(rng.NextDouble() * 160.0 - 130.0),
                (float)(-rng.NextDouble() * 1500.0 + 60.0));
            var clear = true;
            foreach (var (sa, sb, c) in segs)
            {
                if (DistanceToSegment(p, sa, sb) < c + 14f)
                {
                    clear = false;
                    break;
                }
            }
            if (!clear)
            {
                continue;
            }
            var size = (float)(rng.NextDouble() * 22.0 + 8.0);
            var holder = new StaticBody3D { Name = "Rock", Position = p };
            holder.Rotation = new Vector3((float)rng.NextDouble(), (float)rng.NextDouble() * 6f, 0f);
            var col = new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(size, size * 0.7f, size) } };
            var mesh = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = ((BoxShape3D)col.Shape).Size },
                MaterialOverride = mat,
            };
            holder.AddChild(col);
            holder.AddChild(mesh);
            body.AddChild(holder);
            placed++;
        }
    }

    private static void AddNodeLandmarks(Node3D parent, GeneratedWorld world)
    {
        foreach (var n in world.Nodes)
        {
            var color = n.Kind switch
            {
                WorldNodeKind.Extraction => new Color(0.45f, 0.95f, 0.6f),
                WorldNodeKind.Objective => Amber,
                _ => string.IsNullOrEmpty(n.ServiceId) ? Cyan : Amber,
            };
            var mat = new StandardMaterial3D
            {
                AlbedoColor = color,
                EmissionEnabled = true,
                Emission = color,
                EmissionEnergyMultiplier = n.Kind is WorldNodeKind.Objective or WorldNodeKind.Extraction ? 1.6f : 0.7f,
            };
            var marker = new MeshInstance3D
            {
                Name = "Landmark_" + n.Id.Replace('.', '_'),
                Mesh = new SphereMesh { Radius = n.Kind == WorldNodeKind.Objective ? 3.2f : 1.6f, Height = 6.4f },
                MaterialOverride = mat,
                Position = ToG(n.Position),
            };
            parent.AddChild(marker);
        }
    }

    /// <summary>
    /// Non-colliding junction chambers mask the trimmed wall ends at multi-leg
    /// nodes. Visual only: they add zero collision, verified by the capsule sweep.
    /// </summary>
    private static void AddJunctionChambers(Node3D parent, GeneratedWorld world, Dictionary<string, float> trim)
    {
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.12f, 0.19f, 0.21f),
            Metallic = 0.4f,
            Roughness = 0.7f,
        };
        foreach (var n in world.Nodes)
        {
            if (!trim.TryGetValue(n.Id, out var t) || t <= 0.5f)
            {
                continue;
            }
            var chamber = new MeshInstance3D
            {
                Name = "Chamber_" + n.Id.Replace('.', '_'),
                Mesh = new SphereMesh { Radius = t * 0.9f, Height = t * 0.9f },
                MaterialOverride = mat,
                Position = ToG(n.Position),
            };
            chamber.Scale = new Vector3(1f, 0.5f, 1f);
            parent.AddChild(chamber);
        }
    }

    private static void AddLootMarkers(Node3D parent, GeneratedWorld world)
    {
        var mat = new StandardMaterial3D
        {
            AlbedoColor = Parchment,
            EmissionEnabled = true,
            Emission = new Color(0.55f, 0.6f, 0.55f),
            EmissionEnergyMultiplier = 0.5f,
        };
        foreach (var l in world.LootSpawns)
        {
            var m = new MeshInstance3D
            {
                Name = "Loot_" + l.SpawnId.Replace('.', '_'),
                Mesh = new BoxMesh { Size = new Vector3(2.4f, 2.4f, 2.4f) },
                MaterialOverride = mat,
                Position = ToG(l.Position),
            };
            m.Rotation = new Vector3(0.6f, 0.6f, 0f);
            parent.AddChild(m);
        }
    }

    /// <summary>
    /// Visible docking stations at objective and contract-service nodes: a small
    /// amber pad + mast offset above the node, visual only (no collision, so the
    /// physical path stays clear). Interiors are not implemented and not claimed.
    /// </summary>
    private static void AddDockingMarkers(Node3D parent, GeneratedWorld world)
    {
        var padMat = new StandardMaterial3D
        {
            AlbedoColor = Amber,
            EmissionEnabled = true,
            Emission = Amber,
            EmissionEnergyMultiplier = 1.4f,
        };
        foreach (var n in world.Nodes)
        {
            if (n.Kind != WorldNodeKind.Objective && string.IsNullOrEmpty(n.ServiceId))
                continue;
            var basePos = ToG(n.Position) + new Vector3(0f, 7f, 0f);
            var pad = new MeshInstance3D
            {
                Name = "Dock_" + n.Id.Replace('.', '_'),
                Mesh = new BoxMesh { Size = new Vector3(6f, 0.6f, 6f) },
                MaterialOverride = padMat,
                Position = basePos,
            };
            parent.AddChild(pad);
            var mast = new MeshInstance3D
            {
                Name = "DockMast_" + n.Id.Replace('.', '_'),
                Mesh = new BoxMesh { Size = new Vector3(0.8f, 6f, 0.8f) },
                MaterialOverride = padMat,
                Position = basePos + new Vector3(3.4f, 3f, 0f),
            };
            parent.AddChild(mast);
        }
    }

    private static void AddExtractionRing(Node3D parent, GeneratedWorld world)
    {
        var node = world.GetNode(world.ExtractionNodeId);
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.45f, 0.95f, 0.6f),
            EmissionEnabled = true,
            Emission = new Color(0.45f, 0.95f, 0.6f),
            EmissionEnergyMultiplier = 1.2f,
        };
        var ring = new MeshInstance3D
        {
            Name = "ExtractionRing",
            Mesh = new TorusMesh { InnerRadius = 24f, OuterRadius = 27f },
            MaterialOverride = mat,
            Position = ToG(node.Position),
        };
        parent.AddChild(ring);
    }

    private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var t = Math.Clamp((p - a).Dot(ab) / Math.Max(ab.LengthSquared(), 0.001f), 0f, 1f);
        return (a + ab * t - p).Length();
    }

    public static Vector3 ToG(SysVec v) => new(v.X, v.Y, v.Z);
    public static SysVec ToS(Vector3 v) => new(v.X, v.Y, v.Z);
}
