using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace BDVM.Domain;

[DataContract]
public sealed class RuntimeSaveSettings
{
    [DataMember(Name = "enableSaveGameDataHook", Order = 1)]
    public bool EnableSaveGameDataHook { get; set; }

    [DataMember(Name = "enableWalletBridge", Order = 2)]
    public bool EnableWalletBridge { get; set; }

    [DataMember(Name = "enableCompanyTransfers", Order = 3)]
    public bool EnableCompanyTransfers { get; set; }

    [DataMember(Name = "enableVehicleAcquisition", Order = 4)]
    public bool EnableVehicleAcquisition { get; set; }

    [DataMember(Name = "enableLicenseQuotes", Order = 5)]
    public bool EnableLicenseQuotes { get; set; }

    [DataMember(Name = "verboseLogging", Order = 6)]
    public bool VerboseLogging { get; set; }

    [DataMember(Name = "enableMultiplayerProtocol", Order = 7)]
    public bool EnableMultiplayerProtocol { get; set; }

    [DataMember(Name = "enableFleetManagement", Order = 8)]
    public bool EnableFleetManagement { get; set; }

    [DataMember(Name = "enableVehicleResale", Order = 9)]
    public bool EnableVehicleResale { get; set; }

    [DataMember(Name = "enableCompanyGovernance", Order = 10)]
    public bool EnableCompanyGovernance { get; set; }

    [DataMember(Name = "enableOperatingCosts", Order = 11)]
    public bool EnableOperatingCosts { get; set; }

    [DataMember(Name = "enableFiniteMarket", Order = 12)]
    public bool EnableFiniteMarket { get; set; }

    [DataMember(Name = "enableInboundLeasing", Order = 13)]
    public bool EnableInboundLeasing { get; set; }

    [DataMember(Name = "enableMissionAssignments", Order = 14)]
    public bool EnableMissionAssignments { get; set; }

    [DataMember(Name = "enableIndustrialPilot", Order = 15)]
    public bool EnableIndustrialPilot { get; set; }

    [DataMember(Name = "enableOutboundLeasing", Order = 16)]
    public bool EnableOutboundLeasing { get; set; }

    [DataMember(Name = "enablePassengerEconomy", Order = 17)]
    public bool EnablePassengerEconomy { get; set; }

    [DataMember(Name = "enableDynamicEconomy", Order = 18)]
    public bool EnableDynamicEconomy { get; set; }

    [DataMember(Name = "enableDedicatedAuthority", Order = 19)]
    public bool EnableDedicatedAuthority { get; set; }

    [DataMember(Name = "enableAssetLifecycle", Order = 20)]
    public bool EnableAssetLifecycle { get; set; }

    [DataMember(Name = "enableFinancing", Order = 21)]
    public bool EnableFinancing { get; set; }

    [DataMember(Name = "enableTriageAssistance", Order = 22)]
    public bool EnableTriageAssistance { get; set; }

    [DataMember(Name = "initialDeliveryTracks", Order = 23)]
    public List<InitialDeliveryTrackRule> InitialDeliveryTracks { get; set; } = new List<InitialDeliveryTrackRule>();

    [DataMember(Name = "starterBundleDefinitionIds", Order = 24)]
    public List<string> StarterBundleDefinitionIds { get; set; } = new List<string> { "LocoDE2", "CarFlatcar", "CarFlatcar", "CarFlatcar" };

    public static RuntimeSaveSettings SafeDefaults() => new RuntimeSaveSettings();

    public static RuntimeSaveSettings Load(string path, Action<string>? warning = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return SafeDefaults();
        try
        {
            using (var stream = File.OpenRead(path))
            {
                var serializer = new DataContractJsonSerializer(typeof(RuntimeSaveSettings));
                return serializer.ReadObject(stream) as RuntimeSaveSettings ?? throw new InvalidDataException("Runtime settings are empty.");
            }
        }
        catch (Exception exception)
        {
            warning?.Invoke("Runtime settings refused; safe defaults applied: " + exception.Message);
            return SafeDefaults();
        }
    }
}

[DataContract]
public sealed class InitialDeliveryTrackRule
{
    [DataMember(Name = "trackId", Order = 1)] public string TrackId { get; set; } = "";
    [DataMember(Name = "kind", Order = 2)] public InitialDeliveryTargetKind Kind { get; set; }
}
