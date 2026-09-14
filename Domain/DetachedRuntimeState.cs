using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

namespace BDVM.Domain;

public sealed class DetachedRuntimeState
{
    public VehicleAcquisitionSnapshot Snapshot { get; }
    public long Revision { get; }
    internal DetachedRuntimeState(VehicleAcquisitionSnapshot snapshot, long revision) { Snapshot = snapshot; Revision = revision; }
}

// One owner advances this capture between frames. A revision change discards the
// partial graph; a worker only receives Result after every live read has finished.
public sealed class RuntimeStateCapture : IDisposable
{
    private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
    private readonly Func<bool> isCurrent;
    private readonly long revision;
    private readonly RuntimeStateCopy.Session session;
    private bool disposed;
    internal RuntimeStateCapture(VehicleAcquisitionSnapshot source, long revision, Func<bool> isCurrent)
    { this.revision = revision; this.isCurrent = isCurrent; session = new RuntimeStateCopy.Session(source); }
    public DetachedRuntimeState Result => !disposed && session.Complete
        ? new DetachedRuntimeState((VehicleAcquisitionSnapshot)session.Result!, revision)
        : throw new InvalidOperationException("The runtime capture has not completed.");
    public bool Advance(double milliseconds = 0.75, int maximumSteps = 4096)
    {
        if (Thread.CurrentThread.ManagedThreadId != ownerThread) throw new InvalidOperationException("Runtime capture must stay on its owner thread.");
        if (disposed) throw new ObjectDisposedException(nameof(RuntimeStateCapture));
        if (milliseconds <= 0 || maximumSteps <= 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        if (!isCurrent()) { Dispose(); throw new OperationCanceledException("Runtime state changed during capture."); }
        var started = Stopwatch.GetTimestamp();
        for (var step = 0; step < maximumSteps; step++)
        {
            if (session.Advance()) return true;
            if ((Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency >= milliseconds) break;
        }
        return false;
    }
    public void Dispose() { disposed = true; session.Dispose(); }
}

// Copy persisted data on its owner's thread. Validation and JSON follow on a worker.
internal static class RuntimeStateCopy
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Members = new ConcurrentDictionary<Type, PropertyInfo[]>();
    public static VehicleAcquisitionSnapshot Capture(VehicleAcquisitionSnapshot source)
        => (VehicleAcquisitionSnapshot)Copy(source, new Dictionary<object, object>(Identity.Instance), 0)!;
    internal static T CaptureValue<T>(T source)
        => (T)Copy(source, new Dictionary<object, object>(Identity.Instance), 0)!;

    internal sealed class Session : IDisposable
    {
        private sealed class Frame
        {
            public object Source = null!, Target = null!;
            public PropertyInfo[]? Properties;
            public IDictionaryEnumerator? Entries;
            public int Index, Count, Depth;
        }
        private readonly Stack<Frame> frames = new Stack<Frame>();
        private Dictionary<object, object> copies = new Dictionary<object, object>(Identity.Instance);
        private object? source;
        private bool started;
        public object? Result { get; private set; }
        public bool Complete => started && frames.Count == 0;
        public Session(object source) { this.source = source; }
        public bool Advance()
        {
            if (!started) { started = true; Result = Begin(source, 0); source = null; return Complete; }
            if (frames.Count == 0) return true;
            var frame = frames.Peek();
            if (frame.Entries != null)
            {
                if (!frame.Entries.MoveNext()) { Pop(); return Complete; }
                var entry = frame.Entries.Entry;
                ((IDictionary)frame.Target).Add(Begin(entry.Key, frame.Depth + 1)!, Begin(entry.Value, frame.Depth + 1));
            }
            else if (frame.Source is IList list)
            {
                if (list.Count != frame.Count) throw new OperationCanceledException("Runtime collection changed during capture.");
                if (frame.Index == frame.Count) { Pop(); return Complete; }
                var index = frame.Index++;
                var item = Begin(list[index], frame.Depth + 1);
                if (frame.Target is Array) ((IList)frame.Target)[index] = item; else ((IList)frame.Target).Add(item);
            }
            else
            {
                if (frame.Index == frame.Properties!.Length) { Pop(); return Complete; }
                var property = frame.Properties[frame.Index++];
                property.SetValue(frame.Target, Begin(property.GetValue(frame.Source), frame.Depth + 1));
            }
            return Complete;
        }
        private object? Begin(object? value, int depth)
        {
            if (value == null) return null;
            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is string || value is decimal || value is Guid ||
                value is DateTime || value is DateTimeOffset || value is TimeSpan) return value;
            if (copies.TryGetValue(value, out var existing)) return existing;
            if (depth >= 64) throw new InvalidOperationException("Runtime state nesting exceeds the capture limit.");
            var frame = new Frame { Source = value, Depth = depth };
            if (value is IDictionary dictionary)
            {
                var comparerProperty = type.GetProperty("Comparer");
                var constructor = comparerProperty == null ? null : type.GetConstructor(new[] { comparerProperty.PropertyType });
                frame.Target = constructor == null ? Activator.CreateInstance(type)!
                    : constructor.Invoke(new[] { comparerProperty!.GetValue(value) });
                frame.Entries = dictionary.GetEnumerator();
            }
            else if (value is IList list)
            {
                frame.Count = list.Count;
                frame.Target = type.IsArray ? Array.CreateInstance(type.GetElementType()!, list.Count) : Activator.CreateInstance(type)!;
            }
            else
            {
                if (!string.Equals(type.Namespace, "BDVM.Domain", StringComparison.Ordinal))
                    throw new InvalidOperationException("Runtime state contains an unsupported mutable type: " + type.FullName);
                frame.Properties = Members.GetOrAdd(type, valueType => valueType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(property => valueType.IsDefined(typeof(DataContractAttribute), false)
                        ? property.IsDefined(typeof(DataMemberAttribute), false)
                        : property.GetMethod?.IsPublic == true && property.SetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0).ToArray());
                frame.Target = Activator.CreateInstance(type)!;
            }
            copies.Add(value, frame.Target);
            frames.Push(frame);
            return frame.Target;
        }
        private void Pop() { var frame = frames.Pop(); (frame.Entries as IDisposable)?.Dispose(); }
        public void Dispose() { while (frames.Count > 0) Pop(); copies = null!; source = null; Result = null; }
    }
    private static object? Copy(object? source, Dictionary<object, object> copies, int depth)
    {
        if (source == null) return null;
        var type = source.GetType();
        if (type.IsPrimitive || type.IsEnum || source is string || source is decimal || source is Guid ||
            source is DateTime || source is DateTimeOffset || source is TimeSpan) return source;
        if (copies.TryGetValue(source, out var existing)) return existing;
        if (depth >= 64) throw new InvalidOperationException("Runtime state nesting exceeds the capture limit.");
        if (source is IDictionary dictionary)
        {
            var comparerProperty = type.GetProperty("Comparer");
            var constructor = comparerProperty == null ? null : type.GetConstructor(new[] { comparerProperty.PropertyType });
            var target = (IDictionary)(constructor == null ? Activator.CreateInstance(type)!
                : constructor.Invoke(new[] { comparerProperty!.GetValue(source) }));
            copies.Add(source, target);
            foreach (DictionaryEntry item in dictionary) target.Add(Copy(item.Key, copies, depth + 1)!, Copy(item.Value, copies, depth + 1));
            return target;
        }
        if (source is IList list)
        {
            var target = type.IsArray ? (IList)Array.CreateInstance(type.GetElementType()!, list.Count) : (IList)Activator.CreateInstance(type)!;
            copies.Add(source, target);
            for (var i = 0; i < list.Count; i++)
            {
                var item = Copy(list[i], copies, depth + 1);
                if (type.IsArray) target[i] = item; else target.Add(item);
            }
            return target;
        }
        if (!string.Equals(type.Namespace, "BDVM.Domain", StringComparison.Ordinal))
            throw new InvalidOperationException("Runtime state contains an unsupported mutable type: " + type.FullName);
        var properties = Members.GetOrAdd(type, value => value.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(property => value.IsDefined(typeof(DataContractAttribute), false)
                ? property.IsDefined(typeof(DataMemberAttribute), false)
                : property.GetMethod?.IsPublic == true && property.SetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0).ToArray());
        var result = Activator.CreateInstance(type)!;
        copies.Add(source, result);
        foreach (var property in properties) property.SetValue(result, Copy(property.GetValue(source), copies, depth + 1));
        return result;
    }
    private sealed class Identity : IEqualityComparer<object>
    {
        public static readonly Identity Instance = new Identity();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }
}
