using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace BDVM.Domain;

[DataContract]
public sealed class ExternalAuthorityConfiguration
{
    [DataMember(Name = "serverUrl", IsRequired = true)] public string ServerUrl { get; set; } = "";
    [DataMember(Name = "checkpointId", IsRequired = true)] public string CheckpointId { get; set; } = "";
    [DataMember(Name = "workerKeyFile", IsRequired = true)] public string WorkerKeyFile { get; set; } = "";

    public static ExternalAuthorityConfiguration Read(string path)
    {
        using (var stream = File.OpenRead(path))
        {
            if (stream.Length > 4096) throw new InvalidDataException("Dedicated authority configuration is too large.");
            var value = (ExternalAuthorityConfiguration?)new DataContractJsonSerializer(typeof(ExternalAuthorityConfiguration)).ReadObject(stream)
                ?? throw new InvalidDataException("Dedicated authority configuration is empty.");
            if (!Uri.TryCreate(value.ServerUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") || (!uri.IsLoopback && uri.Scheme != "https") ||
                uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                string.IsNullOrWhiteSpace(value.CheckpointId) || value.CheckpointId.Length > 128 || string.IsNullOrWhiteSpace(value.WorkerKeyFile))
                throw new InvalidDataException("Invalid dedicated authority origin or checkpoint identity.");
            value.WorkerKeyFile = Path.GetFullPath(Path.IsPathRooted(value.WorkerKeyFile) ? value.WorkerKeyFile : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, value.WorkerKeyFile));
            return value;
        }
    }
}

// Explicit per-process selection. A failed read leaves delegation active and throws;
// an unavailable dedicated server must never silently promote the game to authority.
public static class RuntimeAuthorityMode
{
    public static bool IsDelegated { get; private set; }
    public static ExternalAuthorityConfiguration? Configuration { get; private set; }

    public static void Configure(string path)
    {
        Configuration = null;
        IsDelegated = File.Exists(path);
        if (IsDelegated) Configuration = ExternalAuthorityConfiguration.Read(path);
    }
}
