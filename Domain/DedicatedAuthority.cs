using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace BDVM.Domain;

[DataContract]
public sealed class DedicatedClockPolicy
{
    [DataMember(Name = "runWhileEmpty", Order = 1)] public bool RunWhileEmpty { get; set; }
    [DataMember(Name = "maximumAdvanceTicks", Order = 2)] public long MaximumAdvanceTicks { get; set; } = 1000;
}

[DataContract]
public sealed class DedicatedAuthorityState
{
    [DataMember(Name = "serverInstanceId", Order = 1)] public string ServerInstanceId { get; set; } = "";
    [DataMember(Name = "checkpointRevision", Order = 2)] public long CheckpointRevision { get; set; }
    [DataMember(Name = "clockPolicy", Order = 3)] public DedicatedClockPolicy ClockPolicy { get; set; } = new DedicatedClockPolicy();
    [DataMember(Name = "knownPlayerIds", Order = 4)] public List<string> KnownPlayerIds { get; set; } = new List<string>();
    [DataMember(Name = "commands", Order = 5)] public List<MissionAssignmentCommand> Commands { get; set; } = new List<MissionAssignmentCommand>();
    [DataMember(Name = "restartCount", Order = 6)] public int RestartCount { get; set; }
    [DataMember(Name = "rollbackCount", Order = 7)] public int RollbackCount { get; set; }
    [DataMember(Name = "webCommands", Order = 8)] public List<DedicatedWebCommand> WebCommands { get; set; } = new List<DedicatedWebCommand>();
}

[DataContract]
public sealed class DedicatedWebCommand
{
    [DataMember(Order = 1)] public string Principal { get; set; } = "";
    [DataMember(Order = 2)] public string Key { get; set; } = "";
    [DataMember(Order = 3)] public string Fingerprint { get; set; } = "";
    [DataMember(Order = 4)] public string ResultJson { get; set; } = "";
}

public sealed class DedicatedCheckpointRecord
{
    public string CheckpointId { get; set; } = "";
    public long Revision { get; set; }
    public string Payload { get; set; } = "";
}

public interface IDedicatedCheckpointStore
{
    DedicatedCheckpointRecord? Read(string checkpointId);
    long Write(string checkpointId, long expectedRevision, string payload);
}

public interface IDedicatedIdentityAuthenticator
{
    bool Authenticate(string peerSessionId, string claimedPersistentPlayerId, string credential);
}

public sealed class DedicatedAuthorityHost
{
    private readonly object gate = new object();
    private readonly IDedicatedCheckpointStore store;
    private readonly IDedicatedIdentityAuthenticator authenticator;
    private readonly INetworkRoleDetector authority;
    private readonly Dictionary<string, string> connectedPeers = new Dictionary<string, string>(StringComparer.Ordinal);
    private VehicleAcquisitionSnapshot? current;
    public VehicleAcquisitionSnapshot Current => current ?? throw new InvalidOperationException("Dedicated authority is not started.");

