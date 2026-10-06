#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Soft particles (issue 4n-6, docs/design/06 §3.12): a particle fades over its record's `soft` metres in
// front of the opaque geometry behind it, reading the scene's depth (`sage:depth`, the depth hook of issue
// #316). The fade, when it applies, the record field and the frame asking for the depth are headless
// (SoftParticles, Particles.AnySoft); sprite.fx's soft techniques repeat the arithmetic and compile only on
// Windows CI, so here their declarations are checked against the materials that draw with them.
public class SoftParticleTests
{
    public SoftParticleTests() { _ = TestEnv.UserRoot; }

    // z/w (Direct3D's 0..1) of a point `metres` along a perspective view: what the depth hook stores.
    private static float ZOverW(float metres, float near, float far) => far * (metres - near) / (metres * (far - near));

    // The fade: none touching the surface (or behind it), all of it `soft` metres in front, linear between;
    // soft 0 is hard. From the depth hook's z/w it is the same in metres, perspective or orthographic.
    [Fact]
    public void AParticleFadesOverItsSoftDistanceInFrontOfTheScene()
    {
        Assert.Equal(0f, SoftParticles.Fade(0f, 0.5f));
        Assert.Equal(0.5f, SoftParticles.Fade(0.25f, 0.5f), 5);
        Assert.Equal(1f, SoftParticles.Fade(2f, 0.5f));
        Assert.Equal(0f, SoftParticles.Fade(-1f, 0.5f));          // behind the wall: the depth test hides it anyway
        Assert.Equal(1f, SoftParticles.Fade(0f, 0f));             // hard: all of it, touching or not

        const float near = 0.1f, far = 500f;
        float wall = ZOverW(20f, near, far);
        Assert.Equal(0.5f, SoftParticles.Fade(wall, ZOverW(19.75f, near, far), near, far, orthographic: false, soft: 0.5f), 2);
        Assert.Equal(1f, SoftParticles.Fade(wall, ZOverW(10f, near, far), near, far, orthographic: false, soft: 0.5f), 4);
        Assert.Equal(0f, SoftParticles.Fade(wall, wall, near, far, orthographic: false, soft: 0.5f), 4);
        // The sky (z/w 1) is the far plane: a particle in front of it is whole.
        Assert.Equal(1f, SoftParticles.Fade(1f, ZOverW(50f, near, far), near, far, orthographic: false, soft: 0.5f), 4);
        // Orthographic depth is linear already: 1 m of a 100 m range is 0.01.
        Assert.Equal(0.5f, SoftParticles.Fade(0.5f, 0.495f, 0f, 100f, orthographic: true, soft: 1f), 3);

        // sprite.fx's SoftParams: 1 / soft, the planes, orthographic.
        Assert.Equal(new Vector4(2f, near, far, 0f), SoftParticles.Params(0.5f, near, far, orthographic: false));
        Assert.Equal(new Vector4(0f, 1f, 2f, 1f), SoftParticles.Params(0f, 1f, 2f, orthographic: true));
    }

    // Where a pixel reads the depth: the view's own rectangle of `sage:depth` (a split screen's right half,
    // a render scale's smaller scene), top to bottom as textures are.
    [Fact]
    public void APixelReadsTheDepthUnderItInItsViewsRectangle()
    {
        var right = SoftParticles.Rect(100, 0, 100, 100, 200, 100);
        Assert.Equal(new Vector4(0.5f, 0f, 0.5f, 1f), right);
        Assert.Equal(new Vector2(0.75f, 0.5f), SoftParticles.DepthUv(Vector2.Zero, right));          // the centre
        Assert.Equal(new Vector2(0.5f, 0f), SoftParticles.DepthUv(new Vector2(-1f, 1f), right));     // top left
        Assert.Equal(new Vector2(1f, 1f), SoftParticles.DepthUv(new Vector2(1f, -1f), right));       // bottom right
        Assert.Equal(new Vector4(0f, 0f, 1f, 1f), SoftParticles.Rect(0, 0, 640, 360, 640, 360));
    }

