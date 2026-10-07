using System.Linq;
using System.Text.Json.Nodes;

namespace SmithsGuild;

// The smiths_guild mod's code (phase 9, engine issue #396). A code mod's modules are plugins named for the mod
// (`smiths_guild`, or `smiths_guild.<name>`), so what they register is the mod's in the RegistrationLedger
// (`plugins smiths_guild`). It needs the items plugin, which the mods game loads: were it missing, the mod
// would be refused with that reason and the game would still boot.
[Plugin(Id, "1.0.0")]
[RequiresPlugin("sage", ">=0.1")]
[RequiresPlugin("sage.gameplay.items", ">=0.1")]
public sealed class SmithsGuildModule : IModule
{
    public const string Id = "smiths_guild";

    // Register only, as any module: the record type and the saved resource are declared below and registered
    // by the generated code before this runs.
    public void Init(ModuleContext ctx)
    {
        var engine = ctx.Engine;
        engine.CVars.RegisterCommand("guild_commission", CVarFlags.None,
            "guild_commission [n]: the guild takes n commissions (1 if not given), kept in the save.", a =>
            {
                int n = a.Count > 0 && int.TryParse(a[0], out int given) ? given : 1;
                var ledger = engine.Worlds[0].Resources.GetOrAdd(() => new GuildLedger());
                ledger.Commissions += n;
                Log.Info(LogCat.Console, $"The guild has {ledger.Commissions} commission(s); its orders: " +
                                         string.Join(", ", engine.Records.Ids("guild_order").Select(i => i.ToString()).OrderBy(i => i)));
            });
    }

    // Every world keeps the guild's ledger; a save replaces it.
    public void OnWorldCreated(World world) => world.Resources.GetOrAdd(() => new GuildLedger());
}

// A record type of the mod's own: `{ "type": "guild_order", ... }` in its data.
[Record("guild_order", Plugin = SmithsGuildModule.Id)]
public sealed class GuildOrder
{
    [Property(Tooltip = "What was ordered, and for whom")]
    public string Commission = "";

    [Property(Min = 0, Tooltip = "What the guild charges")]
    public int Price;
}

// The guild's ledger, saved with the world. Version 2 renamed `Orders` to `Commissions`: a version 1 save is
// upgraded on load. A save made with the mod and loaded without it keeps the ledger as it was (an unknown
// resource), so it is back when the mod is.
[SavedResource("guild_ledger", Plugin = SmithsGuildModule.Id, Version = 2)]
public sealed class GuildLedger
{
    public int Commissions { get; set; }

    [Upgrade(1)]
    private static void From1(ref JsonObject o) => o.RenameField("Orders", "Commissions");
}
