#nullable enable
using System;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

#pragma warning disable SAGE0134 // the joint part is what these test

// The `joint` prefab part (issue #245, phase 4k): a sign hung from a beam, a crate on a rope, a joint
// that breaks, and a swing that a save keeps swinging.
public class JointPartTests
{
    public JointPartTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;
    private const float Degree = MathF.PI / 180f;

    private const string Content = """
    [
      // A beam to hang things from, 2 m long, its underside at y = 4.9.
      { "type": "prefab", "id": "beam", "name": "beam", "parts": { "body": { "size": [2, 0.2, 0.2] } } },
      // A sign hinged along its top edge, a little in front of the beam so the two never touch.
      { "type": "prefab", "id": "sign", "name": "sign", "parts": {
          "body": { "size": [1, 0.5, 0.06], "mass": 3 },
          "joint": { "kind": "Hinge", "target": "beam", "anchor": [0, 0.25, 0], "axis": [1, 0, 0] } } },
      // A rope in two lengths of a metre each: a knot, then a crate.
      { "type": "prefab", "id": "knot", "name": "knot", "parts": {
          "body": { "shape": "Sphere", "radius": 0.05, "mass": 0.2 },
          "joint": { "kind": "Distance", "target": "beam", "targetAnchor": [0.5, -0.1, 0], "maxDistance": 1, "drag": 0 } } },
      { "type": "prefab", "id": "crate", "name": "crate", "parts": {
          "body": { "size": [0.6, 0.6, 0.6], "mass": 8 },
          "joint": { "kind": "Distance", "target": "knot", "anchor": [0, 0.3, 0], "targetAnchor": [0, 0, 0], "maxDistance": 1, "drag": 0 } } },
      // A crate on a rope that cannot take its weight.
      { "type": "prefab", "id": "weak_crate", "name": "weak_crate", "parts": {
          "body": { "size": [0.6, 0.6, 0.6], "mass": 8 },
          "joint": { "kind": "Distance", "target": "beam", "anchor": [0, 0.3, 0], "targetAnchor": [-0.5, -0.1, 0], "maxDistance": 1, "breakForce": 40, "drag": 0 } } },
      { "type": "prefab", "id": "counter", "name": "counter", "parts": { "logic_counter": { "max": 100 } } },
      { "type": "prefab", "id": "strong_crate", "name": "strong_crate", "parts": {
          "body": { "size": [0.6, 0.6, 0.6], "mass": 8 },
          "joint": { "kind": "Distance", "target": "beam", "anchor": [0, 0.3, 0], "targetAnchor": [-0.5, -0.1, 0], "maxDistance": 1, "drag": 0 } } },

      { "type": "prefab", "id": "backwards", "name": "backwards", "parts": {
          "body": { "size": [1, 1, 1], "mass": 1 },
          "joint": { "kind": "Hinge", "min": 40, "max": -40 } } },
      { "type": "scene", "id": "backwards_scene", "place": [ { "prefab": "backwards", "at": [0, 5, 0], "name": "backwards" } ] },

      { "type": "scene", "id": "sign_scene", "place": [
          { "prefab": "beam", "at": [0, 5, 0], "name": "beam" },
          { "prefab": "sign", "at": [0, 4.55, 0.25], "name": "sign" } ] },
      { "type": "scene", "id": "rope_scene", "place": [
          { "prefab": "beam", "at": [0, 5, 0], "name": "beam" },
          { "prefab": "knot", "at": [0.5, 3.9, 0], "name": "knot" },
          { "prefab": "crate", "at": [0.5, 2.6, 0], "name": "crate" } ] },
      { "type": "scene", "id": "strong_scene", "place": [
          { "prefab": "beam", "at": [0, 5, 0], "name": "beam" },
          { "prefab": "counter", "at": [9, 0, 0], "name": "breaks" },
          { "prefab": "strong_crate", "at": [-0.5, 3.4, 0], "name": "crate",
            "outputs": [ { "output": "OnBreak", "target": "breaks", "input": "Add" } ] } ] },
      { "type": "scene", "id": "weak_scene", "place": [
          { "prefab": "beam", "at": [0, 5, 0], "name": "beam" },
          { "prefab": "counter", "at": [9, 0, 0], "name": "breaks" },
          { "prefab": "weak_crate", "at": [-0.5, 3.4, 0], "name": "crate",
            "outputs": [ { "output": "OnBreak", "target": "breaks", "input": "Add" } ] } ] }
    ]
    """;

