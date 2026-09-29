#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Sage.Simulation;

// Work a system puts off until its query loop has finished (docs/design/03 §3.5, TODO R14).
//
// Touching another entity's components inside a query throws, so anything that reacts to what a loop
// found — a hit that applies an effect, an interaction that destroys a pickup, a death that tells the
// rules — has to be collected and run afterwards. Three features had each grown the same four lines
// to do it: a `List<T>` field, `Clear()` at the top of `Run`, `Add` in the loop, `foreach` after
// (engine review 2026-09-23, item 2).
//
// The four lines are not the problem; the `Clear()` is. Forget it and last tick's work runs again,
// which is a bug that looks like a gameplay bug. So this hands everything over and empties itself in
// one step: there is no clear to forget, and no way to drain the same work twice.
//
//     private readonly Deferred<Entity> _died = new();   // field
//     _died.Add(entity);                                 // inside the loop
//     foreach (var e in _died.Drain()) Die(world, e);    // after it
//
// Not `EntityCommands`: that one defers *structural* changes and the world plays them back at the end
// of the phase. This defers a system's own work, with its own data, to a point the system chooses.
public sealed class Deferred<T>
{
    private readonly List<T> _queued = new();
    private readonly List<T> _taken = new();

    public int Count => _queued.Count;
    public bool IsEmpty => _queued.Count == 0;

    public void Add(in T item) => _queued.Add(item);

    // Everything queued, with the queue left empty. Anything added while the caller is iterating the
    // result — work that defers more work — is queued for the *next* drain rather than appearing
    // underneath the loop, which is the difference between "runs later" and "runs forever".
    public ReadOnlySpan<T> Drain()
    {
        _taken.Clear();
        _taken.AddRange(_queued);
        _queued.Clear();
        return CollectionsMarshal.AsSpan(_taken);
    }
}
