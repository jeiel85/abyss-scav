using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AbyssScav.Domain;

/// <summary>
/// Generation request. The host builds exactly one of these per run; the seed is
/// the only run-to-run variable, so equal seeds plus equal catalog versions
/// reproduce equal worlds (docs/04 §2).
/// </summary>
public sealed record RunGenerationRequest(
    ulong RunSeed,
    string BiomeId,
    string ContractId,
    ContentCatalog Catalog);

/// <summary>
/// A navigable world node. Positions are meters in a Y-up, run-local frame with
/// the extraction node at the origin. Depth for pressure math is derived as
/// <c>Biome.MeanDepth - Position.Y</c>; the trench floor plan lives in X/Z.
/// </summary>
public sealed record WorldNode(
    string Id,
    Vector3 Position,
    WorldNodeKind Kind,
    string ServiceId);

/// <summary>An explicit navigable leg between two nodes with a guaranteed clearance radius.</summary>
public sealed record RouteSegment(
    string Id,
    string FromNodeId,
    string ToNodeId,
    float ClearanceRadiusMeters,
    float LengthMeters);

/// <summary>A loot spawn fixed at generation time. Quest spawns carry a quest ID.</summary>
public sealed record LootSpawn(
    string SpawnId,
    string NodeId,
    Vector3 Position,
    LootKind Kind,
    int ValueCredits,
    float MassKg,
    string TraitId,
    string QuestItemId);

/// <summary>A creature spawn fixed at generation time.</summary>
public sealed record ThreatSpawn(
    string SpawnId,
    string CreatureId,
    string NodeId,
    Vector3 Position,
    bool IsApex);

/// <summary>
/// Generated trench world: the stable, Godot-ready handoff.
/// Extraction sits at the origin; the primary objective node is always
/// ≥ <see cref="DomainConstants.MinObjectiveEdgeDistance"/> graph edges away;
/// the extraction-to-objective route is an explicit node list the presentation
/// layer can draw, patrol, or validate against without re-running BFS.
/// </summary>
public sealed record GeneratedWorld(
    ulong RunSeed,
    string BiomeId,
    string ContractId,
    string LayoutHash,
    IReadOnlyList<WorldNode> Nodes,
    IReadOnlyList<RouteSegment> Segments,
    IReadOnlyList<LootSpawn> LootSpawns,
    IReadOnlyList<ThreatSpawn> ThreatSpawns,
    string ExtractionNodeId,
    string ObjectiveNodeId,
    IReadOnlyList<string> RouteFromExtractionToObjective,
    bool UsedFallback,
    int GenerationAttempts)
{
    /// <summary>Node lookup; throws <see cref="KeyNotFoundException"/> for unknown IDs.</summary>
    public WorldNode GetNode(string id) =>
        Nodes.First(n => n.Id == id);

    /// <summary>Breadth-first graph distance in edges between two nodes (-1 when disconnected).</summary>
    public int EdgeDistance(string fromId, string toId)
    {
        if (fromId == toId) return 0;
        var adjacency = BuildAdjacency();
        var visited = new Dictionary<string, int>(StringComparer.Ordinal) { [fromId] = 0 };
        var queue = new Queue<string>();
        queue.Enqueue(fromId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!adjacency.TryGetValue(current, out var next)) continue;
            foreach (var n in next)
            {
                if (visited.ContainsKey(n)) continue;
                visited[n] = visited[current] + 1;
                if (n == toId) return visited[n];
                queue.Enqueue(n);
            }
        }
        return -1;
    }

    private Dictionary<string, List<string>> BuildAdjacency()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Link(string a, string b)
        {
            if (!map.TryGetValue(a, out var la)) map[a] = la = new List<string>();
            if (!map.TryGetValue(b, out var lb)) map[b] = lb = new List<string>();
            la.Add(b);
            lb.Add(a);
        }
        foreach (var s in Segments) Link(s.FromNodeId, s.ToNodeId);
        return map;
    }

    /// <summary>
    /// Builds the run manifest for this world (docs/16 §5).
    /// INTEGRATION: send this to joiners before spawning; refuse the run when
    /// either hash mismatches the host.
    /// </summary>
    public RunManifest ToManifest(string gameVersion, string difficultyId, IEnumerable<string> modifiers) =>
        new(
            DomainConstants.ProtocolVersion,
            gameVersion,
            RunSeed,
            BiomeId,
            ContractId,
            difficultyId,
            LayoutHash,
            ManifestCatalogHash,
            modifiers.ToList());

    /// <summary>Catalog hash captured at generation time (used by <see cref="ToManifest"/>).</summary>
    public string ManifestCatalogHash { get; init; } = string.Empty;
}

