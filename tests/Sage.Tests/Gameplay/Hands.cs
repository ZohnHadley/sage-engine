#nullable enable
using System.Linq;

namespace Sage.Tests;

// Two hands to hold things in, for the base's item tests (issue #27). The base registers no equipment
// slots — the RPG kit's two hands are a kit default — so a test of base items that wields a sword
// registers the slots it names, as a game's module would in Init.
internal static class Hands
{
    public const string Main = "MainHand";
    public const string Off = "OffHand";

    public static HeadlessAppBuilder WithHands(this HeadlessAppBuilder builder) => builder.OnRegistered(app =>
    {
        var slots = app.Engine.Modules.Modules.OfType<ItemsModule>().Single().Slots;
        slots.Register(Main);
        slots.Register(Off);
    });
}
