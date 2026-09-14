using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

namespace BDVM.Domain;

// Explicit migration inventory. Adding a persisted root without choosing its
// migration fails closed instead of silently losing it at the next save.
public static class PersistentSystemMigrations
{
    public static readonly IReadOnlyDictionary<string, string[]> Systems = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["M03-economy"] = new[] { "economy", "leaseClock" },
        ["M04-industry"] = new[] { "industrialStocks", "industrialRecipes", "industrialContracts", "industrialCommands", "industrialTransportPolicies", "industrialTransportNeeds", "industrialCargoTags" },
        ["M05-fleet"] = new[] { "assets", "ownership", "offers", "acquisitions", "fleet", "fleetCommands", "resaleQuotes", "resales", "initialDeliveries", "assetLifecycle" },
        ["M06-governance-market"] = new[] { "companyLiquidations", "operatingCosts", "market", "leases", "leaseActions", "outboundLeases", "outboundLeaseActions", "dynamicEconomy", "financing" },
        ["M07-missions-integrations"] = new[] { "assignments", "assignmentCommands", "passengerRoutes", "passengerContracts", "passengerCommands", "dedicatedAuthority", "triageAssistance" }
    };
}

// Only the domain owner calls this codec. A document is a scalar or one list
// record, not the entire career. List positions are explicit: order, duplicate
// values and nulls round-trip exactly; insert/delete changes following positions.
// Replay deserializes data and never invokes a domain command or a native port.
public static class PersistentSnapshotDocuments
{
    private sealed class Member
    {
        public PropertyInfo Property = null!;
        public string Name = "";
    }
    private static readonly Member[] Roots = Members(typeof(VehicleAcquisitionSnapshot));
    private static readonly HashSet<string> Identity = new HashSet<string>(new[] { "schema", "schemaVersion", "checkpointId" }, StringComparer.Ordinal);
    public static IReadOnlyList<string> RootNames { get; } = Roots.Select(value => value.Name).ToArray();

    static PersistentSnapshotDocuments()
    {
        var declared = PersistentSystemMigrations.Systems.SelectMany(value => value.Value).ToArray();
        if (declared.Distinct(StringComparer.Ordinal).Count() != declared.Length ||
            !new HashSet<string>(declared, StringComparer.Ordinal).SetEquals(Roots.Select(value => value.Name).Where(value => !Identity.Contains(value))))
            throw new InvalidDataException("Persistent system migrations do not cover the saved schema exactly once.");
    }