/// <summary>Run manifest wire shape (docs/16 §5). JSON property names are snake_case.</summary>
public sealed record RunManifest(
    ushort ProtocolVersion,
    string GameVersion,
    ulong RunSeed,
    string BiomeId,
    string ContractId,
    string DifficultyId,
    string LayoutHash,
    string CatalogHash,
    IReadOnlyList<string> Modifiers);

/// <summary>Validation verdict for a generated world (docs/04 §6).</summary>
public sealed record GenerationValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Deterministic trench-graph generator (docs/04).
/// Separate PRNG streams per stage; per-stage sub-seed retries; safe fallback.
/// <para>
/// INTEGRATION: call <see cref="TryGenerate"/> on the host, share the returned
/// world's <see cref="GeneratedWorld.LayoutHash"/> in the manifest, and never
/// enter the InRun state when validation fails — <see cref="TryGenerate"/>
/// only returns worlds that validate, falling back to the guaranteed
/// straight-line layout after <see cref="DomainConstants.MaxStageRetries"/>
/// failed attempts.
/// </para>
/// </summary>
public static class TrenchGenerator
{
    /// <summary>
    /// Generates a validated world. Always succeeds with a usable world: after
    /// three failed full attempts it returns the deterministic safe fallback
    /// (flagged via <see cref="GeneratedWorld.UsedFallback"/>).
    /// Returns false only when the request itself is invalid (unknown biome or
    /// contract), in which case no world is produced.
    /// </summary>
    public static bool TryGenerate(
        in RunGenerationRequest request,
        out GeneratedWorld? world,
        out GenerationValidationResult validation,
        out string reason)
    {
        world = null;
        validation = new GenerationValidationResult(false, new[] { "not run" }, Array.Empty<string>());
        reason = string.Empty;

        if (request.Catalog is null) { reason = "GEN-001 generation request has no catalog."; return false; }
        if (!request.Catalog.Biomes.TryGetValue(request.BiomeId, out var biome))
        { reason = $"GEN-002 unknown biome '{request.BiomeId}'."; return false; }
        if (!request.Catalog.Contracts.TryGetValue(request.ContractId, out var contract))
        { reason = $"GEN-003 unknown contract '{request.ContractId}'."; return false; }
        if (!contract.AllowedBiomeIds.Contains(request.BiomeId))
        { reason = $"GEN-004 contract '{request.ContractId}' is not valid in biome '{request.BiomeId}'."; return false; }

        for (var attempt = 0; attempt < DomainConstants.MaxStageRetries; attempt++)
        {
            var candidate = BuildLayout(request.RunSeed, (ulong)attempt, biome, contract, request.Catalog, fallback: false);
            var verdict = Validate(candidate, request.Catalog);
            if (verdict.IsValid)
            {
                world = candidate with { UsedFallback = false, GenerationAttempts = attempt + 1 };
                validation = verdict;
                return true;
            }
        }

        var fallback = BuildLayout(request.RunSeed, 0xFBU, biome, contract, request.Catalog, fallback: true);
        var fallbackVerdict = Validate(fallback, request.Catalog);
        if (!fallbackVerdict.IsValid)
        {
            reason = "GEN-005 fallback layout failed validation: " + string.Join("; ", fallbackVerdict.Errors);
            validation = fallbackVerdict;
            return false;
        }

        world = fallback with { UsedFallback = true, GenerationAttempts = DomainConstants.MaxStageRetries + 1 };
        validation = fallbackVerdict;
        return true;
    }

