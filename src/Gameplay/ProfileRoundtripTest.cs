using AbyssScav.App;
using AbyssScav.Domain;
using AbyssScav.Foundation;
using AbyssScav.Persistence;
using AbyssScav.Presentation;
using AbyssScav.Presentation.Menus;
using Godot;

namespace AbyssScav.Gameplay;

/// <summary>
/// Debug/test-only headless scene: persistence-to-menu roundtrip on an ISOLATED
/// temp user-data root. Never touches the real user profile dir: builds a unique
/// AppPaths root under the OS temp dir, injects it into the GameServices
/// registry, then uses the SAME production read path as the game
/// (FileSaveStore over GameServices.Paths.SavesDir + real main_menu.tscn
/// LoadProfileAsync/RefreshProfileLabel). Applies one settlement, asserts it
/// applies exactly once, then asserts the visible menu label reflects the
/// persisted profile. Persistence -&gt; menu only; not a RunController E2E.
/// Run headless:
/// Godot --headless --path . res://scenes/profile_roundtrip_test.tscn
/// Release builds refuse to run this scene.
/// </summary>
public partial class ProfileRoundtripTest : Node
{
    private const string TempPrefix = "abyss-roundtrip-";
    private const ulong LabelTimeoutMs = 10_000;

    private readonly List<string> _log = new();
    private bool _ok = true;
    private bool _finished;
    private bool _waiting;
    private ulong _deadlineMsec;
    private string _wantLabel = "";
    private string _settlementId = "";
    private long _creditsBefore;
    private long _runsBefore;
    private long _creditsExpected;
    private long _runsExpected;
    private MainMenu? _menu;
    private AppPaths? _paths;
    private SessionLockService? _sessionLock;
    private LocalLogger? _logger;
    private string _tempRoot = "";

    private void Check(bool cond, string name, string detail = "")
    {
        _log.Add((cond ? "SMOKE PASS " : "SMOKE FAIL ") + name + (detail == "" ? "" : " | " + detail));
        if (!cond) _ok = false;
    }

    public override void _Ready()
    {
        if (!OS.IsDebugBuild())
        {
            GD.Print("SMOKE FAIL test.scene | release build refuses the debug test scene.");
            GetTree()?.CallDeferred(SceneTree.MethodName.Quit, 1);
            return;
        }
        AbyssInput.EnsureRegistered();
        Check(true, "test.scene", "debug build accepted");
        RunAsync();
    }

    public override void _Process(double delta)
    {
        if (_finished || !_waiting || _menu is null || !IsInstanceValid(_menu)) return;
        var label = FindLabel(_menu);
        if (label is not null && label.Text == _wantLabel && _wantLabel != "")
        {
            _waiting = false;
            VerifyAndFinishAsync();
            return;
        }
        if (Time.GetTicksMsec() >= _deadlineMsec)
        {
            _waiting = false;
            var seen = label is null ? "no profile label found" : label.Text;
            Check(false, "menu.label_timeout", $"visible label did not reach '{_wantLabel}' within {LabelTimeoutMs}ms | seen='{seen}'");
            Finish();
        }
    }

