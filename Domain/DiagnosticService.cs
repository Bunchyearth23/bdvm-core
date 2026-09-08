using System;
using System.Collections.Generic;
using System.Linq;

namespace BDVM.Domain;

public interface IVehicleDefinitionReader
{
    IReadOnlyList<VehicleDefinitionRecord> ReadLoadedDefinitions(string correlationId);
}

public interface IVisibleVehicleReader
{
    IReadOnlyList<VehicleInstanceRecord> ReadVisibleInventory(string correlationId);
}

public interface IDiagnosticWriter
{
    string Write(DiagnosticSnapshot snapshot);
}

public interface IDiagnosticTrace
{
    void Info(string correlationId, string message);
    void Error(string correlationId, string message, Exception exception);
}

public sealed class DiagnosticService
{
    private readonly INetworkRoleDetector roleDetector;
    private readonly IVehicleDefinitionReader definitions;
    private readonly IVisibleVehicleReader inventory;
    private readonly IDiagnosticWriter writer;
    private readonly IDiagnosticTrace trace;

    public DiagnosticService(INetworkRoleDetector roleDetector, IVehicleDefinitionReader definitions,
        IVisibleVehicleReader inventory, IDiagnosticWriter writer, IDiagnosticTrace trace)
    {
        this.roleDetector = roleDetector ?? throw new ArgumentNullException(nameof(roleDetector));
        this.definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        this.trace = trace ?? throw new ArgumentNullException(nameof(trace));
    }

    public string ExportAuthoritative() => Export(true);

    public string ExportVisibilityObservation() => Export(false);

    private string Export(bool authoritative)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var role = roleDetector.Detect();
        var reportKind = authoritative ? "authoritative" : "visibility-observation";
        trace.Info(correlationId, $"{reportKind} diagnostic export requested; networkRole={role.Role}, hasAuthority={role.HasAuthority}");
        if (authoritative && !role.HasAuthority)
        {
            var reason = role.Role == NetworkRole.MultiplayerClient
                ? "BDVM authoritative diagnostic export is host-only; use the non-authoritative visibility observation for comparison."
                : "BDVM authoritative diagnostic export is refused because network authority is indeterminate.";
            trace.Info(correlationId, "diagnostic export refused: " + reason);
            throw new InvalidOperationException(reason);
        }

        try
        {
            var loadedDefinitions = definitions.ReadLoadedDefinitions(correlationId)
                .OrderBy(d => d.ExistingDefinitionId, StringComparer.Ordinal).ToArray();
            var visibleInventory = inventory.ReadVisibleInventory(correlationId)
                .OrderBy(i => i.ExistingPersistentId, StringComparer.Ordinal).ToArray();
            var snapshot = new DiagnosticSnapshot
            {
                CorrelationId = correlationId,
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                ReportKind = reportKind,
                NetworkRole = role.Role.ToString(),
                IsAuthoritative = authoritative,
                Authority = authoritative ? "host" : "none",
                Definitions = loadedDefinitions,
                VisibleInventory = visibleInventory
            };
            var path = writer.Write(snapshot);
            trace.Info(correlationId, $"diagnostic export completed: definitions={loadedDefinitions.Length}, instances={visibleInventory.Length}, path={path}");
            return path;
        }
        catch (Exception exception)
        {
            trace.Error(correlationId, "diagnostic export failed", exception);
            throw;
        }
    }
}
