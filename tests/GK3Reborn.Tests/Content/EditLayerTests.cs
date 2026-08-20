using System.Numerics;
using System.Text.Json;
using GK3Reborn.Content.Authoring;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Rendering.Lighting;
using GK3Reborn.Rendering.Materials;
using Xunit;

namespace GK3Reborn.Tests.Content;

public sealed class EditLayerTests
{
    private static SceneLight Light(string id, float intensity = 1000f) => new()
    {
        Id = id,
        Kind = SceneLightKind.Point,
        Position = new Vector3(1, 2, 3),
        Color = new Vector3(1f, 0.9f, 0.8f),
        Intensity = intensity,
        Radius = 500f,
        Provenance = AuthoringProvenance.Derived,
        Confidence = 0.6f,
        ReviewNote = "clustered from lightmap maximum",
    };

    private static SceneLightRig Rig(params SceneLight[] lights) => new()
    {
        SchemaVersion = 1,
        SceneId = "LBY",
        Lights = lights,
        SignedOff = false,
    };

    private static SceneLightEdits Edits(params Edit<SceneLight, SceneLightPatch>[] edits) => new()
    {
        SchemaVersion = 1,
        SceneId = "LBY",
        Edits = edits,
    };

    [Fact]
    public void A_light_can_be_added()
    {
        var diagnostics = new DiagnosticBag();

        SceneLightRig effective = Rig(Light("derived-0")).WithEdits(
            Edits(new Edit<SceneLight, SceneLightPatch>
            {
                Operation = EditOperation.Add,
                TargetId = "hand-1",
                Item = Light("hand-1") with { Provenance = AuthoringProvenance.Authored },
                Reason = "chandelier the lightmap missed",
            }),
            diagnostics);

        Assert.Equal(2, effective.Lights.Count);
        Assert.Equal("hand-1", effective.Lights[1].Id);
        Assert.Equal(AuthoringProvenance.Authored, effective.Lights[1].Provenance);
        Assert.Empty(diagnostics.Items);
    }

    [Fact]
    public void A_light_can_be_removed()
    {
        var diagnostics = new DiagnosticBag();

        SceneLightRig effective = Rig(Light("derived-0"), Light("derived-1")).WithEdits(
            Edits(new Edit<SceneLight, SceneLightPatch>
            {
                Operation = EditOperation.Remove,
                TargetId = "derived-0",
                Reason = "false positive from a specular highlight",
            }),
            diagnostics);

        Assert.Equal(["derived-1"], effective.Lights.Select(l => l.Id));
        Assert.Empty(diagnostics.Items);
    }

    [Fact]
    public void A_patch_changes_only_the_fields_it_sets()
    {
        var diagnostics = new DiagnosticBag();

        SceneLightRig effective = Rig(Light("derived-0")).WithEdits(
            Edits(new Edit<SceneLight, SceneLightPatch>
            {
                Operation = EditOperation.Modify,
                TargetId = "derived-0",
                Patch = new SceneLightPatch { Intensity = 2500f, ReviewNote = "too dim for the new range" },
            }),
            diagnostics);

        SceneLight light = Assert.Single(effective.Lights);
        Assert.Equal(2500f, light.Intensity);
        Assert.Equal("too dim for the new range", light.ReviewNote);

        // Untouched fields survive.
        Assert.Equal(new Vector3(1, 2, 3), light.Position);
        Assert.Equal(500f, light.Radius);
        Assert.Equal(SceneLightKind.Point, light.Kind);

        // And the light is now marked as hand-corrected.
        Assert.Equal(AuthoringProvenance.Edited, light.Provenance);
        Assert.Empty(diagnostics.Items);
    }

    [Fact]
    public void Corrections_survive_the_generator_producing_a_different_baseline()
    {
        // This is the whole point: C4b can be improved and rerun without destroying
        // anyone's work. Same edits, a baseline whose values have all changed.
        var edits = Edits(
            new Edit<SceneLight, SceneLightPatch>
            {
                Operation = EditOperation.Remove,
                TargetId = "derived-0",
            },
            new Edit<SceneLight, SceneLightPatch>
            {
                Operation = EditOperation.Modify,
                TargetId = "derived-1",
                Patch = new SceneLightPatch { Intensity = 2500f },
            },
            new Edit<SceneLight, SceneLightPatch>
            {
                Operation = EditOperation.Add,
                TargetId = "hand-1",
                Item = Light("hand-1") with { Provenance = AuthoringProvenance.Authored },
            });

        var first = new DiagnosticBag();
        SceneLightRig before = Rig(Light("derived-0"), Light("derived-1", 800f)).WithEdits(edits, first);

        var second = new DiagnosticBag();
        SceneLightRig after = Rig(Light("derived-0"), Light("derived-1", 1234f)).WithEdits(edits, second);

        Assert.Equal(["derived-1", "hand-1"], before.Lights.Select(l => l.Id));
        Assert.Equal(["derived-1", "hand-1"], after.Lights.Select(l => l.Id));
        Assert.Equal(2500f, after.Lights[0].Intensity);
        Assert.Empty(first.Items);
        Assert.Empty(second.Items);
    }

    [Fact]
    public void A_stale_edit_warns_and_the_rest_still_apply()
    {
        var diagnostics = new DiagnosticBag();

        SceneLightRig effective = Rig(Light("derived-1")).WithEdits(
            Edits(
                new Edit<SceneLight, SceneLightPatch>
                {
                    Operation = EditOperation.Modify,
                    TargetId = "derived-0",
                    Patch = new SceneLightPatch { Intensity = 10f },
                },
                new Edit<SceneLight, SceneLightPatch>
                {
                    Operation = EditOperation.Modify,
                    TargetId = "derived-1",
                    Patch = new SceneLightPatch { Intensity = 99f },
                }),
            diagnostics);

        Assert.Equal(99f, Assert.Single(effective.Lights).Intensity);

        Diagnostic warning = Assert.Single(diagnostics.Items);
        Assert.Equal("GK3R3003", warning.Code);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.False(diagnostics.HasErrors);
        Assert.NotNull(warning.Remediation);
    }

