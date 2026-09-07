// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Formats.Rebarn;

namespace GK3Reborn.Content;

public static class SceneDressing
{
    /// <summary>
    /// A model the set cannot be installed without.
    /// </summary>
    /// <remarks>
    /// The first terrace, and it stands for the rest. A finer check — every name in the
    /// table — would be answering a question nobody asked: the set is built, packed and
    /// installed as one thing, so a workspace holding half of it is a workspace somebody
    /// is in the middle of rebuilding, and the per-model diagnostic already covers that
    /// case exactly.
    /// </remarks>
    public const string Sentinel = "RBN_CZ_ROW_A";

    /// <summary>Whether the geometry the dressing places is installed.</summary>
    /// <param name="modelsDirectory">
    /// The loose <c>enhanced/models</c> directory, or empty when there is none.
    /// </param>
    /// <param name="packs">The ReBarn packs beside the executable, or null for none.</param>
    /// <returns>True when the set is there and the table should be applied.</returns>
    public static bool Available(string modelsDirectory, RebarnContent? packs) =>
        ModelLibrary.Open(modelsDirectory ?? string.Empty, packs).Has(Sentinel);
}