    public DedicatedAuthorityHost(IDedicatedCheckpointStore store, IDedicatedIdentityAuthenticator authenticator, INetworkRoleDetector authority)
    { this.store = store ?? throw new ArgumentNullException(nameof(store)); this.authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator)); this.authority = authority ?? throw new ArgumentNullException(nameof(authority)); }

    public VehicleAcquisitionSnapshot Start(string checkpointId, string serverInstanceId, string? initialPayload, DedicatedClockPolicy policy)
    {
        lock (gate)
        {
            RequireHost(); if (string.IsNullOrWhiteSpace(checkpointId) || string.IsNullOrWhiteSpace(serverInstanceId) || policy == null || policy.MaximumAdvanceTicks <= 0) throw new ArgumentException("Invalid dedicated authority startup.");
            var stored = store.Read(checkpointId); current = stored == null ? (string.IsNullOrWhiteSpace(initialPayload) ? Empty(checkpointId) : VehicleAcquisitionPersistence.Deserialize(initialPayload!, checkpointId)) : VehicleAcquisitionPersistence.Deserialize(stored.Payload, checkpointId);
            current.DedicatedAuthority.ServerInstanceId = serverInstanceId; current.DedicatedAuthority.CheckpointRevision = stored?.Revision ?? 0; current.DedicatedAuthority.ClockPolicy = policy; current.DedicatedAuthority.RestartCount++; connectedPeers.Clear();
            SaveInternal(); return current;
        }
    }

    public PlayerEconomicState Connect(string peerSessionId, string claimedPersistentPlayerId, string credential)
    {
        lock (gate)
        {
            RequireHost(); if (string.IsNullOrWhiteSpace(peerSessionId) || string.IsNullOrWhiteSpace(claimedPersistentPlayerId) || !authenticator.Authenticate(peerSessionId, claimedPersistentPlayerId, credential)) throw new InvalidOperationException("Dedicated peer authentication refused.");
            if (connectedPeers.TryGetValue(peerSessionId, out var existing) && existing != claimedPersistentPlayerId) throw new InvalidOperationException("A peer session cannot change persistent identity.");
            var before = VehicleAcquisitionPersistence.Serialize(Current);
            try
            {
                var economy = new CompanyEconomyEngine(Current.Economy);
                economy.EnsurePlayer(claimedPersistentPlayerId, 0);
                if (!Current.DedicatedAuthority.KnownPlayerIds.Contains(claimedPersistentPlayerId)) Current.DedicatedAuthority.KnownPlayerIds.Add(claimedPersistentPlayerId);
                SaveInternal();
                connectedPeers[peerSessionId] = claimedPersistentPlayerId;
                return Current.Economy.Players.Single(x => x.PlayerId == claimedPersistentPlayerId);
            }
            catch { Rollback(before); throw; }
        }
    }

    public void Disconnect(string peerSessionId) { lock (gate) connectedPeers.Remove(peerSessionId); }

    public T ExecuteAuthenticated<T>(string peerSessionId, string claimedPersistentPlayerId, Func<VehicleAcquisitionSnapshot, string, T> intent)
    {
        lock (gate)
        {
            RequireHost(); if (!connectedPeers.TryGetValue(peerSessionId, out var authenticated) || authenticated != claimedPersistentPlayerId) throw new InvalidOperationException("Dedicated intent identity is not authenticated.");
            var before = VehicleAcquisitionPersistence.Serialize(Current);
            try { var result = intent(Current, authenticated); SaveInternal(); return result; }
            catch { Rollback(before); throw; }
        }
    }

    public long Advance(string commandId, long elapsedTicks)
    {
        lock (gate)
        {
            RequireHost(); if (string.IsNullOrWhiteSpace(commandId) || elapsedTicks < 0 || elapsedTicks > Current.DedicatedAuthority.ClockPolicy.MaximumAdvanceTicks) throw new ArgumentException("Invalid dedicated clock advance.");
            var fingerprint = "advance|" + elapsedTicks + "|connected=" + connectedPeers.Count; var known = Current.DedicatedAuthority.Commands.SingleOrDefault(x => x.CommandId == commandId); if (known != null) { if (known.Fingerprint != fingerprint) throw new InvalidOperationException("Dedicated command ID payload conflict."); return Current.LeaseClock.ActiveTick; }
            var before = VehicleAcquisitionPersistence.Serialize(Current);
            try
            {
                var delta = connectedPeers.Count == 0 && !Current.DedicatedAuthority.ClockPolicy.RunWhileEmpty ? 0 : elapsedTicks;
                var release = new HeadlessReleaseGuard(); var world = new HeadlessOwnershipAdapter();
                new LeaseEngine(Current, authority, release, world).Advance(new LeaseClockAdvance { CommandId = commandId + ":lease", SessionOpen = true, ActiveGameplayTicks = delta });
                new OutboundLeaseEngine(Current, authority, release, new DeclaredOffSceneLeaseSimulationPort()).ProcessClock(commandId + ":outbound");
                new FinancingEngine(Current, authority).ProcessClock(commandId + ":financing", Current.LeaseClock.ActiveTick);
                new FiniteMarketEngine(Current, authority, world, new DisabledMarketDeliveryPort()).AdvanceTo(checked(Current.Market.ClockTick + delta));
                foreach (var route in Current.PassengerRoutes.ToArray()) new PassengerEconomyEngine(Current, authority, new HeadlessMissionCompletionPort()).RefreshDemand(commandId + ":passenger:" + route.RouteId, route.RouteId, Current.LeaseClock.ActiveTick);
                if (Current.DynamicEconomy.Policies.Count > 0 && delta > 0) new DynamicEconomyEngine(Current, authority).Recalculate(commandId + ":dynamic", Math.Max(Current.DynamicEconomy.LastCalculatedTick + 1, Current.LeaseClock.ActiveTick));
                Current.DedicatedAuthority.Commands.Add(new MissionAssignmentCommand { CommandId = commandId, Fingerprint = fingerprint, AssignmentId = "clock", ResultCode = delta.ToString() }); SaveInternal(); return Current.LeaseClock.ActiveTick;
            }
            catch { Rollback(before); throw; }
        }
    }

    public long Save() { lock (gate) { RequireHost(); return SaveInternal(); } }

    private long SaveInternal()
    {
        var payload = VehicleAcquisitionPersistence.Serialize(Current); var next = store.Write(Current.CheckpointId, Current.DedicatedAuthority.CheckpointRevision, payload); Current.DedicatedAuthority.CheckpointRevision = next; return next;
    }
    private void Rollback(string payload)
    {
        var rollback = VehicleAcquisitionPersistence.Deserialize(payload, Current.CheckpointId); rollback.DedicatedAuthority.RollbackCount++; current = rollback;
    }
    private static VehicleAcquisitionSnapshot Empty(string checkpointId) => new VehicleAcquisitionSnapshot { CheckpointId = checkpointId, Economy = new CompanyEconomySnapshot { CheckpointId = checkpointId }, Assets = new AssetRegistrySnapshot() };
    private void RequireHost() { if (!NetworkAuthorityPolicy.CanExecuteEconomy(authority.Detect(), out var reason)) throw new InvalidOperationException(reason); }

    private sealed class HeadlessReleaseGuard : IAssetReleaseGuard { public AssetReleaseInspection Inspect(string persistentCarGuid) => new AssetReleaseInspection { Status = AssetReleaseStatus.Unknown, Detail = "headless-no-world-inspection" }; }
    private sealed class HeadlessOwnershipAdapter : IExistingVehicleOwnershipAdapter { public WorldOwnershipOutcome ApplyOwner(string operationId, string persistentCarGuid, AssetOwnerRef owner) => WorldOwnershipOutcome.Unknown; public WorldOwnershipOutcome InspectOwner(string persistentCarGuid, AssetOwnerRef owner) => WorldOwnershipOutcome.Unknown; }
    private sealed class HeadlessMissionCompletionPort : IMissionCompletionPort { public WorldOwnershipOutcome Inspect(string missionId, IReadOnlyList<string> persistentCarGuids) => WorldOwnershipOutcome.Unknown; }
}

