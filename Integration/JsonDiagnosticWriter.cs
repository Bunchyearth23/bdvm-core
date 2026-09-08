using System;
using System.IO;
using BDVM.Domain;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace BDVM.Adapters;

public sealed class JsonDiagnosticWriter : IDiagnosticWriter
{
    private readonly string outputDirectory;

    public JsonDiagnosticWriter(string outputDirectory) =>
        this.outputDirectory = outputDirectory ?? throw new ArgumentNullException(nameof(outputDirectory));

    public string Write(DiagnosticSnapshot snapshot)
    {
        Directory.CreateDirectory(outputDirectory);
        var fileName = $"vehicle-diagnostic-{snapshot.ReportKind}-v{snapshot.SchemaVersion}-{snapshot.GeneratedAtUtc:yyyyMMddTHHmmssZ}-{snapshot.CorrelationId}.json";
        var path = Path.GetFullPath(Path.Combine(outputDirectory, fileName));
        var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented, new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        });
        File.WriteAllText(path, json + Environment.NewLine, new System.Text.UTF8Encoding(false));
        return path;
    }
}