    /// <summary>Re-validates a world against content rules (docs/04 §6).</summary>
    public static GenerationValidationResult Validate(GeneratedWorld world, ContentCatalog catalog)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!catalog.Biomes.TryGetValue(world.BiomeId, out var biome))
            return new GenerationValidationResult(false, new[] { $"GEN-010 unknown biome '{world.BiomeId}'." }, warnings);
        if (!catalog.Contracts.TryGetValue(world.ContractId, out var contract))
            return new GenerationValidationResult(false, new[] { $"GEN-011 unknown contract '{world.ContractId}'." }, warnings);

        var nodes = world.Nodes.ToDictionary(n => n.Id);
        if (!nodes.TryGetValue(world.ExtractionNodeId, out var extraction))
            errors.Add("GEN-012 extraction node id is missing from the node list.");
        else
        {
            if (extraction.Position.Length() > 1.0f)
                errors.Add("GEN-013 extraction node is not near the origin.");
            if (extraction.Kind != WorldNodeKind.Extraction)
                errors.Add("GEN-014 extraction node has the wrong kind.");
        }
        if (!nodes.TryGetValue(world.ObjectiveNodeId, out var objective))
            errors.Add("GEN-015 objective node id is missing from the node list.");
        else if (objective.Kind != WorldNodeKind.Objective)
            errors.Add("GEN-016 objective node has the wrong kind.");

        // Geometry: finite positions, node separation, segment clearance/length.
        foreach (var n in world.Nodes)
        {
            if (!IsFinite(n.Position))
                errors.Add($"GEN-020 node '{n.Id}' has a non-finite position.");
        }
        for (var i = 0; i < world.Nodes.Count; i++)
            for (var j = i + 1; j < world.Nodes.Count; j++)
                if (Vector3.Distance(world.Nodes[i].Position, world.Nodes[j].Position) < DomainConstants.MinNodeSeparationMeters)
                    errors.Add($"GEN-021 nodes '{world.Nodes[i].Id}' and '{world.Nodes[j].Id}' overlap.");

        foreach (var s in world.Segments)
        {
            if (!nodes.ContainsKey(s.FromNodeId) || !nodes.ContainsKey(s.ToNodeId))
            { errors.Add($"GEN-022 segment '{s.Id}' references a missing node."); continue; }
            if (s.ClearanceRadiusMeters < DomainConstants.MinClearanceMeters)
                errors.Add($"GEN-023 segment '{s.Id}' clearance {s.ClearanceRadiusMeters:F1} m is below minimum {DomainConstants.MinClearanceMeters:F1} m.");
            if (s.LengthMeters > DomainConstants.MaxSegmentLengthMeters)
                errors.Add($"GEN-024 segment '{s.Id}' length {s.LengthMeters:F0} m exceeds the maximum.");
            if (s.LengthMeters <= 0 || !float.IsFinite(s.LengthMeters))
                errors.Add($"GEN-025 segment '{s.Id}' has an invalid length.");
        }
        var narrow = world.Segments.Count(s => s.ClearanceRadiusMeters < DomainConstants.NarrowSegmentMeters);
        if (narrow > DomainConstants.MaxNarrowSegments)
            errors.Add($"GEN-026 run has {narrow} forced-narrow segments (max {DomainConstants.MaxNarrowSegments}).");

        // Connectivity: every node reachable from extraction; objective far enough.
        if (errors.Count == 0)
        {
            var dist = world.EdgeDistance(world.ExtractionNodeId, world.ObjectiveNodeId);
            if (dist < DomainConstants.MinObjectiveEdgeDistance)
                errors.Add($"GEN-030 objective is {dist} edges from extraction (min {DomainConstants.MinObjectiveEdgeDistance}).");
            foreach (var n in world.Nodes)
                if (world.EdgeDistance(world.ExtractionNodeId, n.Id) < 0)
                    errors.Add($"GEN-031 node '{n.Id}' is unreachable from extraction.");
            if (world.RouteFromExtractionToObjective.Count == 0 ||
                world.RouteFromExtractionToObjective[0] != world.ExtractionNodeId ||
                world.RouteFromExtractionToObjective[^1] != world.ObjectiveNodeId)
                errors.Add("GEN-032 extraction-to-objective route endpoints are wrong.");
        }

        // Content: required quest loot, service targets, threats, sane values.
        var questCounts = world.LootSpawns
            .Where(l => !string.IsNullOrEmpty(l.QuestItemId))
            .GroupBy(l => l.QuestItemId)
            .ToDictionary(g => g.Key, g => g.Count());
        foreach (var need in RequiredQuestSpawns(contract))
        {
            questCounts.TryGetValue(need, out var have);
            var want = need.StartsWith(QuestItems.BioSample + ".", StringComparison.Ordinal) ? 1 : RequiredCountFor(contract, need);
            if (have < want)
                errors.Add($"GEN-040 quest target '{need}' has {have} spawns (need {want}).");
        }
        foreach (var dup in questCounts.Where(kv => kv.Value > 1 && IsUniqueQuest(kv.Key)).Select(kv => kv.Key))
            errors.Add($"GEN-041 unique quest target '{dup}' is placed more than once.");

        var needServices = RequiredServiceIds(contract);
        if (needServices.Count > 0)
        {
            var haveServices = new HashSet<string>(world.Nodes.Where(n => !string.IsNullOrEmpty(n.ServiceId)).Select(n => n.ServiceId));
            foreach (var s in needServices)
                if (!haveServices.Contains(s))
                    errors.Add($"GEN-042 required service target '{s}' is missing.");
        }

        if (world.ThreatSpawns.Count == 0)
            errors.Add("GEN-043 run has no threat spawns.");
        if (contract.Id == "contract.apex_observe" && !world.ThreatSpawns.Any(t => t.IsApex))
            errors.Add("GEN-044 apex contract has no apex spawn.");

        foreach (var l in world.LootSpawns)
        {
            if (l.ValueCredits < 0) errors.Add($"GEN-045 loot '{l.SpawnId}' has negative value.");
            if (l.MassKg <= 0) errors.Add($"GEN-045 loot '{l.SpawnId}' has non-positive mass.");
            if (!string.IsNullOrEmpty(l.TraitId) && !catalog.RelicTraits.ContainsKey(l.TraitId))
                errors.Add($"GEN-046 loot '{l.SpawnId}' references unknown trait '{l.TraitId}'.");
            if (!nodes.ContainsKey(l.NodeId))
                errors.Add($"GEN-046 loot '{l.SpawnId}' references missing node '{l.NodeId}'.");
        }
        foreach (var t in world.ThreatSpawns)
        {
            if (!catalog.Creatures.TryGetValue(t.CreatureId, out var cdef))
            { errors.Add($"GEN-047 threat '{t.SpawnId}' references unknown creature '{t.CreatureId}'."); continue; }
            if (cdef.IsApex != t.IsApex)
                errors.Add($"GEN-047 threat '{t.SpawnId}' apex flag disagrees with the catalog.");
            if (!nodes.ContainsKey(t.NodeId))
                errors.Add($"GEN-047 threat '{t.SpawnId}' references missing node '{t.NodeId}'.");
        }

        // Hash integrity: recompute and compare.
        var recomputed = ComputeLayoutHash(world.RunSeed, world.BiomeId, world.ContractId, world.Nodes, world.Segments, world.LootSpawns, world.ThreatSpawns);
        if (!string.Equals(recomputed, world.LayoutHash, StringComparison.Ordinal))
            errors.Add("GEN-050 layout hash does not match world contents.");

        if (world.UsedFallback)
            warnings.Add("GEN-W01 safe fallback layout was used after retries.");

        return new GenerationValidationResult(errors.Count == 0, errors, warnings);
    }

    // ------------------------------------------------------------ construction

    private static GeneratedWorld BuildLayout(
        ulong seed, ulong attempt, BiomeDef biome, ContractDef contract, ContentCatalog catalog, bool fallback)
    {
        var terrain = new DeterministicRandom(DeterministicRandom.Derive(seed ^ (attempt * 0xD1B54A32UL), GenStreams.Terrain));
        var poi = new DeterministicRandom(DeterministicRandom.Derive(seed ^ (attempt * 0xD1B54A32UL), GenStreams.Poi));
        var lootRng = new DeterministicRandom(DeterministicRandom.Derive(seed ^ (attempt * 0xD1B54A32UL), GenStreams.Loot));
        var creatureRng = new DeterministicRandom(DeterministicRandom.Derive(seed ^ (attempt * 0xD1B54A32UL), GenStreams.Creature));
        var facilityRng = new DeterministicRandom(DeterministicRandom.Derive(seed ^ (attempt * 0xD1B54A32UL), GenStreams.Facility));

        var nodes = new List<WorldNode>();
        var edges = new List<(string From, string To, float Clearance)>();

        if (fallback)
        {
            // Guaranteed straight trench: 10 nodes, 150 m legs, generous clearance.
            WorldNode? prev = null;
            for (var i = 0; i < 10; i++)
            {
                var kind = i == 0 ? WorldNodeKind.Extraction : i == 9 ? WorldNodeKind.Objective : WorldNodeKind.Route;
                var node = new WorldNode($"node.{i:000}", new Vector3(0f, -(i * 12f), -(i * 150f)), kind, "");
                nodes.Add(node);
                if (prev is not null)
                    edges.Add((prev.Id, node.Id, Math.Max(16f, DomainConstants.MinClearanceMeters + 2f)));
                prev = node;
            }
        }
        else
        {
            // Main trench route along -Z with deterministic winding.
            var count = 9 + terrain.NextInt(0, 5);
            float x = 0f, y = 0f;
            for (var i = 0; i < count; i++)
            {
                var kind = i == 0 ? WorldNodeKind.Extraction : i == count - 1 ? WorldNodeKind.Objective : WorldNodeKind.Route;
                var pos = i == 0 ? Vector3.Zero : new Vector3(x, y, -(i * terrain.NextFloat(120f, 220f) + poi.NextFloat(0f, 40f)));
                // Keep legs bounded: recompute from the previous node so no leg exceeds the max.
                if (i > 0)
                {
                    var prev = nodes[i - 1].Position;
                    var leg = pos - prev;
                    if (leg.Length() > DomainConstants.MaxSegmentLengthMeters - 20f)
                        pos = prev + Vector3.Normalize(leg) * (DomainConstants.MaxSegmentLengthMeters - 20f);
                    if ((pos - prev).Length() < DomainConstants.MinNodeSeparationMeters + 5f)
                        pos = prev + new Vector3(0f, -10f, -60f);
                }
                var node = new WorldNode($"node.{i:000}", i == 0 ? Vector3.Zero : pos, kind, "");
                if (i > 0)
                {
                    var clearance = biome.CorridorHalfWidthMeters * terrain.NextFloat(0.85f, 1.15f);
                    edges.Add((nodes[i - 1].Id, node.Id, Math.Max(clearance, DomainConstants.MinClearanceMeters)));
                }
                nodes.Add(node);
                x = terrain.NextFloat(-1f, 1f) * biome.CorridorHalfWidthMeters * 3f;
                y = terrain.NextFloat(-30f, 10f);
            }

            // Branch routes: 2-5 dead ends of 2-4 nodes off the main route.
            var branches = 2 + poi.NextInt(0, 4);
            for (var b = 0; b < branches; b++)
            {
                var attach = poi.NextInt(1, nodes.Count - 1);
                var length = 2 + poi.NextInt(0, 3);
                var basePos = nodes[attach].Position;
                var dirX = poi.NextFloat(0.6f, 1f) * (poi.NextInt(0, 2) == 0 ? -1f : 1f);
                var prevId = nodes[attach].Id;
                for (var j = 0; j < length; j++)
                {
                    var id = $"node.b{b}.{j}";
                    var pos = basePos + new Vector3(dirX * (60f + j * poi.NextFloat(55f, 90f)), poi.NextFloat(-25f, 15f), poi.NextFloat(-70f, 70f));
                    pos = Separate(pos, nodes, poi);
                    nodes.Add(new WorldNode(id, pos, WorldNodeKind.Branch, ""));
                    edges.Add((prevId, id, Math.Max(biome.CorridorHalfWidthMeters * poi.NextFloat(0.85f, 1.1f), DomainConstants.MinClearanceMeters)));
                    prevId = id;
                }
            }
        }

        // Enforce the "max 2 forced-narrow segments" rule instead of hoping for it.
        var narrowSeen = 0;
        for (var i = 0; i < edges.Count; i++)
        {
            if (edges[i].Clearance < DomainConstants.NarrowSegmentMeters)
            {
                narrowSeen++;
                if (narrowSeen > DomainConstants.MaxNarrowSegments)
                    edges[i] = (edges[i].From, edges[i].To, DomainConstants.NarrowSegmentMeters);
            }
        }

        var routeSegments = new List<RouteSegment>();
        for (var i = 0; i < edges.Count; i++)
        {
            var (from, to, clearance) = edges[i];
            var a = nodes.First(n => n.Id == from).Position;
            var b = nodes.First(n => n.Id == to).Position;
            routeSegments.Add(new RouteSegment($"seg.{i:000}", from, to, clearance, Vector3.Distance(a, b)));
        }

        var objectiveId = nodes.First(n => n.Kind == WorldNodeKind.Objective).Id;
        var extractionId = nodes.First(n => n.Kind == WorldNodeKind.Extraction).Id;

        // Service targets (facility stream): beacons ride the route thirds, core rides the objective.
        var services = RequiredServiceIds(contract);
        if (services.Count > 0)
        {
            var routeMains = nodes.Where(n => n.Kind is WorldNodeKind.Route or WorldNodeKind.Objective).ToList();
            if (contract.Id == "contract.beacon_repair" && routeMains.Count >= 2)
            {
                var first = routeMains[routeMains.Count / 3].Id;
                var second = routeMains[(routeMains.Count * 2) / 3].Id;
                SetService(nodes, first, "service.beacon.1");
                SetService(nodes, second == first ? objectiveId : second, "service.beacon.2");
            }
            else if (contract.Id == "contract.facility_core")
            {
                SetService(nodes, objectiveId, QuestItems.CoreService);
            }
            else
            {
                for (var i = 0; i < services.Count; i++)
                    SetService(nodes, routeMains[facilityRng.PickIndex(routeMains.Count)].Id, services[i]);
            }
        }

        // Loot (loot stream): quest spawns first so validation minima always exist,
        // then normal salvage scaled by biome richness.
        var loot = new List<LootSpawn>();
        var lootIndex = 0;
        void PlaceLoot(string nodeId, LootKind kind, int value, float mass, string trait, string quest)
        {
            var anchor = nodes.First(n => n.Id == nodeId).Position;
            var pos = anchor + new Vector3(lootRng.NextFloat(-10f, 10f), lootRng.NextFloat(-6f, 6f), lootRng.NextFloat(-10f, 10f));
            loot.Add(new LootSpawn($"loot.{lootIndex++:000}", nodeId, pos, kind, value, mass, trait, quest));
        }

        var traitIds = catalog.RelicTraits.Keys.OrderBy(k => k).ToList();
        string RandomTrait() => traitIds[lootRng.PickIndex(traitIds.Count)];

        var routeIds = nodes.Where(n => n.Kind is WorldNodeKind.Route or WorldNodeKind.Branch or WorldNodeKind.Objective).Select(n => n.Id).ToList();
        switch (contract.Id)
        {
            case "contract.blackbox_recovery":
                PlaceLoot(objectiveId, LootKind.BlackBox, 400, 60f, "trait.memory_bearing", QuestItems.BlackBox);
                break;
            case "contract.facility_core":
                PlaceLoot(objectiveId, LootKind.Core, 700, 220f, "trait.ancient_power", QuestItems.Core);
                break;
            case "contract.bio_sample":
                for (var i = 0; i < 3; i++)
                {
                    var spread = routeIds[(routeIds.Count * (i + 1)) / 4];
                    PlaceLoot(spread, LootKind.BioSample, 150, 25f, "trait.parasitic", $"quest.bio.{i + 1}");
                }
                break;
            case "contract.rescue_pod":
                PlaceLoot(objectiveId, LootKind.Pod, 500, 420f, "trait.fragile", QuestItems.Pod);
                break;
        }

        var normalCount = 6 + (int)(biome.SalvageRichness * 4f) + lootRng.NextInt(0, 4);
        for (var i = 0; i < normalCount; i++)
        {
            var nodeId = routeIds[lootRng.PickIndex(routeIds.Count)];
            var roll = lootRng.NextDouble();
            if (roll < 0.45) PlaceLoot(nodeId, LootKind.Scrap, lootRng.NextInt(20, 70), lootRng.NextFloat(5f, 20f), "", "");
            else if (roll < 0.75) PlaceLoot(nodeId, LootKind.Crate, lootRng.NextInt(80, 200), lootRng.NextFloat(60f, 160f), "", "");
            else if (roll < 0.90) PlaceLoot(nodeId, LootKind.Relic, lootRng.NextInt(150, 400), lootRng.NextFloat(10f, 45f), RandomTrait(), "");
            else PlaceLoot(nodeId, LootKind.BioSample, lootRng.NextInt(60, 140), lootRng.NextFloat(8f, 30f), RandomTrait(), "");
        }

        // Threats (creature stream): scaled by biome weight; apex forced for apex contracts.
        var threats = new List<ThreatSpawn>();
        var normals = catalog.Creatures.Values.Where(c => !c.IsApex).OrderBy(c => c.Id).ToList();
        var apexes = catalog.Creatures.Values.Where(c => c.IsApex).OrderBy(c => c.Id).ToList();
        var threatIndex = 0;
        void PlaceThreat(string nodeId, CreatureDef def)
        {
            var anchor = nodes.First(n => n.Id == nodeId).Position;
            var pos = anchor + new Vector3(creatureRng.NextFloat(-40f, 40f), creatureRng.NextFloat(-15f, 15f), creatureRng.NextFloat(-40f, 40f));
            threats.Add(new ThreatSpawn($"threat.{threatIndex++:000}", def.Id, nodeId, pos, def.IsApex));
        }

        var threatCount = 2 + (int)(biome.ThreatWeight * 2f) + creatureRng.NextInt(0, 3);
        for (var i = 0; i < threatCount; i++)
            PlaceThreat(routeIds[creatureRng.PickIndex(routeIds.Count)], normals[creatureRng.PickIndex(normals.Count)]);

        if (contract.Id == "contract.apex_observe" || (!fallback && creatureRng.NextDouble() < 0.15 * biome.ThreatWeight))
        {
            var apexNode = contract.Id == "contract.apex_observe" ? objectiveId : routeIds[creatureRng.PickIndex(routeIds.Count)];
            PlaceThreat(apexNode, apexes[creatureRng.PickIndex(apexes.Count)]);
        }
        if (fallback && contract.Id == "contract.apex_observe" && !threats.Any(t => t.IsApex))
            PlaceThreat(objectiveId, apexes[0]);

        var hash = ComputeLayoutHash(seed, biome.Id, contract.Id, nodes, routeSegments, loot, threats);
        var world = new GeneratedWorld(
            seed, biome.Id, contract.Id, hash,
            nodes, routeSegments, loot, threats,
            extractionId, objectiveId,
            ComputeRoute(extractionId, objectiveId, routeSegments),
            UsedFallback: false, GenerationAttempts: 1)
        {
            ManifestCatalogHash = catalog.CatalogHash,
        };
        return world;
    }

    private static Vector3 Separate(Vector3 pos, List<WorldNode> existing, DeterministicRandom rng)
    {
        // Deterministic de-overlap: nudge branch nodes until every neighbor is at
        // least the minimum separation away (bounded tries; validation is the backstop).
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var clear = true;
            foreach (var n in existing)
            {
                if (Vector3.Distance(pos, n.Position) < DomainConstants.MinNodeSeparationMeters + 5f)
                {
                    clear = false;
                    break;
                }
            }
            if (clear) return pos;
            var flip = attempt % 2 == 0 ? 1f : -1f;
            pos += new Vector3(flip * rng.NextFloat(25f, 45f), rng.NextFloat(-8f, 8f), flip * rng.NextFloat(20f, 40f));
        }
        return pos;
    }

    private static void SetService(List<WorldNode> nodes, string nodeId, string serviceId)
    {
        var index = nodes.FindIndex(n => n.Id == nodeId);
        if (index < 0) return;
        var current = nodes[index];
        nodes[index] = current with { ServiceId = serviceId };
    }

    private static List<string> ComputeRoute(string extractionId, string objectiveId, List<RouteSegment> segments)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Link(string a, string b)
        {
            if (!adjacency.TryGetValue(a, out var la)) adjacency[a] = la = new List<string>();
            if (!adjacency.TryGetValue(b, out var lb)) adjacency[b] = lb = new List<string>();
            la.Add(b);
            lb.Add(a);
        }
        foreach (var s in segments) Link(s.FromNodeId, s.ToNodeId);

        var prev = new Dictionary<string, string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { extractionId };
        var queue = new Queue<string>();
        queue.Enqueue(extractionId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == objectiveId) break;
            if (!adjacency.TryGetValue(current, out var next)) continue;
            foreach (var n in next)
            {
                if (!visited.Add(n)) continue;
                prev[n] = current;
                queue.Enqueue(n);
            }
        }
        if (!visited.Contains(objectiveId)) return new List<string>();
        var path = new List<string> { objectiveId };
        while (path[^1] != extractionId) path.Add(prev[path[^1]]);
        path.Reverse();
        return path;
    }

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static List<string> RequiredQuestSpawns(ContractDef contract)
    {
        var needs = new List<string>();
        foreach (var o in contract.PrimaryObjectives)
        {
            if (o.Kind != ContractObjectiveKind.QuestItem) continue;
            if (o.TargetQuestItemId == QuestItems.BioSample)
            {
                for (var i = 1; i <= o.Required; i++) needs.Add($"quest.bio.{i}");
            }
            else needs.Add(o.TargetQuestItemId);
        }
        return needs;
    }

    private static int RequiredCountFor(ContractDef contract, string questId) =>
        contract.PrimaryObjectives.Where(o => o.TargetQuestItemId == questId).Sum(o => o.Required);

    private static bool IsUniqueQuest(string questId) =>
        questId is QuestItems.BlackBox or QuestItems.Core or QuestItems.Pod ||
        questId.StartsWith("quest.bio.", StringComparison.Ordinal);

    private static List<string> RequiredServiceIds(ContractDef contract)
    {
        var needs = new List<string>();
        foreach (var o in contract.PrimaryObjectives)
        {
            if (o.Kind != ContractObjectiveKind.ServiceNode) continue;
            if (o.TargetQuestItemId == QuestItems.BeaconService)
            {
                for (var i = 1; i <= o.Required; i++) needs.Add($"service.beacon.{i}");
            }
            else needs.Add(o.TargetQuestItemId);
        }
        return needs;
    }

    /// <summary>Deterministic SHA256 over canonical world contents (positions rounded to mm).</summary>
    internal static string ComputeLayoutHash(
        ulong seed,
        string biomeId,
        string contractId,
        IReadOnlyList<WorldNode> nodes,
        IReadOnlyList<RouteSegment> segments,
        IReadOnlyList<LootSpawn> loot,
        IReadOnlyList<ThreatSpawn> threats)
    {
        static string V(Vector3 v) =>
            $"{Math.Round(v.X, 3):F3},{Math.Round(v.Y, 3):F3},{Math.Round(v.Z, 3):F3}";
        var lines = new List<string>
        {
            "version=" + DomainConstants.CatalogVersion,
            $"seed={seed}",
            $"biome={biomeId}",
            $"contract={contractId}",
        };
        foreach (var n in nodes.OrderBy(x => x.Id))
            lines.Add($"node|{n.Id}|{V(n.Position)}|{n.Kind}|{n.ServiceId}");
        foreach (var s in segments.OrderBy(x => x.Id))
            lines.Add($"seg|{s.Id}|{s.FromNodeId}|{s.ToNodeId}|{s.ClearanceRadiusMeters:F3}|{s.LengthMeters:F3}");
        foreach (var l in loot.OrderBy(x => x.SpawnId))
            lines.Add($"loot|{l.SpawnId}|{l.NodeId}|{V(l.Position)}|{l.Kind}|{l.ValueCredits}|{l.MassKg:F2}|{l.TraitId}|{l.QuestItemId}");
        foreach (var t in threats.OrderBy(x => x.SpawnId))
            lines.Add($"threat|{t.SpawnId}|{t.CreatureId}|{t.NodeId}|{V(t.Position)}|{(t.IsApex ? 1 : 0)}");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
        return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }
}