public static class DedicatedAuthorityValidation
{
    public static void Validate(DedicatedAuthorityState state)
    {
        // DataContract deserialization bypasses initializers on checkpoints predating this field.
        if (state != null && state.WebCommands == null) state.WebCommands = new List<DedicatedWebCommand>();
        if (state != null && (state.WebCommands.Count > 10000 || state.WebCommands.Any(x => x == null || string.IsNullOrWhiteSpace(x.Principal) || string.IsNullOrWhiteSpace(x.Key) || string.IsNullOrWhiteSpace(x.Fingerprint) || string.IsNullOrWhiteSpace(x.ResultJson)) || state.WebCommands.GroupBy(x => new { x.Principal, x.Key }).Any(x => x.Count() != 1))) throw new InvalidOperationException("Invalid dedicated web command ledger.");
        if (state == null || state.CheckpointRevision < 0 || state.ClockPolicy == null || state.ClockPolicy.MaximumAdvanceTicks <= 0 || state.KnownPlayerIds.Any(string.IsNullOrWhiteSpace) || state.KnownPlayerIds.Distinct(StringComparer.Ordinal).Count() != state.KnownPlayerIds.Count || state.Commands.GroupBy(x => x.CommandId).Any(x => x.Count() != 1) || state.RestartCount < 0 || state.RollbackCount < 0) throw new InvalidOperationException("Invalid dedicated authority state.");
    }
}
