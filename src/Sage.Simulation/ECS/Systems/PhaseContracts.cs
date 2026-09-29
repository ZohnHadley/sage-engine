#nullable enable
using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;

namespace sage_engine;

// What a phase *guarantees*, checked instead of described (docs/design/03 §3.5, TODO R16).
//
// `before:`/`after:` fixes the order systems run in. What it cannot say is the thing that actually
// matters downstream: "by the end of the Commands phase, PawnIntent is what this tick will act on".
// That kind of promise lived only in comments, and review #48 is what that costs — `AIThinkSystem`
// wrote intent in the AI phase while `CharacterMovementSystem` consumed it in PrePhysics, so every
// creature acted on last tick's decision. The comment next to it claimed the opposite. It survived a
// code review and a docs audit because both read the comment (engine review 2026-09-23, item 7).
//
// So a contract is declared:
//
//     world.Contracts.FinalAfter<PawnIntent>(Phase.Commands);
//
// and in a dev build the world takes a copy of every instance at the end of that phase and compares
// after each later phase of the tick. A write that was supposed to be too late to matter is reported
// with the entity and the phase that did it, which is where the comment used to be.
//
// It costs a copy per guarded component per entity per tick, and nothing at all in Shipping or when
// no contracts are declared. Guard the small components that other systems build on — an intent, a
// command — not a transform that half the engine writes by design.
public sealed class PhaseContracts
{
    private readonly World _world;
    private readonly List<IPhaseContract> _contracts = new();

    internal PhaseContracts(World world) => _world = world;

    public int Count => _contracts.Count;

    // How many times a contract has been broken in this world. A test can assert it is zero after
    // ticking, which is the point of the feature: the promise fails a build instead of a code review.
    public int Violations { get; private set; }

    // "Nothing writes T after `phase` this tick." Ignored outside dev builds.
    public void FinalAfter<T>(Phase phase) where T : struct, IComponent
    {
        if (!BuildInfo.IsDevBuild) return;
        foreach (var existing in _contracts)
            if (existing.Component == typeof(T))
            {
                Log.Warn(LogCat.World, $"A contract for {typeof(T).Name} is already declared; the later one is ignored");
                return;
            }
        _contracts.Add(new Contract<T>(phase));
    }

    // Called by the world after every Fixed phase. Snapshots when the guarded phase ends, compares
    // after each one that follows, and re-snapshots once it has reported so a single offender is
    // named once rather than by every phase after it.
    internal void AfterPhase(Phase phase)
    {
        for (int i = 0; i < _contracts.Count; i++) Violations += _contracts[i].AfterPhase(_world, phase);
    }

    private interface IPhaseContract
    {
        Type Component { get; }
        int AfterPhase(World world, Phase phase);
    }

    private sealed class Contract<T> : IPhaseContract where T : struct, IComponent
    {
        // A component under contract is compared after every phase, every tick, for every entity that
        // has one. `EqualityComparer<T>.Default` on a struct **without** `IEquatable<T>` falls back to
        // `ValueType.Equals(object)` and boxes both sides each time — about 3 KB per character per tick
        // when R18's scale test measured it. Cached here, and reported once, so the next component put
        // under a contract finds out from a warning rather than from a profiler.
        private static readonly EqualityComparer<T> Comparer = EqualityComparer<T>.Default;
        private static readonly bool Typed = typeof(IEquatable<T>).IsAssignableFrom(typeof(T));

        private readonly Phase _finalAfter;
        private readonly Dictionary<int, T> _snapshot = new();   // entity id → value as it was left
        private ArchetypeQuery<T>? _query;

        public Contract(Phase finalAfter)
        {
            _finalAfter = finalAfter;
            if (!Typed)
                Log.Once(LogCat.World, LogLevel.Warn, $"contract-boxing:{typeof(T).Name}",
                    $"{typeof(T).Name} is under a phase contract but does not implement IEquatable<{typeof(T).Name}>, " +
                    "so each check boxes it twice. Implement it: the check runs every phase, every tick (03 §3.5).");
        }

        public Type Component => typeof(T);

        public int AfterPhase(World world, Phase phase)
        {
            _query ??= world.Query<T>();

            if (phase == _finalAfter) { Take(); return 0; }
            if (phase < _finalAfter || phase >= PhaseInfo.FirstFrame) return 0;   // before it, or a frame phase

            return Compare(world, phase);
        }

        private void Take()
        {
            _snapshot.Clear();
            foreach (var entity in _query!.Entities)
                _snapshot[entity.Id] = entity.GetComponent<T>();
        }

        private int Compare(World world, Phase phase)
        {
            int broken = 0;
            foreach (var entity in _query!.Entities)
            {
                if (!_snapshot.TryGetValue(entity.Id, out var was)) continue;   // it only just appeared
                var now = entity.GetComponent<T>();
                if (Comparer.Equals(was, now)) continue;

                // Ensure, not Dev: a violated contract is a real bug but not a reason to stop the
                // game, and it is reported once per site so a broken tick does not drown the log.
                Assert.Ensure(false,
                    $"{typeof(T).Name} is declared final after {_finalAfter}, but {World.Describe(entity)} " +
                    $"had it changed during {phase}. Whatever writes it belongs in {_finalAfter} or earlier, " +
                    $"or the contract is wrong (03 §3.5).");
                broken++;
            }
            if (broken > 0) Take();   // the new values are the baseline now; name one offender, not all of them
            return broken;
        }
    }
}
