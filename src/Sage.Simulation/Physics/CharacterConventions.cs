#nullable enable

namespace Sage.Simulation;

// What a game means by "a character" when it says nothing (issue #26): the movement profile a
// character with none of its own moves by, and the names of the actions the controller reads.
//
// The words belong to the game's `gameplay_conventions` record, which lives in Sage.Gameplay, above
// this assembly. It sits in Sage.Simulation beside CharacterController (issue #30), so gameplay reads
// it without a physics backend and a 2D controller would read the same one. So this is the controller's view of them, one per world: CharacterModule installs
// the engine's own (no default profile, so `MovementProfileRecord.Fallback`, and the actions it
// registers), and gameplay replaces it with one that reads the record.
public class CharacterConventions
{
    private static readonly CharacterConventions Engine = new();

    // Empty: a character without a profile moves by MovementProfileRecord.Fallback.
    public virtual RecordId DefaultProfile => default;
    public virtual string JumpAction => "Jump";
    public virtual string RunAction => "Run";
    public virtual string CrouchAction => "Crouch";

    // The profile a character with `profile` moves by: its own, the default, or the built-in values.
    public MovementProfileRecord ProfileOf(RecordStore records, RecordId profile)
    {
        var id = profile.IsEmpty ? DefaultProfile : profile;
        return !id.IsEmpty && records.TryGet(id, out MovementProfileRecord found) ? found : MovementProfileRecord.Fallback;
    }

    // This world's, or the engine's when nothing installed one (a world without the character plugin).
    public static CharacterConventions Of(World world) =>
        world.Resources.TryGet<CharacterConventions>(out var conventions) && conventions != null ? conventions : Engine;
}