    [Fact]
    public void Adding_an_id_the_generator_now_produces_warns()
    {
        var diagnostics = new DiagnosticBag();

        SceneLightRig effective = Rig(Light("derived-0")).WithEdits(
            Edits(new Edit<SceneLight, SceneLightPatch>
            {
                Operation = EditOperation.Add,
                TargetId = "derived-0",
                Item = Light("derived-0"),
            }),
            diagnostics);

        Assert.Single(effective.Lights);
        Assert.Equal("GK3R3001", Assert.Single(diagnostics.Items).Code);
    }

    [Fact]
    public void An_incomplete_edit_warns_rather_than_throwing()
    {
        var diagnostics = new DiagnosticBag();

        Rig(Light("derived-0")).WithEdits(
            Edits(
                new Edit<SceneLight, SceneLightPatch> { Operation = EditOperation.Add, TargetId = "x" },
                new Edit<SceneLight, SceneLightPatch> { Operation = EditOperation.Modify, TargetId = "derived-0" }),
            diagnostics);

        Assert.Equal(["GK3R3002", "GK3R3004"], diagnostics.Items.Select(d => d.Code));
    }

    [Fact]
    public void A_rig_with_no_edits_is_returned_unchanged()
    {
        var diagnostics = new DiagnosticBag();
        SceneLightRig rig = Rig(Light("derived-0"));

        Assert.Same(rig, rig.WithEdits(null, diagnostics));
        Assert.Same(rig, rig.WithEdits(Edits(), diagnostics));
    }

    [Fact]
    public void Material_channels_are_correctable_the_same_way()
    {
        var diagnostics = new DiagnosticBag();

        var library = new MaterialLibrary
        {
            SchemaVersion = 1,
            LibraryId = "LBY",
            Materials =
            [
                new MaterialDefinition
                {
                    Id = "STONEFLOOR",
                    BaseColorTexture = "STONEFLOOR",
                    Roughness = 0.35f,
                    Metallic = 0f,
                    Provenance = AuthoringProvenance.Derived,
                    Confidence = 0.4f,
                    ReviewNote = "inferred from texture value histogram",
                },
            ],
        };

        MaterialLibrary effective = library.WithEdits(
            new MaterialEdits
            {
                SchemaVersion = 1,
                LibraryId = "LBY",
                Edits =
                [
                    new Edit<MaterialDefinition, MaterialPatch>
                    {
                        Operation = EditOperation.Modify,
                        TargetId = "STONEFLOOR",
                        Patch = new MaterialPatch { Roughness = 0.85f, ReviewNote = "read as wet stone" },
                        Reason = "too glossy under the new lighting",
                    },
                ],
            },
            diagnostics);

        MaterialDefinition material = Assert.Single(effective.Materials);
        Assert.Equal(0.85f, material.Roughness);
        Assert.Equal(0f, material.Metallic);
        Assert.Equal("STONEFLOOR", material.BaseColorTexture);
        Assert.Equal(AuthoringProvenance.Edited, material.Provenance);
        Assert.Empty(diagnostics.Items);
    }

    [Fact]
    public void Documents_round_trip_through_json()
    {
        SceneLightEdits edits = Edits(
            new Edit<SceneLight, SceneLightPatch>
            {
                Operation = EditOperation.Modify,
                TargetId = "derived-0",
                Patch = new SceneLightPatch { Position = new Vector3(4, 5, 6), Intensity = 42f },
                Reason = "moved to the window",
            },
            new Edit<SceneLight, SceneLightPatch>
            {
                Operation = EditOperation.Add,
                TargetId = "hand-1",
                Item = Light("hand-1"),
            });

        string json = JsonSerializer.Serialize(edits, ManifestJson.Options);
        SceneLightEdits? back = JsonSerializer.Deserialize<SceneLightEdits>(json, ManifestJson.Options);

        Assert.NotNull(back);
        Assert.Equal(2, back.Edits.Count);
        Assert.Equal(EditOperation.Modify, back.Edits[0].Operation);
        Assert.Equal(new Vector3(4, 5, 6), back.Edits[0].Patch!.Position);
        Assert.Null(back.Edits[0].Patch!.Radius);
        Assert.Equal("hand-1", back.Edits[1].Item!.Id);

        // Operations serialize in their readable wire form, since humans edit this file.
        Assert.Contains("\"modify\"", json, StringComparison.Ordinal);
        Assert.Contains("\"add\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Vectors_serialize_as_arrays_rather_than_empty_objects()
    {
        // Vector3 exposes fields, not properties, so the default serializer emits {}.
        string json = JsonSerializer.Serialize(
            new SceneLightPatch { Position = new Vector3(1.5f, -2f, 0f) }, ManifestJson.Options);

        Assert.Contains("[", json, StringComparison.Ordinal);
        Assert.DoesNotContain("{}", json, StringComparison.Ordinal);

        SceneLightPatch? back = JsonSerializer.Deserialize<SceneLightPatch>(json, ManifestJson.Options);
        Assert.Equal(new Vector3(1.5f, -2f, 0f), back!.Position);
    }

    [Fact]
    public void Edits_files_sit_beside_the_baseline_they_correct() =>
        Assert.Equal(
            Path.Combine("scenes", "LBY.lighting.edits.json"),
            AuthoringStore.EditsPathFor(Path.Combine("scenes", "LBY.lighting.json")));
}
