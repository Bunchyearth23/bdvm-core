using System;

namespace BDVM.Adapters;

internal sealed class SaveGameAutomaticUpdateGate
{
    private object? manager;
    private object? data;
    private bool staged;

    public bool ShouldSkip(object currentManager, object currentData)
        => staged && ReferenceEquals(manager, currentManager) && ReferenceEquals(data, currentData);

    public void MarkStaged(object currentManager, object currentData)
    {
        manager = currentManager ?? throw new ArgumentNullException(nameof(currentManager));
        data = currentData ?? throw new ArgumentNullException(nameof(currentData));
        staged = true;
    }

    public void Reset()
    {
        manager = null;
        data = null;
        staged = false;
    }
}
