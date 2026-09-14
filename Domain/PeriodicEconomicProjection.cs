using System;
using System.Collections.Generic;
using System.Linq;

namespace BDVM.Domain;

// A long-lived worker projection containing only inputs to the automatic
// clock/production scheduler. Historical ledgers, closed contracts, assets,
// permissions, market catalogues and other modules never cross this boundary.
public sealed class PeriodicEconomicProjection
{
    private readonly VehicleAcquisitionSnapshot state;
    public long Tick => state.LeaseClock.ActiveTick;
    public PeriodicEconomicProjection(DetachedRuntimeState captured)
    {
        state = captured?.Snapshot ?? throw new ArgumentNullException(nameof(captured));
        if (state.LeaseClock.ActiveTick < 0 || state.LeaseClock.Version < 0 || state.Economy.Wallets.Any(value => value.Balance < 0 || value.Version < 0) ||
            state.IndustrialStocks.Any(value => value.OnHand < 0 || value.Capacity < value.OnHand || value.ReservedInbound < 0 || value.ReservedOutbound < 0) ||
            state.IndustrialRecipes.Any(value => value.CadenceTicks <= 0 || value.MaximumBacklogCycles <= 0 || value.InputQuantity < 0 || value.OutputQuantity < 0))
            throw new InvalidOperationException("Invalid periodic economic projection.");
    }
    // Invoked by the Unity owner only when its observed domain revision changes.
    // This creates a selection of references; RuntimeStateCapture detaches them
    // incrementally before the selection can be handed to the worker.
    internal static VehicleAcquisitionSnapshot Select(VehicleAcquisitionSnapshot source)
    {
        var pending = source.IndustrialContracts.Where(value => !value.StockDriven && value.State == IndustrialContractState.Reserved && value.PreparationExpiresTick > 0).ToList();
        var assets = new HashSet<string>(pending.SelectMany(value => value.AssignedWagons).Select(value => value.AssetId), StringComparer.Ordinal);
        return new VehicleAcquisitionSnapshot { CheckpointId = source.CheckpointId,
            Economy = new CompanyEconomySnapshot { CheckpointId = source.CheckpointId, Wallets = source.Economy.Wallets },
            LeaseClock = source.LeaseClock, IndustrialStocks = source.IndustrialStocks, IndustrialRecipes = source.IndustrialRecipes,
            IndustrialContracts = pending, Fleet = source.Fleet.Where(value => assets.Contains(value.AssetId)).ToList(),
            IndustrialCargoTags = source.IndustrialCargoTags.Where(value => assets.Contains(value.AssetId)).ToList() };
    }
    public PeriodicEconomicDelta Advance(LeaseClockAdvance advance, INetworkRoleDetector authority, bool industry, long expectedTick)
    {
        if (!NetworkAuthorityPolicy.CanExecuteEconomy(authority.Detect(), out _)) throw new InvalidOperationException("Host authority is required.");
        if (Tick != expectedTick) throw new InvalidOperationException("Periodic worker projection is stale.");
        if (string.IsNullOrWhiteSpace(advance.CommandId) || advance.ActiveGameplayTicks < 0 || advance.SleepTicks < 0 || advance.FastTravelTicks < 0)
            throw new ArgumentException("Invalid economic clock advance.");
        var wallets = state.Economy.Wallets.ToDictionary(value => value.Account.Key, value => value.Version, StringComparer.Ordinal);
        var stocks = state.IndustrialStocks.ToDictionary(StockKey, value => value.Version, StringComparer.Ordinal);
        var recipes = state.IndustrialRecipes.ToDictionary(value => value.RecipeId, value => value.Version, StringComparer.Ordinal);
        var contracts = state.IndustrialContracts.ToDictionary(value => value.ContractId, value => value.Version, StringComparer.Ordinal);
        var fleet = state.Fleet.ToDictionary(value => value.AssetId, value => value.Version, StringComparer.Ordinal);
        var tags = state.IndustrialCargoTags.ToDictionary(value => value.AssetId, value => value.Version, StringComparer.Ordinal);
        var previousClockVersion = state.LeaseClock.Version;
        var ticks = !advance.SessionOpen || advance.Paused ? 0 : checked(advance.ActiveGameplayTicks + advance.SleepTicks + advance.FastTravelTicks);
        state.LeaseClock.ActiveTick = checked(Tick + ticks); state.LeaseClock.Version = checked(previousClockVersion + 1);
        // Only this advance's emitted records are retained. The authoritative
        // projection appends them once after its revision/clock fence succeeds.
        state.Economy.Ledger.Clear(); state.IndustrialCommands.Clear();
        if (industry)
        {
            var engine = IndustrialEconomyEngine.ForPeriodicProjection(state, authority);
            foreach (var contract in state.IndustrialContracts.Where(value => value.State == IndustrialContractState.Reserved && value.PreparationExpiresTick <= Tick).ToArray())
                engine.ExpirePreparation(advance.CommandId + ":expire:" + contract.ContractId, contract.ContractId, Tick);
            engine.AdvanceDueProduction(advance.CommandId, Tick);
        }
        var result = new PeriodicEconomicDelta { CheckpointId = state.CheckpointId, PreviousTick = expectedTick, PreviousClockVersion = previousClockVersion,
            Clock = RuntimeStateCopy.CaptureValue(state.LeaseClock),
            Wallets = Changes(state.Economy.Wallets, value => value.Account.Key, value => value.Version, wallets),
            Stocks = Changes(state.IndustrialStocks, StockKey, value => value.Version, stocks),
            Recipes = Changes(state.IndustrialRecipes, value => value.RecipeId, value => value.Version, recipes),
            Contracts = Changes(state.IndustrialContracts, value => value.ContractId, value => value.Version, contracts),
            Fleet = Changes(state.Fleet, value => value.AssetId, value => value.Version, fleet),
            Tags = Changes(state.IndustrialCargoTags, value => value.AssetId, value => value.Version, tags),
            Ledger = RuntimeStateCopy.CaptureValue(state.Economy.Ledger), Commands = RuntimeStateCopy.CaptureValue(state.IndustrialCommands),
            ClockAction = new LeaseActionRecord { CommandId = advance.CommandId,
                Fingerprint = string.Join("|", "economic-clock-no-leasing", advance.ActiveGameplayTicks, advance.SleepTicks, advance.FastTravelTicks, advance.SessionOpen, advance.Paused),
                LeaseId = "disabled", State = LeaseActionState.Succeeded, ResultCode = "economic-clock-advanced-without-leasing", Amount = ticks } };
        return result;
    }
    internal static string StockKey(IndustrialStock value) => value.FacilityId + "\0" + value.CargoId;
    private static List<PeriodicRecordChange<T>> Changes<T>(IEnumerable<T> records, Func<T, string> key, Func<T, long> version, Dictionary<string, long> before)
        => records.Where(value => version(value) != before[key(value)]).Select(value => new PeriodicRecordChange<T>
            { Key = key(value), PreviousVersion = before[key(value)], Value = RuntimeStateCopy.CaptureValue(value) }).ToList();
}