    private async void RunAsync()
    {
        try
        {
            if (GameServices.IsInitialized)
            {
                Check(false, "services.not_contaminated", "host services already initialized; refusing to run on a live profile.");
                Finish();
                return;
            }

            _tempRoot = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N"));
            _paths = AppPaths.FromRoot(_tempRoot);
            _paths.EnsureDirectories();
            Check(true, "services.isolated", _tempRoot);

            _sessionLock = new SessionLockService(_paths);
            _sessionLock.Acquire();
            _logger = new LocalLogger(_paths.LogsDir, _sessionLock.SessionId, GameVersion.Current);
            var registry = new DependencyRegistry();
            registry.RegisterInstance(_paths);
            registry.RegisterInstance(_logger);
            registry.RegisterInstance(_sessionLock);
            registry.RegisterInstance(new SceneFlow());
            registry.RegisterInstance(new AppSettingsHolder(AppSettings.Default(), false, false));
            registry.RegisterInstance(new ProfileSessionState());
            GameServices.Initialize(registry);

            var store = new FileSaveStore(GameServices.Paths.SavesDir);
            var before = await store.LoadAsync(CancellationToken.None);
            _creditsBefore = before.Currencies.Credits;
            _runsBefore = before.Stats.RunsCompleted;

            _settlementId = $"settle.test.{DateTime.UtcNow:yyyyMMdd_HHmmss}.{Guid.NewGuid():N}".ToLowerInvariant();
            var payload = new SettlementPayload(_settlementId, 1, 0, 0,
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), 1, 0);
            var first = await store.ApplySettlementAsync(payload, CancellationToken.None);
            Check(first.Outcome == SettlementApplyOutcome.Applied, "profile.applied_once", $"{_settlementId} {first.Outcome}");
            if (first.Outcome != SettlementApplyOutcome.Applied)
            {
                Finish();
                return;
            }
            var second = await store.ApplySettlementAsync(payload, CancellationToken.None);
            Check(second.Outcome == SettlementApplyOutcome.AlreadyApplied, "profile.exactly_once", $"retry={second.Outcome}");

            _creditsExpected = Math.Min(_creditsBefore + 1, ProfileSave.MaxCurrency);
            _runsExpected = _runsBefore + 1;
            _wantLabel = $"Settlement ledger — {_creditsExpected} cr · {first.Save.Currencies.ResearchData} research · {first.Save.Currencies.AbyssShards} shards · {_runsExpected}W/{first.Save.Stats.RunsFailed}L";

            // The REAL menu scene on the main thread: its _Ready builds UI and calls LoadProfileAsync.
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_finished) return;
            _menu = ResourceLoader.Load<PackedScene>("res://scenes/main_menu.tscn").Instantiate<MainMenu>();
            AddChild(_menu);
            Check(true, "menu.instantiated", "real main_menu.tscn in tree");
            _deadlineMsec = Time.GetTicksMsec() + LabelTimeoutMs;
            _waiting = true;
        }
        catch (Exception ex)
        {
            Check(false, "profile.exception", ex.GetType().Name + ": " + ex.Message);
            Finish();
        }
    }

    private async void VerifyAndFinishAsync()
    {
        try
        {
            var store = new FileSaveStore(GameServices.Paths.SavesDir);
            var profile = await store.LoadAsync(CancellationToken.None);
            Check(profile.Currencies.Credits == _creditsExpected, "profile.credits_persisted",
                $"before={_creditsBefore} after={profile.Currencies.Credits} expected={_creditsExpected}");
            Check(profile.AppliedSettlementIds.Contains(_settlementId, StringComparer.Ordinal), "profile.id_recorded", _settlementId);
            Check(profile.Stats.RunsCompleted == _runsExpected, "profile.run_counter",
                $"{_runsBefore} -> {profile.Stats.RunsCompleted}");

            // Re-enter the main thread before touching live nodes.
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_finished) return;
            if (_menu is null || !IsInstanceValid(_menu))
            {
                Check(false, "menu.label_reflects_profile", "menu freed before label assert");
                Finish();
                return;
            }
            var label = FindLabel(_menu);
            Check(label is not null && label.Text == _wantLabel, "menu.label_reflects_profile",
                label is null ? "no profile label found" : label.Text);
            if (!_ok)
            {
                Finish();
                return;
            }
            // Same isolated store: real research purchase (via the real button
            // signal), double-press charges once, reload -> equip -> sim effect.
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_finished) return;
            await ResearchBuyPhaseAsync();
            if (_finished) return;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_finished) return;
            // Unsaved session by choice: the real purchase handler must leave
            // the profile file byte-identical (no write of any kind).
            await OptOutPhaseAsync();
        }
        catch (Exception ex)
        {
            Check(false, "menu.assert", ex.GetType().Name + ": " + ex.Message);
        }
        Finish();
    }

    /// <summary>
    /// Real research purchase on the isolated store: grants research data via
    /// the production store path, presses the REAL catalog-built Buy button
    /// signal, presses it again, and asserts exactly one charge. Reloads the
    /// profile (owned persists), equips through the real loadout gate, and
    /// asserts a numeric sim effect plus the unowned gate refusal.
    /// </summary>
    private async Task ResearchBuyPhaseAsync()
    {
        ResearchScreen? screen = null;
        try
        {
            if (!ContentCatalog.TryBuild(out var catalog, out var errors) || catalog is null)
            {
                Check(false, "research.catalog", string.Join("; ", errors));
                return;
            }
            var store = new FileSaveStore(GameServices.Paths.SavesDir);
            const long grant = 500L;
            await store.UpdateAsync(p => p with
            {
                Currencies = new SaveCurrencies(p.Currencies.Credits, p.Currencies.ResearchData + grant, p.Currencies.AbyssShards),
            }, CancellationToken.None);
            var target = catalog.Modules.Values
                .Where(m => ModuleLoadout.IsSupported(m.Id))
                .OrderBy(m => m.ResearchCost)
                .FirstOrDefault();
            if (target is null)
            {
                Check(false, "research.target", "no supported module in catalog");
                return;
            }
            var before = await store.LoadAsync(CancellationToken.None);
            var rdBefore = before.Currencies.ResearchData;
            Check(rdBefore >= target.ResearchCost, "research.granted",
                $"have={rdBefore} need={target.ResearchCost}");

            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_finished) return;
            screen = new ResearchScreen { Name = "RoundtripResearch" };
            AddChild(screen);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_finished) return;
            if (screen is null || !IsInstanceValid(screen))
            {
                Check(false, "research.screen", "research screen lost");
                return;
            }
            var buy = FindUnlockButton(screen, target.Id);
            Check(buy is not null, "research.button",
                buy is null ? $"no buy button for {target.Id}" : $"{target.Id} button='{buy.Text}'");
            if (buy is null) return;

            // First press: real button signal through the production handler.
            buy.EmitSignal(BaseButton.SignalName.Pressed);
            var ownedAfterFirst = await WaitForBlueprintAsync(store, target.Id);
            var first = await store.LoadAsync(CancellationToken.None);
            Check(ownedAfterFirst, "research.buy_charged_once",
                $"owned={ownedAfterFirst} rd={first.Currencies.ResearchData}");
            Check(first.Currencies.ResearchData == rdBefore - target.ResearchCost, "research.charged_exactly",
                $"before={rdBefore} after={first.Currencies.ResearchData} cost={target.ResearchCost}");

            // Second press of the SAME button: idempotent, charged exactly once.
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_finished) return;
            var same = FindUnlockButton(screen, target.Id) ?? buy;
            if (IsInstanceValid(same)) same.EmitSignal(BaseButton.SignalName.Pressed);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_finished) return;
            var second = await store.LoadAsync(CancellationToken.None);
            Check(second.Currencies.ResearchData == first.Currencies.ResearchData, "research.double_press_single_charge",
                $"first={first.Currencies.ResearchData} second={second.Currencies.ResearchData}");
            Check(second.Unlocks.Blueprints.Count(b => string.Equals(b, target.Id, StringComparison.Ordinal)) == 1,
                "research.owned_once", target.Id);

            // Reload -> equip -> numeric sim effect through the real gate.
            var owned = new HashSet<string>(second.Unlocks.Blueprints, StringComparer.Ordinal);
            Check(ModuleLoadout.TryValidate(new[] { target.Id }, catalog, owned, out var gateErrors),
                "research.equip_gate", gateErrors.Count == 0 ? target.Id : string.Join("; ", gateErrors));
            var fx = ModuleLoadout.Resolve(new[] { target.Id });
            var stock = ModuleLoadout.Resolve(Array.Empty<string>());
            var differs = Math.Abs(fx.PulseRangeMult - stock.PulseRangeMult) > 0.001f
                || Math.Abs(fx.PulseThreatMult - stock.PulseThreatMult) > 0.001f
                || Math.Abs(fx.PulseCooldownMult - stock.PulseCooldownMult) > 0.001f
                || Math.Abs(fx.PassiveRangeMult - stock.PassiveRangeMult) > 0.001f
                || Math.Abs(fx.EngineNoiseMult - stock.EngineNoiseMult) > 0.001f
                || Math.Abs(fx.EngineThrustMult - stock.EngineThrustMult) > 0.001f
                || Math.Abs(fx.MaxHullBonus - stock.MaxHullBonus) > 0.001f
                || Math.Abs(fx.HullRatingBonus - stock.HullRatingBonus) > 0.001f
                || Math.Abs(fx.SalvageRangeMeters - stock.SalvageRangeMeters) > 0.001f;
            Check(differs, "research.sim_effect_numeric", ModuleLoadout.Describe(target.Id));
            var req = new RunGenerationRequest(4242UL, "biome.shelf_graveyard", "contract.salvage_quota", catalog);
            if (TrenchGenerator.TryGenerate(req, out var world, out var verdict, out _) && world is not null && verdict.IsValid)
            {
                Check(RunSimulation.TryCreate(world, catalog, "difficulty.standard", "frame.skiff", null,
                        RunSimulation.InsuranceNone, out _, out _,
                        new[] { target.Id }, owned),
                    "research.sim_create_equipped", target.Id);
                Check(!RunSimulation.TryCreate(world, catalog, "difficulty.standard", "frame.skiff", null,
                        RunSimulation.InsuranceNone, out _, out _,
                        new[] { target.Id }, new HashSet<string>(StringComparer.Ordinal)),
                    "research.unowned_refused", target.Id);
            }
            else
            {
                Check(false, "research.sim_world", "could not generate verification world");
            }
        }
        catch (Exception ex)
        {
            Check(false, "research.exception", ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try
            {
                if (screen is not null && IsInstanceValid(screen)) screen.QueueFree();
            }
            catch { }
        }
    }

    /// <summary>
    /// Unsaved session by choice on the isolated store: sets the session
    /// opt-out, snapshots the real profile file bytes, drives the REAL
    /// purchase handler, and asserts the file stays byte-identical.
    /// </summary>
    private async Task OptOutPhaseAsync()
    {
        ResearchScreen? screen = null;
        try
        {
            if (!ContentCatalog.TryBuild(out var catalog, out _) || catalog is null)
            {
                Check(false, "optout.catalog", "no catalog");
                return;
            }
            GameServices.ProfileSession.ContinueWithoutSaving = true;
            var store = new FileSaveStore(GameServices.Paths.SavesDir);
            var profileBefore = await store.LoadAsync(CancellationToken.None);
            var target = catalog.Modules.Values
                .Where(m => ModuleLoadout.IsSupported(m.Id)
                    && !profileBefore.Unlocks.Blueprints.Contains(m.Id, StringComparer.Ordinal))
                .OrderBy(m => m.ResearchCost)
                .FirstOrDefault();
            if (target is null)
            {
                Check(true, "optout.nothing_to_buy", "every supported blueprint already owned");
                return;
            }
            // Top up so the only refusal reason is the opt-out itself.
            await store.UpdateAsync(p => p with
            {
                Currencies = new SaveCurrencies(p.Currencies.Credits,
                    Math.Max(p.Currencies.ResearchData, target.ResearchCost), p.Currencies.AbyssShards),
            }, CancellationToken.None);
            byte[] bytesBefore;
            try
            {
                bytesBefore = await File.ReadAllBytesAsync(store.PrimaryPath);
            }
            catch (Exception ex)
            {
                Check(false, "optout.snapshot", ex.GetType().Name + ": " + ex.Message);
                return;
            }

            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_finished) return;
            screen = new ResearchScreen { Name = "RoundtripOptOut" };
            AddChild(screen);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_finished) return;
            if (screen is null || !IsInstanceValid(screen))
            {
                Check(false, "optout.screen", "research screen lost");
                return;
            }
            Check(screen.BalanceText.Contains("not saved by choice"), "optout.choice_shown", screen.BalanceText);
            var buy = FindUnlockButton(screen, target.Id);
            if (buy is not null && IsInstanceValid(buy)) buy.EmitSignal(BaseButton.SignalName.Pressed);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_finished) return;
            byte[] bytesAfter;
            try
            {
                bytesAfter = await File.ReadAllBytesAsync(store.PrimaryPath);
            }
            catch (Exception ex)
            {
                Check(false, "optout.reread", ex.GetType().Name + ": " + ex.Message);
                return;
            }
            Check(bytesBefore.SequenceEqual(bytesAfter), "optout.bytes_unchanged",
                $"before={bytesBefore.Length}B after={bytesAfter.Length}B handler={target.Id}");
            var profileAfter = await store.LoadAsync(CancellationToken.None);
            Check(!profileAfter.Unlocks.Blueprints.Contains(target.Id, StringComparer.Ordinal)
                && profileAfter.Currencies.ResearchData == Math.Max(profileBefore.Currencies.ResearchData, target.ResearchCost),
                "optout.nothing_charged", target.Id);
        }
        catch (Exception ex)
        {
            Check(false, "optout.exception", ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try
            {
                if (screen is not null && IsInstanceValid(screen)) screen.QueueFree();
            }
            catch { }
        }
    }

    private async Task<bool> WaitForBlueprintAsync(FileSaveStore store, string moduleId)
    {
        var deadline = Time.GetTicksMsec() + 10_000UL;
        while (Time.GetTicksMsec() < deadline)
        {
            if (_finished) return false;
            try
            {
                var profile = await store.LoadAsync(CancellationToken.None);
                if (profile.Unlocks.Blueprints.Contains(moduleId, StringComparer.Ordinal)) return true;
            }
            catch { }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return false;
    }

    private static Button? FindUnlockButton(Node root, string moduleId)
    {
        // Exact row match first (no fallbacks inside the recursion, so a
        // nested subtree can never leak an unrelated button as a "match").
        var exact = FindRowButton(root, moduleId);
        if (exact is not null) return exact;
        return FindFirstUnlock(root);
    }

    private static Button? FindRowButton(Node root, string moduleId)
    {
        // Real catalog-built rows: the row name label starts with the module id
        // and the Buy button is its sibling in the same row container.
        if (root is Label lab && IsInstanceValid(lab)
            && lab.Text.StartsWith(moduleId, StringComparison.Ordinal))
        {
            var parent = lab.GetParent();
            if (parent is not null && IsInstanceValid(parent))
            {
                foreach (var sibling in parent.GetChildren())
                {
                    if (sibling is Button sbuy && IsInstanceValid(sbuy)) return sbuy;
                }
            }
        }
        foreach (var child in root.GetChildren())
        {
            var found = FindRowButton(child, moduleId);
            if (found is not null) return found;
        }
        return null;
    }

    private static Button? FindFirstUnlock(Node root)
    {
        if (root is Button rb && IsInstanceValid(rb)
            && rb.Text.StartsWith("Unlock", StringComparison.Ordinal)) return rb;
        foreach (var child in root.GetChildren())
        {
            var found = FindFirstUnlock(child);
            if (found is not null) return found;
        }
        return null;
    }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        _waiting = false;
        Cleanup();
        GD.Print("---- profile roundtrip log ----");
        foreach (var line in _log) GD.Print(line);
        GD.Print("SMOKE RESULT " + (_ok ? "PASS" : "FAIL"));
        GD.Print("SMOKE DONE ok=" + _ok);
        GetTree()?.CallDeferred(SceneTree.MethodName.Quit, _ok ? 0 : 1);
    }

    private void Cleanup()
    {
        try
        {
            if (_menu is not null && IsInstanceValid(_menu)) _menu.QueueFree();
        }
        catch { }
        _menu = null;
        try { _sessionLock?.Release(); } catch { }
        try { _sessionLock?.Dispose(); } catch { }
        _sessionLock = null;
        try { _logger?.Dispose(); } catch { }
        _logger = null;
        try { GameServices.ResetForTests(); } catch { }
        try
        {
            if (IsIsolatedTempRoot(_tempRoot) && Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch { }
    }

    private static bool IsIsolatedTempRoot(string root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        string tmp;
        try { tmp = Path.GetFullPath(Path.GetTempPath()); }
        catch { return false; }
        string full;
        try { full = Path.GetFullPath(root); }
        catch { return false; }
        if (!full.StartsWith(tmp, StringComparison.OrdinalIgnoreCase)) return false;
        return Path.GetFileName(full).StartsWith(TempPrefix, StringComparison.Ordinal);
    }

    private static Label? FindLabel(Node node)
    {
        if (node is Label l && l.Text.StartsWith("Settlement ledger", StringComparison.Ordinal)) return l;
        foreach (var child in node.GetChildren())
        {
            var found = FindLabel(child);
            if (found is not null) return found;
        }
        return null;
    }
}
