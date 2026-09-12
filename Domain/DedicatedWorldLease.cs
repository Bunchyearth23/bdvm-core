using System;
using System.Diagnostics;

namespace BDVM.Domain;

public sealed class WorldLeaseRequest
{
    public int SchemaVersion { get; set; } = 1;
    public string CheckpointId { get; set; } = "";
    public string WorkerId { get; set; } = "";
}

public sealed class WorldLeaseGrant
{
    public string ServerEpoch { get; set; } = "";
    public string LeaseId { get; set; } = "";
    public int ExpiresAfterSeconds { get; set; }
    public long NextSequence { get; set; }
}

public sealed class WorldHeartbeat
{
    public int SchemaVersion { get; set; } = 1;
    public string CheckpointId { get; set; } = "";
    public string WorkerId { get; set; } = "";
    public string ServerEpoch { get; set; } = "";
    public string LeaseId { get; set; } = "";
    public long Sequence { get; set; }
    public bool WorldLoaded { get; set; }
    public long ObservedTick { get; set; }
}

public sealed class WorldPresence
{
    public bool TelemetryConnected { get; set; }
    public bool WorldLoaded { get; set; }
    public long ObservedTick { get; set; }
    public long Sequence { get; set; }
}

// A heartbeat is a liveness observation, never a world-state or economic commit.
// Every process boot gets a new epoch; expired workers cannot revive an old lease.
public sealed class DedicatedWorldLease
{
    private const int LeaseSeconds = 15;
    private readonly object gate = new object();
    private readonly string checkpoint;
    private readonly string epoch = Guid.NewGuid().ToString("N");
    private readonly Func<double> clock;
    private string worker = "", lease = "";
    private double deadline;
    private long sequence;
    private WorldPresence presence = new WorldPresence();

    public DedicatedWorldLease(string checkpoint, Func<double>? monotonicSeconds = null)
    {
        this.checkpoint = checkpoint;
        clock = monotonicSeconds ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
    }

    public WorldLeaseGrant Acquire(WorldLeaseRequest request)
    {
        lock (gate)
        {
            if (request == null || request.SchemaVersion != 1 || request.CheckpointId != checkpoint || !Guid.TryParse(request.WorkerId, out _))
                throw new InvalidOperationException("World identity or protocol mismatch.");
            var now = clock();
            if (lease.Length != 0 && now < deadline)
            {
                if (worker != request.WorkerId) throw new InvalidOperationException("Another world worker holds the active lease.");
                return Grant();
            }
            worker = request.WorkerId;
            lease = Guid.NewGuid().ToString("N");
            sequence = 0;
            presence = new WorldPresence();
            deadline = now + LeaseSeconds;
            return Grant();
        }
    }

    public void Observe(WorldHeartbeat heartbeat)
    {
        lock (gate)
        {
            var now = clock();
            if (heartbeat == null || heartbeat.SchemaVersion != 1 || heartbeat.CheckpointId != checkpoint ||
                heartbeat.WorkerId != worker || heartbeat.ServerEpoch != epoch || heartbeat.LeaseId != lease || lease.Length == 0 ||
                now >= deadline || heartbeat.Sequence <= sequence || heartbeat.ObservedTick < 0)
                throw new InvalidOperationException("Stale or invalid world observation.");
            sequence = heartbeat.Sequence;
            deadline = now + LeaseSeconds;
            presence = new WorldPresence { TelemetryConnected = true, WorldLoaded = heartbeat.WorldLoaded, ObservedTick = heartbeat.ObservedTick, Sequence = heartbeat.Sequence };
        }
    }

    public WorldPresence Read()
    {
        lock (gate)
        {
            if (clock() >= deadline) return new WorldPresence();
            return new WorldPresence { TelemetryConnected = presence.TelemetryConnected, WorldLoaded = presence.WorldLoaded, ObservedTick = presence.ObservedTick, Sequence = presence.Sequence };
        }
    }

    private WorldLeaseGrant Grant() => new WorldLeaseGrant { ServerEpoch = epoch, LeaseId = lease, ExpiresAfterSeconds = LeaseSeconds, NextSequence = checked(sequence + 1) };
}