public sealed class PeriodicRecordChange<T>
{
    public string Key { get; set; } = "";
    public long PreviousVersion { get; set; }
    public T Value { get; set; } = default!;
}
public sealed class PeriodicEconomicDelta
{
    public string CheckpointId { get; set; } = "";
    public long PreviousTick { get; set; }
    public long PreviousClockVersion { get; set; }
    public LeaseClock Clock { get; set; } = null!;
    public LeaseActionRecord ClockAction { get; set; } = null!;
    public List<PeriodicRecordChange<Wallet>> Wallets { get; set; } = new List<PeriodicRecordChange<Wallet>>();
    public List<PeriodicRecordChange<IndustrialStock>> Stocks { get; set; } = new List<PeriodicRecordChange<IndustrialStock>>();
    public List<PeriodicRecordChange<IndustrialRecipe>> Recipes { get; set; } = new List<PeriodicRecordChange<IndustrialRecipe>>();
    public List<PeriodicRecordChange<IndustrialContract>> Contracts { get; set; } = new List<PeriodicRecordChange<IndustrialContract>>();
    public List<PeriodicRecordChange<FleetAssetState>> Fleet { get; set; } = new List<PeriodicRecordChange<FleetAssetState>>();
    public List<PeriodicRecordChange<IndustrialCargoTag>> Tags { get; set; } = new List<PeriodicRecordChange<IndustrialCargoTag>>();
    public List<LedgerEntry> Ledger { get; set; } = new List<LedgerEntry>();
    public List<MissionAssignmentCommand> Commands { get; set; } = new List<MissionAssignmentCommand>();

    internal bool TryApply(VehicleAcquisitionSnapshot target)
    {
        if (target.CheckpointId != CheckpointId || target.LeaseClock.ActiveTick != PreviousTick || target.LeaseClock.Version != PreviousClockVersion) return false;
        // Preflight every row before touching anything. Readers and commands on
        // Unity observe the complete transaction within the same callback.
        var apply = new List<Action>();
        if (!Prepare(target.Economy.Wallets, Wallets, value => value.Account.Key, value => value.Version, apply) ||
            !Prepare(target.IndustrialStocks, Stocks, PeriodicEconomicProjection.StockKey, value => value.Version, apply) ||
            !Prepare(target.IndustrialRecipes, Recipes, value => value.RecipeId, value => value.Version, apply) ||
            !Prepare(target.IndustrialContracts, Contracts, value => value.ContractId, value => value.Version, apply) ||
            !Prepare(target.Fleet, Fleet, value => value.AssetId, value => value.Version, apply) ||
            !Prepare(target.IndustrialCargoTags, Tags, value => value.AssetId, value => value.Version, apply)) return false;
        foreach (var action in apply) action();
        target.LeaseClock = Clock; target.LeaseActions.Add(ClockAction);
        target.Economy.Ledger.AddRange(Ledger); target.IndustrialCommands.AddRange(Commands);
        return true;
    }
    private static bool Prepare<T>(List<T> target, List<PeriodicRecordChange<T>> changes, Func<T, string> key, Func<T, long> version, List<Action> apply)
    {
        foreach (var change in changes)
        {
            var index = target.FindIndex(value => key(value) == change.Key);
            if (index < 0 || version(target[index]) != change.PreviousVersion) return false;
            var record = change.Value; apply.Add(() => target[index] = record);
        }
        return true;
    }
}
