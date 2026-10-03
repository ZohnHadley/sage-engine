#nullable enable
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Gameplay's words for a data-only game (issue #275): `has_tag` and `lacks_tag` on any entity, `set_tag`,
// `is_alive` and `cue`. Registered by the plugins that own tags, death and cues.
public class GameplayWordTests
{
    public GameplayWordTests() { _ = TestEnv.UserRoot; }

    private static RecordId Id(string name) => new("sage", name);

    private const string Content = """
        [{ "type": "tag", "id": "alerted" },
         { "type": "cue", "id": "bell_toll" },
         { "type": "gate_rule", "id": "guard_alerted", "requires": { "has_tag": "alerted", "entity": "guard" } },
         { "type": "gate_rule", "id": "other_calm", "requires": { "lacks_tag": "alerted", "entity": "!other" } },
         { "type": "gate_rule", "id": "me_alerted", "requires": { "has_tag": "alerted" } },
         { "type": "gate_rule", "id": "guard_alive", "requires": { "is_alive": "guard" } },
         { "type": "gate_rule", "id": "alarm", "then": [ { "set_tag": "alerted", "target": "guard" } ] },
         { "type": "gate_rule", "id": "calm", "then": [ { "set_tag": "alerted", "target": "!other", "on": false } ] },
         { "type": "gate_rule", "id": "toll", "then": [ { "cue": "bell_toll", "at": "guard" } ] }]
        """;

    private static bool Holds(HeadlessApp app, string rule, Entity subject = default, Entity other = default) =>
        Conditions.Evaluate(app.World, subject, app.Records.Get<GateRule>(Id(rule)).Requires, other);

    private static void Do(HeadlessApp app, string rule, Entity subject = default, Entity other = default) =>
        Conditions.Run(app.World, subject, app.Records.Get<GateRule>(Id(rule)).Then, other);

    [Fact]
    public void TagsAliveAndCuesWorkOnAnyEntity()
    {
        using var app = HeadlessApp.Gameplay().WithEngineContent().With(new LogicTestPlugin()).File("data/words.json", Content).Boot("words");
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        var owners = app.Engine.Vocabularies.All.SelectMany(v => v.Entries.Select(e => (v.Name, e.Id, e.Owner))).ToList();
        Assert.Contains(("condition", "is_alive", "sage.gameplay.attributes"), owners);
        Assert.Contains(("action", "set_tag", "sage.gameplay.attributes"), owners);
        Assert.Contains(("action", "cue", "sage.gameplay.abilities"), owners);

        var player = world.Create(Transform.At(Vector3.Zero), "player");
        var guard = world.Create(Transform.At(new Vector3(1, 2, 3)), "guard");
        var reader = world.Events.Reader<CueTriggered>(this, Schedule.Fixed);

        // The guard, asked about by name and as `!other`; the subject is not the guard.
        Assert.False(Holds(app, "guard_alerted", player));
        Assert.True(Holds(app, "other_calm", player, guard));
        Do(app, "alarm", player);
        Assert.True(Holds(app, "guard_alerted", player));
        Assert.False(Holds(app, "other_calm", player, guard));
        Assert.False(Holds(app, "me_alerted", player));            // without `entity`, the subject
        Do(app, "calm", player, guard);
        Assert.False(Holds(app, "guard_alerted", player));

        // Alive while there and not dead; the conventions' dead tag, or gone, is not.
        Assert.True(Holds(app, "guard_alive"));
        var dead = world.Conventions().Dead;
        Assert.False(dead.IsEmpty);
        world.AddTag(guard, dead);
        Assert.False(Holds(app, "guard_alive"));
        world.RemoveTag(guard, dead);
        Assert.True(Holds(app, "guard_alive"));
        world.Destroy(guard);
        Assert.False(Holds(app, "guard_alive"));

        // A cue at an entity.
        guard = world.Create(Transform.At(new Vector3(1, 2, 3)), "guard");
        Do(app, "toll", player);
        int cues = 0;
        foreach (ref readonly var cue in reader.Read())
        {
            Assert.Equal((Id("bell_toll"), guard, new Vector3(1, 2, 3)), (cue.Cue, cue.Source, cue.Point));
            cues++;
        }
        Assert.Equal(1, cues);
    }
}
