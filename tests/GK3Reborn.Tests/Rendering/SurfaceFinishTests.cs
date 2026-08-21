using System.Numerics;
using System.Text.Json;
using GK3Reborn.Content.Authoring;
using GK3Reborn.Content.Manifests;
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
            SurfaceFinishes finishes = SurfaceFinishes.Load(library, diagnostics);

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
}
