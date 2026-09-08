using System;
using System.Collections.Generic;

namespace BDVM.Domain;

public static class DiagnosticSchema
{
    public const int Version = 2;
    public const string Name = "bdvm.vehicle-diagnostic";
}

public enum ResolutionState
{
    Resolved,
    MissingIdentifier,
    MissingDefinition,
    AmbiguousDefinition,
    ExcludedExternalTraffic
}

public sealed class OriginRecord
{
    public string Kind { get; set; } = "unknown";
    public string? ProviderId { get; set; }
    public string? AssemblyName { get; set; }
}

public sealed class VehicleDefinitionRecord
{
    public string? ExistingDefinitionId { get; set; }
    public string? Type { get; set; }
    public OriginRecord Origin { get; set; } = new OriginRecord();
    public IReadOnlyList<string> Components { get; set; } = Array.Empty<string>();
    public ResolutionState Resolution { get; set; }
    public string? ResolutionDetail { get; set; }
}

public sealed class VehicleInstanceRecord
{
    public string? ExistingPersistentId { get; set; }
    public string? ExistingVisibleId { get; set; }
    public string? ExistingSessionNetId { get; set; }
    public string? DefinitionId { get; set; }
    public string? Type { get; set; }
    public OriginRecord Origin { get; set; } = new OriginRecord();
    public IReadOnlyList<string> Components { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> TrainsetMemberPersistentIds { get; set; } = Array.Empty<string>();
    public ResolutionState Resolution { get; set; }
    public string? ResolutionDetail { get; set; }
}

public sealed class DiagnosticSnapshot
{
    public string Schema { get; set; } = DiagnosticSchema.Name;
    public int SchemaVersion { get; set; } = DiagnosticSchema.Version;
    public string CorrelationId { get; set; } = "";
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public string ReportKind { get; set; } = "authoritative";
    public string NetworkRole { get; set; } = "indeterminate";
    public bool IsAuthoritative { get; set; }
    public string Authority { get; set; } = "none";
    public string MutationPolicy { get; set; } = "read-only";
    public IReadOnlyList<VehicleDefinitionRecord> Definitions { get; set; } = Array.Empty<VehicleDefinitionRecord>();
    public IReadOnlyList<VehicleInstanceRecord> VisibleInventory { get; set; } = Array.Empty<VehicleInstanceRecord>();
}
