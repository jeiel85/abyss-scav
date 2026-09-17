using System.Numerics;
using AbyssScav.Domain;

namespace AbyssScav.Domain.Tests;

/// <summary>Shared fixtures: catalog build, world generation, and simulation creation.</summary>
internal static class DomainSetup
{
    public static ContentCatalog Catalog()
    {
        if (!ContentCatalog.TryBuild(out var catalog, out var errors) || catalog is null)
            throw new Exception("Catalog build failed: " + string.Join("; ", errors));
        return catalog;
    }

    public static GeneratedWorld World(ContentCatalog catalog, ulong seed, string biomeId, string contractId)
    {
        var request = new RunGenerationRequest(seed, biomeId, contractId, catalog);
        if (!TrenchGenerator.TryGenerate(request, out var world, out _, out var reason) || world is null)
            throw new Exception($"Generation failed (seed {seed}): {reason}");
        return world;
    }

    public static RunSimulation Sim(
        ContentCatalog catalog, GeneratedWorld world,
        string difficulty = "difficulty.standard",
        string frame = "frame.skiff",
        IEnumerable<string>? modifiers = null,
        string insurance = "insurance.basic",
        IEnumerable<string>? modules = null,
        IReadOnlyCollection<string>? owned = null)
    {
        if (!RunSimulation.TryCreate(world, catalog, difficulty, frame, modifiers, insurance, out var sim, out var reason, modules, owned) || sim is null)
            throw new Exception("Simulation creation failed: " + reason);
        return sim;
    }

    public static readonly ShipControlInput Idle = new(0f, false, false);

    public static void Teleport(RunSimulation sim, Vector3 position, float throttle = 0f)
    {
        sim.Tick(0.1f, position, new ShipControlInput(throttle, false, false));
    }

    public static void Wait(RunSimulation sim, float seconds)
    {
        var left = seconds;
        while (left > 0f)
        {
            var dt = Math.Min(0.5f, left);
            sim.Tick(dt, sim.ShipPosition, Idle);
            left -= dt;
            if (sim.Phase != RunPhase.Active) break;
        }
    }

    /// <summary>First allowed biome for a contract (keeps every flow test on valid content).</summary>
    public static string FirstBiome(ContentCatalog catalog, string contractId) =>
        catalog.Contracts[contractId].AllowedBiomeIds[0];
}