    private static HeadlessApp Boot(string scene)
    {
        var files = new MountFixture();
        files.Write("game", "data/content.json", Content);
        files.Mount("game", "game");
        var app = HeadlessApp.Gameplay().Mount(files).StartScene("game:" + scene).Boot();
        app.Engine.Saves.Root = System.IO.Path.Combine(TestEnv.NewTempDir(), "saves");
        app.World.RunFixed(Dt);
        return app;
    }

    private static void Run(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(Dt);
    }

    // The sign's angle about its hinge (+X), in degrees.
    private static float Angle(World world, Entity entity)
    {
        var q = world.Get<Transform>(entity).LocalRotation;
        float angle = 2f * MathF.Atan2(q.X, q.W);
        if (angle > MathF.PI) angle -= 2f * MathF.PI;
        if (angle < -MathF.PI) angle += 2f * MathF.PI;
        return angle / Degree;
    }

    private static void Shove(World world, Entity sign)
    {
        var physics = world.Resources.Get<IPhysicsWorld>();
        var body = world.Get<PhysicsBody>(sign);
        physics.ApplyImpulse(body, new Vector3(0, 0, 4), physics.PoseOf(body).Position - new Vector3(0, 0.2f, 0));
    }

    private static float Count(World world) => world.Get<LogicCounter>(world.FindByName("breaks")).Value;

    // A sign hung from a beam by a hinge part, shoved: it swings well out, and with a little drag it
    // settles hanging straight down, and goes to sleep.
    [Fact]
    public void ASignHingedToABeamSwingsAndSettles()
    {
        using var app = Boot("sign_scene");
        var world = app.World;
        var sign = world.FindByName("sign");
        Assert.True(world.Has<Joint>(sign));
        Run(world, 5);
        Assert.Equal(1, world.Resources.Get<IPhysicsWorld>().JointCount);
        Assert.True(MathF.Abs(Angle(world, sign)) < 1f, "it hangs still until it is touched");

        Shove(world, sign);
        float widest = 0f;
        for (int i = 0; i < 90; i++)
        {
            Run(world, 1);
            widest = MathF.Max(widest, MathF.Abs(Angle(world, sign)));
            // It turns about the hinge: the top edge stays where it was hung.
            var top = world.Get<Transform>(sign).LocalPosition + Vector3.Transform(new Vector3(0, 0.25f, 0), world.Get<Transform>(sign).LocalRotation);
            Assert.True(Vector3.Distance(top, new Vector3(0, 4.8f, 0.25f)) < 0.05f, $"the hinge held: top edge at {top}");
        }
        Assert.True(widest > 15f, $"it swung out to {widest} degrees");

        Run(world, 60 * 40);
        Assert.True(MathF.Abs(Angle(world, sign)) < 1f, $"it settled hanging down: {Angle(world, sign)} degrees");
        Assert.False(world.Resources.Get<IPhysicsWorld>().IsAwake(world.Get<PhysicsBody>(sign)), "and went to sleep");
    }

    // Two distance joints in a row hold a crate: it falls until the rope is taut, then hangs.
    [Fact]
    public void ARopeOfDistanceJointsHoldsACrate()
    {
        using var app = Boot("rope_scene");
        var world = app.World;
        var crate = world.FindByName("crate");
        var knot = world.FindByName("knot");
        Run(world, 60 * 4);
        float y = world.Get<Transform>(crate).LocalPosition.Y;
        Run(world, 60 * 2);
        Assert.Equal(y, world.Get<Transform>(crate).LocalPosition.Y, 0.05f);

        // Two metres of rope below the beam's underside (4.9), then the crate hangs from its top.
        float lowest = 4.9f - 2f - 0.3f;
        Assert.InRange(world.Get<Transform>(crate).LocalPosition.Y, lowest - 0.15f, lowest + 0.05f);
        var knotAt = world.Get<Transform>(knot).LocalPosition;
        Assert.InRange(Vector3.Distance(knotAt, new Vector3(0.5f, 4.9f, 0)), 0.9f, 1.1f);
        Assert.Equal(2, world.Resources.Get<IPhysicsWorld>().JointCount);
    }

    // A rope that cannot bear the crate breaks as it comes taut, and says so once.
    [Fact]
    public void ABreakForceBreaksTheJointAndFiresOnBreakOnce()
    {
        using var app = Boot("weak_scene");
        var world = app.World;
        var crate = world.FindByName("crate");
        Run(world, 60 * 3);
        Assert.Equal(1f, Count(world));
        Assert.True(world.Get<Joint>(crate).Broken);
        Assert.Equal(0, world.Resources.Get<IPhysicsWorld>().JointCount);
        Run(world, 60 * 3);
        Assert.Equal(1f, Count(world));
        Assert.True(world.Get<Transform>(crate).LocalPosition.Y < 0f, "and the crate fell");
    }

