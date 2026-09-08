using System;
using BDVM.Domain;

namespace BDVM.Adapters;

public sealed class HostSaveGameUpdateHandler : IHostSaveUpdateHandler
{
    private readonly IRuntimeStatePayloadProvider state;
    private readonly Action<string>? info;

    public HostSaveGameUpdateHandler(IRuntimeStatePayloadProvider state, Action<string>? info = null)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.info = info;
    }

    public void Update(SaveGameData data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        var identity = SaveGameCareerIdentityReader.Read(data);
        info?.Invoke("Career identity resolved; source=" + identity.IdentitySource + ", gameModePresent=" + !string.IsNullOrWhiteSpace(identity.GameMode) + ", difficultyPresent=" + !string.IsNullOrWhiteSpace(identity.StartingDifficulty) + ", stableAnchorPresent=" + !string.IsNullOrWhiteSpace(identity.StartingTimeAndDate) + ".");
        var persistence = new SaveGameDataPersistenceService(new SaveGameDataAtomicNode(data));
        var envelope = persistence.Write(identity, state.Provide);
        info?.Invoke("BDVM state staged in SaveGameData; checkpoint=" + envelope.CheckpointId + ", schema=" + envelope.SchemaVersion);
    }
}
