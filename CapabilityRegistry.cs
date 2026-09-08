using System;
using System.Collections.Generic;
using BDVM.Common;

namespace BDVM.Core;

public sealed class CapabilityRegistry : IBdvmCapabilityRegistry
{
    private readonly Dictionary<string, BdvmCapability> capabilities = new Dictionary<string, BdvmCapability>(StringComparer.OrdinalIgnoreCase);

    public void Register(BdvmCapability capability)
    {
        if (capability == null || string.IsNullOrWhiteSpace(capability.Id) || string.IsNullOrWhiteSpace(capability.OwnerModuleId) || capability.Version == null)
            throw new ArgumentException("A complete capability is required.", nameof(capability));
        if (capabilities.ContainsKey(capability.Id)) throw new InvalidOperationException("Capability already registered: " + capability.Id);
        capabilities.Add(capability.Id, capability);
    }

    public bool TryGet(string capabilityId, out BdvmCapability capability) => capabilities.TryGetValue(capabilityId, out capability!);
}