    // The Break input takes the joint away and fires OnBreak, once however often it is sent.
    [Fact]
    public void TheBreakInputRemovesTheJointAndFiresOnBreakOnce()
    {
        using var app = Boot("strong_scene");
        var world = app.World;
        var crate = world.FindByName("crate");
        Run(world, 60 * 3);
        Assert.False(world.Get<Joint>(crate).Broken);
        Assert.Equal(1, world.Resources.Get<IPhysicsWorld>().JointCount);
        Assert.Equal(0f, Count(world));

        world.IO().FireInput(crate, PhysicsJointIO.Break);
        world.IO().FireInput(crate, PhysicsJointIO.Break);
        Run(world, 5);
        Assert.True(world.Get<Joint>(crate).Broken);
        Assert.Equal(0, world.Resources.Get<IPhysicsWorld>().JointCount);
        Assert.Equal(1f, Count(world));
        Run(world, 60);
        Assert.Equal(1f, Count(world));
        Assert.True(world.Get<Transform>(crate).LocalPosition.Y < 0f, "the crate dropped");
    }

    // A save mid-swing, loaded into a fresh app, swings on as the uninterrupted one did: the body's
    // velocity is saved, and the joint is made again as it was.
    [Fact]
    public void ASaveMidSwingResumesTheSwingInAFreshApp()
    {
        string saves;
        float expected, atSave;
        {
            using var app = Boot("sign_scene");
            var world = app.World;
            var sign = world.FindByName("sign");
            Run(world, 5);
            Shove(world, sign);
            Run(world, 5);
            atSave = Angle(world, sign);
            Assert.True(world.Get<BodyMotion>(sign).Angular.Length() > 0.5f, $"mid-swing: turning at {world.Get<BodyMotion>(sign).Angular}");
            Assert.True(app.Engine.Saves.Save("swing"));
            saves = app.Engine.Saves.Root;

            Run(world, 12);
            expected = Angle(world, sign);
        }
        Assert.True(MathF.Abs(expected - atSave) > 5f, $"the swing moves on in 12 ticks: {atSave} to {expected}");

        using var fresh = Boot("sign_scene");
        fresh.Engine.Saves.Root = saves;
        Assert.True(fresh.Engine.Saves.Load("swing"));
        var loaded = fresh.World;
        var again = loaded.FindByName("sign");
        Assert.Equal(atSave, Angle(loaded, again), 0.5f);
        Run(loaded, 12);
        Assert.Equal(expected, Angle(loaded, again), 3f);
        Assert.Equal(1, loaded.Resources.Get<IPhysicsWorld>().JointCount);
    }

    // A broken joint stays broken through a save and a load.
    [Fact]
    public void ABrokenJointStaysBrokenAcrossASaveAndLoad()
    {
        using var app = Boot("weak_scene");
        var world = app.World;
        Run(world, 60 * 3);
        Assert.Equal(1f, Count(world));
        Assert.True(app.Engine.Saves.Save("broken"));
        string saves = app.Engine.Saves.Root;

        using var fresh = Boot("weak_scene");
        fresh.Engine.Saves.Root = saves;
        Assert.True(fresh.Engine.Saves.Load("broken"));
        var loaded = fresh.World;
        Run(loaded, 30);
        Assert.True(loaded.Get<Joint>(loaded.FindByName("crate")).Broken);
        Assert.Equal(0, loaded.Resources.Get<IPhysicsWorld>().JointCount);
        Assert.Equal(1f, Count(loaded));
    }

    // Only a dynamic body can have a joint: one on a body-less entity is an error in the log, and it is
    // never made.
    [Fact]
    public void AJointOnAnEntityWithNoDynamicBodyIsNeverMade()
    {
        using var app = Boot("sign_scene");
        var world = app.World;
        var physics = world.Resources.Get<IPhysicsWorld>();
        var beam = world.FindByName("beam");
        world.Add(beam, new Joint { Kind = JointKind.Ball, Target = "sign" });
        Run(world, 5);
        Assert.Equal(1, physics.JointCount);   // the sign's own, not the static beam's
    }

    // A hinge with its range the wrong way round is said so, and gets no joint.
    [Fact]
    public void AJointPartWithItsLimitsTheWrongWayRoundIsAnError()
    {
        using var capture = new CaptureSink();
        using var app = Boot("backwards_scene");
        var backwards = app.World.FindByName("backwards");
        Assert.False(app.World.Has<Joint>(backwards));
        Assert.Contains(capture.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("\"min\" (40) is above its \"max\" (-40)"));
    }
}
