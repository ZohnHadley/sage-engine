#nullable enable

namespace Sage.Gameplay;

// Haptics from data (issue #331). The same two events the decals and particles read, turned into rumble on
// the pad of the player they happened to:
//   - a cue whose record names a `rumble` makes the player who is the cue's *source* feel it (a spell they
//     cast, a footstep of theirs, a swing);
//   - a hit that cost a player health makes them feel its damage type's `rumble`.
// An entity that is not a player's pawn feels nothing, so a creature's swing does not buzz a pad.
//
// The effects go into the world's `RumbleMixer` (Sage.Simulation), which sums them per player and ages
// them here, at the tick rate; the client reads the motors each frame. It runs only in a world that has a
// mixer (the client adds one, and so may a test); a headless server drains its events into nothing.
[System("sage.combat.rumble", Phase.Late)]
internal sealed class RumbleSystem : ISystem
{
    private readonly RecordStore _records;
    private readonly EventReader<CueTriggered> _cues;
    private readonly EventReader<Damaged> _damage;
    private readonly bool _typed;

    public RumbleSystem(World world, RecordStore records)
    {
        _records = records;
        _cues = world.Events.Reader<CueTriggered>(this);
        _damage = world.Events.Reader<Damaged>(this);
        _typed = records.TypeNameOf(typeof(RumbleRecord)) != null;
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        if (!world.Resources.TryGet<RumbleMixer>(out var mixer) || mixer == null || !_typed)
        {
            _cues.Read();      // drained anyway: a reader that stops reading holds the queue open (04 §3.1)
            _damage.Read();
            return;
        }

        foreach (ref readonly var cue in _cues.Read())
        {
            if (!_records.TryGet(cue.Cue, out CueRecord record) || record.Rumble.IsEmpty) continue;
            int player = world.PlayerIndexOf(cue.Source);
            if (player >= 0 && _records.TryGet(record.Rumble.Id, out RumbleRecord rumble)) mixer.Start(player, rumble);
        }

        foreach (ref readonly var hit in _damage.Read())
        {
            if (hit.Applied <= 0f) continue;
            var typeId = hit.Hit.Type.IsEmpty ? GameplayConventions.Of(_records).DamageType.Id : hit.Hit.Type;
            if (typeId.IsEmpty || !_records.TryGet(typeId, out DamageTypeRecord type) || type.Rumble.IsEmpty) continue;
            int player = world.PlayerIndexOf(hit.Hit.Target);
            if (player >= 0 && _records.TryGet(type.Rumble.Id, out RumbleRecord rumble)) mixer.Start(player, rumble);
        }

        mixer.Update(ctx.Tick.Dt);
    }
}
