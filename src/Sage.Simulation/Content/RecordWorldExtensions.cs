#nullable enable
namespace sage_engine;

// Where a world's records live. Beside `world.Messages()` and `world.Debug()`, and public for the same
// reason: a game's own systems and screens ask the record store as often as the engine's do, and two
// spellings of "where the records live" is how they end up pointing at different stores.
public static class RecordWorldExtensions
{
    public static RecordStore Records(this World world) => world.Resources.Get<RecordStore>();
}
