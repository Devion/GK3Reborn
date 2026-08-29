using System.Numerics;
using System.Text.Json;
using GK3Reborn.Content;
using GK3Reborn.Content.Authoring;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Rendering.Materials;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for what the renderer is told about a surface.
/// </summary>
/// <remarks>
/// The interesting behaviour is not the numbers themselves but which source wins: a
/// classifier's guess, a generated map's measurement, or a person's correction. Getting
/// that order wrong is what made every character's hair look like moulded plastic.
/// </remarks>
public sealed class SurfaceFinishTests
{
    private static MaterialDefinition Material(
        string id,
        float roughness,
        AuthoringProvenance provenance = AuthoringProvenance.Derived) =>
        new()
        {
            Id = id,
            BaseColorTexture = id,
            Roughness = roughness,
            Metallic = 0f,
            Provenance = provenance,
            Confidence = 0.3f,
        };

    private static MaterialLibrary Library(params MaterialDefinition[] materials) =>
        new()
        {
            SchemaVersion = 1,
            LibraryId = "test",
            Materials = materials,
        };

    [Fact]
    public void A_texture_nobody_measured_is_matte_and_not_authored()
    {
        SurfaceFinish finish = SurfaceFinishes.Empty.Of("ANYTHING");

        Assert.Equal(1f, finish.Roughness);
        Assert.False(finish.Authored);

        // Which is what switches the specular lobe off: a guess does not earn one, because
        // GK3's diffuse textures already have their highlights painted in.
        Assert.False(finish.Emits);
    }

    [Fact]
    public void A_derived_finish_is_not_authored_and_a_corrected_one_is()
    {
        SurfaceFinishes finishes = SurfaceFinishes.From(Library(
            Material("GUESSED", 0.44f),
            Material("CORRECTED", 0.75f, AuthoringProvenance.Edited)));

        Assert.False(finishes.Of("GUESSED").Authored);
        Assert.True(finishes.Of("CORRECTED").Authored);
    }

    [Fact]
    public void Corrections_beside_the_library_are_read_and_counted()
    {
        // The whole point of ADR 0006, and it was being written and never read: every
        // correction anybody made to a material did nothing at all.
        string directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);