    // When it applies: a soft distance and a Transparent material (an alpha-tested one is in the depth it
    // would read). A frame draws the depth for a post effect or water, or for a soft particle on screen.
    [Fact]
    public void OnlyATransparentParticleIsSoft_AndASoftOneAsksForTheDepth()
    {
        Assert.True(SoftParticles.Applies(0.3f, RenderPass.Transparent));
        Assert.False(SoftParticles.Applies(0f, RenderPass.Transparent));
        Assert.False(SoftParticles.Applies(0.3f, RenderPass.AlphaTested));
        Assert.False(SoftParticles.Applies(0.3f, RenderPass.Opaque));

        Assert.False(SoftParticles.FrameNeedsDepth(postNeedsDepth: false, softSprites: 0));
        Assert.True(SoftParticles.FrameNeedsDepth(postNeedsDepth: false, softSprites: 1));
        Assert.True(SoftParticles.FrameNeedsDepth(postNeedsDepth: true, softSprites: 0));

        // Live particles: a soft one asks, a hard one does not, and a dead one stops asking.
        var world = HeadlessApp.Bare().Boot("soft").World;
        var particles = new Particles();
        particles.Emit(new RecordId("game", "sparks"), new ParticleRecord { LifeMin = 0.1f, LifeMax = 0.1f }, Vector3.Zero, Vector3.UnitY, 4);
        Assert.False(particles.AnySoft);
        particles.Emit(new RecordId("game", "smoke"), new ParticleRecord { Soft = 0.5f, LifeMin = 0.1f, LifeMax = 0.1f }, Vector3.Zero, Vector3.UnitY, 4);
        Assert.True(particles.AnySoft);
        for (int i = 0; i < 10; i++) particles.Update(world, 1f / 60f);
        Assert.Equal(0, particles.Live);
        Assert.False(particles.AnySoft);
    }

    // The record: `soft` loads in metres, 0 by default (hard: nothing changes), and a negative one is a
    // load error at its line.
    [Fact]
    public void SoftIsARecordFieldAndANegativeOneIsALoadError()
    {
        var files = new MountFixture();
        files.Write("game", "data/fx.json", """
        [
          { "type": "particle", "id": "hard" },
          { "type": "particle", "id": "smoke", "soft": 0.4 },
          { "type": "particle", "id": "inside_out", "soft": -0.5 }
        ]
        """);
        files.Mount("game", "game");
        using var log = new CaptureSink();
        using var app = HeadlessApp.Simulation().Mount(files)
            .OnRegistered(a => a.Records.Register<ParticleRecord>()).Boot();
        Assert.Equal(1, app.Records.ErrorCount);
        Assert.Contains(log.Entries, e => e.Message.Contains("inside_out") && e.Message.Contains("\"soft\""));
        Assert.Equal(0f, app.Records.Get<ParticleRecord>(new RecordId("game", "hard")).Soft);
        Assert.Equal(0.4f, app.Records.Get<ParticleRecord>(new RecordId("game", "smoke")).Soft);
    }

    // The shader side, as far as it can be checked without mgfxc: every transparent sprite material the
    // engine ships draws with a technique whose soft twin (and that twin's instanced twin) sprite.fx
    // declares, the soft pixel shader reads the depth and the fade, and the Sandbox's fire burst is soft
    // with a material that has one.
    [Fact]
    public void TheEnginesParticleMaterialsHaveSoftTwins()
    {
        string shaders = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content", "shaders");
        string sprite = File.ReadAllText(Path.Combine(shaders, "sprite.fx"));
        var declared = Regex.Matches(sprite, @"^technique\s+(\w+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Equal("UnlitBlendSoft", SoftParticles.TechniqueFor("UnlitBlend"));
        foreach (var name in new[] { "SceneDepth", "SoftParams", "SoftRect" })
            Assert.Matches(new Regex(@"^(texture|float4) " + name + ";", RegexOptions.Multiline), sprite);

        var fx = new MountFixture();
        fx.Write("engine", "data/materials.json", File.ReadAllText(Path.Combine(shaders, "..", "data", "materials.json")));
        fx.Mount("engine", "sage");
        var store = new RecordStore();
        store.Register<MaterialRecord>();
        store.Load(fx.Vfs);
        Assert.Equal(0, store.ErrorCount);
        int checkedMaterials = 0;
        foreach (var id in store.Ids("material"))
        {
            var material = store.Get<MaterialRecord>(id);
            if (material.Pass != RenderPass.Transparent || !material.Effect.ToString().EndsWith("sprite.mgfxo", StringComparison.Ordinal)) continue;
            Assert.Contains(SoftParticles.TechniqueFor(material.Technique), declared);
            Assert.Contains(Instancing.TechniqueFor(SoftParticles.TechniqueFor(material.Technique)), declared);
            checkedMaterials++;
        }
        Assert.True(checkedMaterials >= 2, "particle_additive and particle_blend draw with sprite.fx");

        using var app = HeadlessApp.ForGame(Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox"), new global::Sandbox.SandboxModule())
            .WithEngineContent()
            .OnRegistered(a => a.Records.Register<ParticleRecord>())
            .Boot();
        var burst = app.Records.Get<ParticleRecord>(new RecordId("sandbox", "fire_burst"));
        Assert.True(burst.Soft > 0f);
        Assert.True(burst.Material.IsEmpty);                     // the engine's additive particle material
        var look = store.Get<MaterialRecord>(ParticleRecord.DefaultMaterial);
        Assert.True(SoftParticles.Applies(burst.Soft, look.Pass));
    }
}