    public static IReadOnlyList<PersistentDocument> Export(VehicleAcquisitionSnapshot snapshot, IEnumerable<string>? roots = null)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        var selected = Select(roots);
        var result = new List<PersistentDocument>();
        foreach (var root in Roots)
            if (selected == null || selected.Contains(root.Name))
                Write(result, root.Name, "$", root.Property.PropertyType, root.Property.GetValue(snapshot), true);
        return result;
    }

    public static VehicleAcquisitionSnapshot Restore(IEnumerable<PersistentDocument> documents, string expectedCheckpointId, bool validate = true)
    {
        var source = Index(documents);
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        var result = new VehicleAcquisitionSnapshot();
        foreach (var root in Roots)
            root.Property.SetValue(result, Read(source, consumed, root.Name, "$", root.Property.PropertyType, true));
        if (consumed.Count != source.Count) throw new InvalidDataException("Checkpoint contains an unknown or orphaned document.");
        if (result.CheckpointId != expectedCheckpointId) throw new InvalidDataException("Checkpoint document career identity mismatch.");
        if (validate) VehicleAcquisitionPersistence.Validate(result);
        return result;
    }

    public static IReadOnlyList<PersistentMutation> Difference(IEnumerable<PersistentDocument> before, IEnumerable<PersistentDocument> after)
    {
        var old = Index(before); var next = Index(after);
        var result = new List<PersistentMutation>();
        foreach (var item in next.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            old.TryGetValue(item.Key, out var previous);
            if (previous != null && previous.Value == item.Value.Value) continue;
            result.Add(new PersistentMutation { System = item.Value.System, Key = item.Value.Key,
                ExpectedHash = previous == null ? "" : PersistentJournalCodec.Hash(previous.Value), Value = item.Value.Value });
        }
        foreach (var item in old.OrderBy(value => value.Key, StringComparer.Ordinal))
            if (!next.ContainsKey(item.Key)) result.Add(new PersistentMutation { System = item.Value.System, Key = item.Value.Key,
                ExpectedHash = PersistentJournalCodec.Hash(item.Value.Value), Value = null });
        return result;
    }

    private static HashSet<string>? Select(IEnumerable<string>? roots)
    {
        if (roots == null) return null;
        var result = new HashSet<string>(roots, StringComparer.Ordinal);
        if (result.Any(value => !RootNames.Contains(value))) throw new ArgumentException("Unknown persistent system.", nameof(roots));
        return result;
    }
    private static Dictionary<string, PersistentDocument> Index(IEnumerable<PersistentDocument> documents)
    {
        var result = new Dictionary<string, PersistentDocument>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            if (document == null || document.System == null || document.Key == null || document.Value == null || document.System.Contains("\0") || document.Key.Contains("\0"))
                throw new InvalidDataException("Invalid snapshot document.");
            var key = document.System + "\0" + document.Key;
            if (result.ContainsKey(key)) throw new InvalidDataException("Duplicate snapshot document.");
            result.Add(key, document);
        }
        return result;
    }
    private static Member[] Members(Type type)
    {
        if (type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Any(field => field.GetCustomAttribute<DataMemberAttribute>() != null))
            throw new InvalidDataException("A persisted field requires an explicit document migration: " + type.FullName);
        return type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(property => new { Property = property, Data = property.GetCustomAttribute<DataMemberAttribute>() })
            .Where(value => value.Data != null).OrderBy(value => value.Data!.Order)
            .Select(value => new Member { Property = value.Property, Name = value.Data!.Name ?? value.Property.Name }).ToArray();
    }
    private static void Add(List<PersistentDocument> result, string system, string key, string value)
        => result.Add(new PersistentDocument { System = system, Key = key, Value = value });
    private static void Write(List<PersistentDocument> result, string system, string key, Type type, object? value, bool split)
    {
        if (value == null) { Add(result, system, key, "null"); return; }
        if (value is IList list && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            Add(result, system, key, "list:" + list.Count.ToString(CultureInfo.InvariantCulture));
            var element = type.GetGenericArguments()[0];
            for (var i = 0; i < list.Count; i++)
                Add(result, system, key + "/" + i.ToString("D10", CultureInfo.InvariantCulture), PersistentJournalCodec.Serialize(list[i], element));
            return;
        }
        if (split && type.GetCustomAttribute<DataContractAttribute>() != null && !type.IsEnum && !type.IsValueType)
        {
            Add(result, system, key, "object");
            foreach (var member in Members(type))
                Write(result, system, key + "/" + member.Name, member.Property.PropertyType, member.Property.GetValue(value), false);
            return;
        }
        Add(result, system, key, "value:" + PersistentJournalCodec.Serialize(value, type));
    }
    private static object? Read(Dictionary<string, PersistentDocument> source, HashSet<string> consumed, string system, string key, Type type, bool split)
    {
        var documentKey = system + "\0" + key;
        if (!source.TryGetValue(documentKey, out var document)) throw new InvalidDataException("Missing snapshot document: " + system + "/" + key);
        consumed.Add(documentKey);
        var value = document.Value;
        if (value == "null") return null;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            if (!value.StartsWith("list:", StringComparison.Ordinal) || !int.TryParse(value.Substring(5), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 0 || count > source.Count)
                throw new InvalidDataException("Invalid snapshot collection size.");
            var list = (IList)Activator.CreateInstance(type)!;
            for (var i = 0; i < count; i++)
            {
                var itemKey = system + "\0" + key + "/" + i.ToString("D10", CultureInfo.InvariantCulture);
                if (!source.TryGetValue(itemKey, out var item)) throw new InvalidDataException("Missing snapshot collection entry.");
                consumed.Add(itemKey); list.Add(PersistentJournalCodec.Deserialize(item.Value, type.GetGenericArguments()[0]));
            }
            return list;
        }
        if (split && type.GetCustomAttribute<DataContractAttribute>() != null && !type.IsEnum && !type.IsValueType)
        {
            if (value != "object") throw new InvalidDataException("Invalid snapshot object marker.");
            var result = Activator.CreateInstance(type)!;
            foreach (var member in Members(type)) member.Property.SetValue(result,
                Read(source, consumed, system, key + "/" + member.Name, member.Property.PropertyType, false));
            return result;
        }
        if (!value.StartsWith("value:", StringComparison.Ordinal)) throw new InvalidDataException("Invalid snapshot value marker.");
        return PersistentJournalCodec.Deserialize(value.Substring(6), type);
    }
}