        try
        {
            string library = Path.Combine(directory, "material-library.json");

            File.WriteAllText(
                library,
                JsonSerializer.Serialize(
                    Library(Material("GABE_HAIR", 0.44f)), ManifestJson.Options));

            File.WriteAllText(
                Path.Combine(directory, "material-library.materials.edits.json"),
                JsonSerializer.Serialize(
                    new MaterialEdits
                    {
                        SchemaVersion = 1,
                        LibraryId = "test",
                        Edits =
                        [
                            new Edit<MaterialDefinition, MaterialPatch>
                            {
                                Operation = EditOperation.Modify,
                                TargetId = "GABE_HAIR",
                                Patch = new MaterialPatch { Roughness = 0.75f },
                                Reason = "0.44 is a plastic sweep under an isotropic lobe.",
                            },
                        ],
                    },
                    ManifestJson.Options));

            var diagnostics = new DiagnosticBag();
            SurfaceFinishes finishes = SurfaceFinishes.Load(library, packs: null, diagnostics);

            Assert.Equal(0.75f, finishes.Of("GABE_HAIR").Roughness, 3);

            // And it says so, because a correction that silently failed to apply looks
            // exactly like no correction at all.
            Assert.Equal(1, finishes.Corrected);
            Assert.True(finishes.Of("GABE_HAIR").Authored);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_library_with_no_corrections_beside_it_loads_unchanged()
    {
        string directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);

        try
        {
            string library = Path.Combine(directory, "material-library.json");

            File.WriteAllText(
                library,
                JsonSerializer.Serialize(
                    Library(Material("WALL", 0.8f)), ManifestJson.Options));

            SurfaceFinishes finishes = SurfaceFinishes.Load(library);

            Assert.Equal(0.8f, finishes.Of("WALL").Roughness, 3);
            Assert.Equal(0, finishes.Corrected);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void An_emissive_material_does_not_stop_a_ray()
    {
        // The rig puts its emitters inside the fittings, because the 1999 bake never traced
        // a fitting against its own light. Tracing one now seals every lamp in its shade.
        SurfaceFinishes finishes = SurfaceFinishes.From(Library(
            Material("BULB", 0.5f) with { Emissive = new Vector3(1f, 0.9f, 0.7f) },
            Material("WALL", 0.9f)));

        Assert.False(finishes.Of("BULB").Occludes);
        Assert.True(finishes.Of("WALL").Occludes);
    }

    [Fact]
    public void Only_a_model_is_kept_out_of_its_own_shadow()
    {
        // GK3's people are a stack of overlapping shells — a shirt over a torso, arms
        // through sleeves — so a shadow ray leaving the shirt hits the arm inside it and
        // every character wore a dark patch across the chest. A ray leaving the room still
        // traces everything, so a character still lays a shadow on the floor.
        Assert.Equal(
            GK3Reborn.Rendering.Vulkan.RayTracingScene.WorldMask,
            GK3Reborn.Rendering.Vulkan.RayTracingScene.MaskFor(0));

        Assert.Equal(
            GK3Reborn.Rendering.Vulkan.RayTracingScene.ModelMask,
            GK3Reborn.Rendering.Vulkan.RayTracingScene.MaskFor(1));

        Assert.NotEqual(
            GK3Reborn.Rendering.Vulkan.RayTracingScene.WorldMask,
            GK3Reborn.Rendering.Vulkan.RayTracingScene.ModelMask);
    }

    /// <summary>Writes a library and its corrections into a pack, the way pack-content does.</summary>
    private static string PackedLibrary(string directory, MaterialLibrary library, string editsJson)
    {
        var builder = new RebarnBuilder();

        builder.AddBytes(
            RebarnKind.Manifest,
            "material-library.json",
            JsonSerializer.SerializeToUtf8Bytes(library, ManifestJson.Options),
            RebarnPayload.Json);

        builder.AddBytes(
            RebarnKind.Manifest,
            "material-library.materials.edits.json",
            System.Text.Encoding.UTF8.GetBytes(editsJson),
            RebarnPayload.Json);

        string path = Path.Combine(directory, "RebornMaterials.rebarn");
        builder.Write(path);

        return path;
    }

    private const string HairCorrection = """
        {
          "schemaVersion": 1,
          "libraryId": "test",
          "edits": [
            {
              "operation": "modify",
              "targetId": "GABE_HAIR",
              "patch": { "roughness": 0.75 },
              "reason": "a plastic highlight across the crown"
            }
          ]
        }
        """;

    [Fact]
    public void A_library_that_ships_only_in_a_pack_is_still_read()
    {
        // Nothing packed the library until 2026-08-29, and the gap was invisible because a
        // development checkout always has the loose file beside it. A player has only the
        // volumes, and a run against those alone got no library at all: every surface
        // matte, no specular lobe anywhere in the game, and no error, because a missing
        // library reads exactly like a checkout that never ran the material pass.
        string directory = Directory.CreateTempSubdirectory("gk3r-finishes").FullName;

        try
        {
            PackedLibrary(
                directory,
                Library(Material("GABE_HAIR", 0.42f), Material("WALL", 0.9f)),
                HairCorrection);

            using RebarnContent packs = RebarnContent.Open(directory);

            // A path to a file that is not there, which is exactly what a player has.
            SurfaceFinishes finishes = SurfaceFinishes.Load(
                Path.Combine(directory, "manifests", "material-library.json"), packs);

            Assert.Equal(2, finishes.Count);
            Assert.Equal(0.9f, finishes.Of("WALL").Roughness, 3);

            // And the corrections travel with it. A library shipped without them is the
            // classifier's first guess, which is the thing they exist to overrule.
            Assert.Equal(0.75f, finishes.Of("GABE_HAIR").Roughness, 3);
            Assert.Equal(1, finishes.Corrected);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void The_loose_library_beats_the_packed_one()
    {
        // Which is how every other enhanced set works and for the same reason: a roughness
        // corrected during a session has to reach the screen without the packs being
        // rebuilt first.
        string directory = Directory.CreateTempSubdirectory("gk3r-finishes").FullName;

        try
        {
            PackedLibrary(directory, Library(Material("WALL", 0.1f)), HairCorrection);

            string manifests = Directory.CreateDirectory(
                Path.Combine(directory, "manifests")).FullName;

            string loose = Path.Combine(manifests, "material-library.json");
            File.WriteAllText(loose, JsonSerializer.Serialize(
                Library(Material("WALL", 0.9f)), ManifestJson.Options));

            using RebarnContent packs = RebarnContent.Open(directory);

            Assert.Equal(0.9f, SurfaceFinishes.Load(loose, packs).Of("WALL").Roughness, 3);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Nothing_has_a_coat_unless_it_was_given_one()
    {
        // Shells are drawn by repeating a batch, so a surface that grows fur by accident
        // costs twelve extra draws of itself and looks like a hedgehog. Everything in the
        // game is bare until an edit says otherwise, including a texture nobody measured.
        SurfaceFinishes finishes = SurfaceFinishes.From(Library(
            Material("WALL", 0.9f),
            Material("CAT", 0.88f) with { Shells = 12, ShellDepth = 1.4f }));

        Assert.False(SurfaceFinishes.Empty.Of("ANYTHING").Furred);
        Assert.False(finishes.Of("WALL").Furred);
        Assert.True(finishes.Of("CAT").Furred);
    }

    [Fact]
    public void A_coat_with_no_depth_or_no_shells_is_no_coat()
    {
        // Both halves are needed and either alone is a mistake somebody made in the edit
        // layer: twelve shells at zero depth are twelve copies of the same surface fighting
        // each other for the depth buffer, and a depth with no shells is nothing at all.
        SurfaceFinishes finishes = SurfaceFinishes.From(Library(
            Material("FLAT", 0.9f) with { Shells = 12, ShellDepth = 0f },
            Material("EMPTY", 0.9f) with { Shells = 0, ShellDepth = 1.4f }));

        Assert.False(finishes.Of("FLAT").Furred);
        Assert.False(finishes.Of("EMPTY").Furred);
    }

    [Fact]
    public void A_coat_is_clamped_to_what_the_renderer_will_draw()
    {
        // Each shell is another draw of the whole batch and each unit of depth is another
        // unit the fur drifts from a limb that has animated under it, so both are capped
        // here rather than trusted from a hand-edited file.
        SurfaceFinishes finishes = SurfaceFinishes.From(Library(
            Material("SHAG", 0.9f) with { Shells = 500, ShellDepth = 100f }));

        Assert.Equal(SurfaceFinishes.MaximumShells, finishes.Of("SHAG").Shells);
        Assert.Equal(SurfaceFinishes.MaximumFur, finishes.Of("SHAG").ShellDepth);
    }

    [Fact]
    public void A_correction_can_give_something_a_coat()
    {
        // The classifier has no idea what an animal is — it called the cat *water*, at
        // roughness 0.10 — so fur can only ever arrive through the edit layer.
        MaterialDefinition cat = Material("CAT", 0.10f).ApplyPatch(new MaterialPatch
        {
            Roughness = 0.88f,
            Shells = 12,
            ShellDepth = 1.4f,
            ShellDensity = 160f,
        });

        Assert.Equal(0.88f, cat.Roughness);
        Assert.Equal(12, cat.Shells);
        Assert.Equal(1.4f, cat.ShellDepth);
        Assert.Equal(160f, cat.ShellDensity);

        // And a patch that says nothing about fur leaves the coat alone.
        MaterialDefinition again = cat.ApplyPatch(new MaterialPatch { Metallic = 0f });

        Assert.Equal(12, again.Shells);
        Assert.Equal(1.4f, again.ShellDepth);
    }
}
