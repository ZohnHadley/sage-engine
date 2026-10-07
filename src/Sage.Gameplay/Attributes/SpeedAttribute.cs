#nullable enable
using System;

namespace Sage.Gameplay;

// Gameplay phase, after effects tick: a character's pace is an attribute (issue #384). The controller
// sits below gameplay and knows no attributes, so this writes the value of gameplay_conventions'
// `speedAttribute` into CharacterController.SpeedScale, which the next tick's move multiplies its walk
// and run speed by. A heavy pack (an encumbrance level's effect), a slowing spell or a sprint potion
// are all effects on that one attribute; a game that names none moves every character as its profile
// says.
[System("sage.attributes.speed", Phase.Gameplay, After = new[] { "sage.effects.tick" })]
internal sealed class SpeedAttributeSystem : ISystem
{
    // The slowest a character is paced: SpeedScale 0 means "unscaled", so an attribute at 0 stops it here.
    private const float Stopped = 0.0001f;

    private readonly Query<Attributes, CharacterController> _characters;
    private readonly GameplayRegistries _registries;

    public SpeedAttributeSystem(World world)
    {
        _characters = world.Query<Attributes, CharacterController>();
        _registries = world.Resources.Get<GameplayRegistries>();
    }

    public void Run(in SystemContext ctx)
    {
        var speed = ctx.World.Conventions().SpeedAttribute;
        int index = speed.IsEmpty ? -1 : _registries.Attribute(speed);
        if (index < 0) return;
        foreach (var (attributes, characters, _) in _characters.Chunks)
        {
            var a = attributes.Span;
            var c = characters.Span;
            for (int n = 0; n < a.Length; n++)
                c[n].SpeedScale = a[n].Values.Has(index) ? MathF.Max(a[n].Values[index], Stopped) : 0f;
        }
    }
}
