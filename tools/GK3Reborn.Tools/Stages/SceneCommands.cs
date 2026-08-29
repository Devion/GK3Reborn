// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Globalization;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// The scene-geometry subcommands: cut a room into objects, and put them back.
/// </summary>
/// <remarks>
/// These parse their own arguments, for the same reason the pack commands do: a room list
/// and a crease angle belong to these two commands and to nothing else, and putting them
/// into the record every command shares would make the rest of the help worse.
/// </remarks>
public static class SceneCommands
{
    /// <summary>The commands this handles.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["extract-scenes", "compose-scenes"];

    /// <summary>Runs one of them.</summary>
    /// <param name="args">The whole command line, the command included.</param>
    /// <returns>A process exit code.</returns>
    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args[0] switch
        {
            "extract-scenes" => Extract(args),
            "compose-scenes" => Compose(args),
            _ => Usage($"Unknown scene command: {args[0]}"),
        };
    }

    private static int Extract(string[] args)
    {
        string? source = Flag(args, "--source");
        string? workspace = Flag(args, "--workspace");

        if (source is null || workspace is null)
        {
            return Usage("extract-scenes requires --source and --workspace.");
        }

        if (!float.TryParse(
                Flag(args, "--crease") ?? "40",
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float crease))
        {
            return Usage("--crease takes an angle in degrees.");
        }

        var diagnostics = new DiagnosticBag();

        new SceneExtractStage(Console.WriteLine).Run(
            source, workspace, Rooms(args), crease, Has(args, "--dry-run"), diagnostics);

        return Report(diagnostics);
    }

    private static int Compose(string[] args)
    {
        string? source = Flag(args, "--source");
        string? workspace = Flag(args, "--workspace");

        if (source is null || workspace is null)
        {
            return Usage("compose-scenes requires --source and --workspace.");
        }

        var diagnostics = new DiagnosticBag();

        bool composed = new SceneComposeStage(Console.WriteLine).Run(
            source, workspace, Rooms(args), Has(args, "--dry-run"), diagnostics);

        int code = Report(diagnostics);
        return code != 0 ? code : composed ? 0 : 3;
    }

    /// <summary>The rooms named after <c>--only</c>, or none, meaning all of them.</summary>
    private static List<string> Rooms(string[] args)
    {
        List<string> rooms = [];

        for (int i = 1; i < args.Length; i++)
        {
            if (!args[i].Equals("--only", StringComparison.Ordinal))
            {
                continue;
            }

            for (int j = i + 1; j < args.Length && !args[j].StartsWith("--", StringComparison.Ordinal); j++)
            {
                rooms.Add(Path.GetFileNameWithoutExtension(args[j]));
            }
        }

        return rooms;
    }

    private static int Report(DiagnosticBag diagnostics)
    {
        if (diagnostics.Items.Count > 0)
        {
            Console.WriteLine();
        }

        foreach (Diagnostic diagnostic in diagnostics.Items)
        {
            TextWriter writer =
                diagnostic.Severity == DiagnosticSeverity.Error ? Console.Error : Console.Out;

            writer.WriteLine(diagnostic.ToString());
        }

        return diagnostics.HasErrors ? 3 : 0;
    }

    private static string? Flag(string[] args, string name)
    {
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool Has(string[] args, string name) =>
        args.Skip(1).Any(a => a.Equals(name, StringComparison.Ordinal));

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"""

                extract-scenes  --source <GK3 Data> --workspace <dir> [--only ROOM ...]
                                [--crease {SceneObjectGlb.DefaultCrease:F0}] [--dry-run]
                    Cut every room into one glTF file per named object, under
                    enhanced/scenes/<room>/original, and classify what each object is so
                    the modelling pass knows what to touch. Writes
                    manifests/scene-objects.json.

                compose-scenes  --source <GK3 Data> --workspace <dir> [--only ROOM ...]
                                [--dry-run]
                    Gather the improved objects a room has — the files sitting beside
                    original/ — into one enhanced/scene-geometry/<room>.glb, checking each
                    against the geometry it claims to replace. Rooms with nothing improved
                    are skipped, and the game falls back to their original geometry.

                See docs/scene-geometry.md.
                """));

        return 2;
    }
}
