"""Draws the leaf and needle cards the modelled trees are dressed with.

    python tools/foliage/make_cards.py --workspace path/to/ContentWorkspace
                                       [--source path/to/normalized/textures]

Writes one RGBA PNG per species into ``enhanced/trees``, beside the trees themselves, and
records what went into each in ``cards.json``.

A card is not a crop of the original sprite, and the first attempt at this proved why. A
GK3 tree sprite is a whole tree seen from one side: cut a rectangle out of the middle of it
and almost every texel is opaque, so the card that was meant to be a spray of needles
against the sky renders as a solid green rectangle with a hard edge, and a tree built from
two hundred of them is a heap of boxes. What a foliage card has to be is mostly holes.

So the shapes are drawn and the *colours* are taken from the sprite. Every needle and every
leaf is painted with a colour sampled from the tree it stands in for, which is the part
that has to match: the lightmaps, the skyboxes and the thousands of distant cards still on
the hillsides were all authored against those greens, and foliage mixed fresh reads as a
tree from another game standing in this one.
"""

import argparse
import json
import math
import os
import random
import sys

try:
    from PIL import Image, ImageDraw, ImageFilter
except ImportError:  # pragma: no cover - the message is the whole point
    print("This needs Pillow: python -m pip install pillow", file=sys.stderr)
    raise


# Each card is drawn with its length running along +u, because that is how the tree
# generator hangs it: u follows the branch out from the trunk, v crosses it.
SIZE = 256

# The same card at four depths inside a crown, laid out two by two.
#
# A crown is dark at its heart and bright at its shell, and that gradient is most of what
# makes a mass of leaves read as a volume rather than as a heap of stickers. There is
# nowhere to put a per-leaf occlusion in the engine's vertex - position, normal and one
# texture coordinate is the whole of it, and widening that costs eight bytes on every
# vertex of every room - so the occlusion is baked into the picture instead and the
# generator picks the tile each leaf has earned. It costs one mip level of resolution and
# nothing at run time.
#
# The factors are centred rather than capped at one: the shell of a crown catches more
# light than the flat card ever did, and a set that only darkens comes out as a duller tree
# than the sprite it replaces. Weighted by how many leaves land in each tile, the atlas
# still averages to the sprite's own colour, which is the measure that matters.
AO_LEVELS = [1.15, 0.92, 0.72, 0.55]
ATLAS = 2                       # tiles per side; ATLAS * ATLAS must cover AO_LEVELS

CARDS = {
    "spruce": {
        "texture": "RBN_SPRUCE_SPRAY",
        "source": "PINE2",
        "kind": "needles",
    },
    "cypress": {
        "texture": "RBN_CYPRESS_SPRAY",
        "source": "TREE06",
        "kind": "needles",
    },
    "broadleaf": {
        "texture": "RBN_BROADLEAF_CLUMP",
        "source": "TREE00",
        "kind": "leaves",
    },
    "maple": {
        "texture": "RBN_MAPLE_CLUMP",
        "source": "MAPLE",
        "kind": "maple",
    },
    "darkbroadleaf": {
        "texture": "RBN_DARKBROADLEAF_CLUMP",
        "source": "WOODTREE3",
        "kind": "leaves",
    },
}


def palette(path, count=24):
    """The colours a sprite uses, and how much of it each one covers.

    Sampled from the opaque texels only, and never from the keyed ones - GK3 marks
    transparency with magenta as often as with an alpha channel, and a palette that
    included it would paint half the leaves pink.

    Returned with weights rather than as a plain list, because the proportions are the
    part that matters. A spruce sprite is mostly two dark greens with a scatter of pale
    highlights; drawn from an unweighted palette the highlights come up one time in
    twenty-four and the tree turns out grey.
    """
    image = Image.open(path).convert("RGBA")
    pixels = list(image.getdata())
    kept = [
        (r, g, b)
        for r, g, b, a in pixels
        if a > 128 and not (r > 200 and b > 200 and g < 80)
    ]

    if not kept:
        raise ValueError("no opaque texels in " + path)

    quantised = Image.new("RGB", (len(kept), 1))
    quantised.putdata(kept)
    reduced = quantised.quantize(colors=count, method=Image.MEDIANCUT).convert("RGB")

    counts = {}
    for colour in reduced.getdata():
        counts[colour] = counts.get(colour, 0) + 1

    found = sorted(counts, key=lambda c: c[0] * 0.30 + c[1] * 0.59 + c[2] * 0.11)
    return found, [counts[colour] for colour in found]


def mean_colour(path):
    """The average colour of a sprite's opaque texels."""
    image = Image.open(path).convert("RGBA")
    total = [0.0, 0.0, 0.0]
    seen = 0

    for r, g, b, a in image.getdata():
        if a > 128 and not (r > 200 and b > 200 and g < 80):
            total[0] += r
            total[1] += g
            total[2] += b
            seen += 1

    return [channel / max(seen, 1) for channel in total]


def shade(rng, palette, low=0.0, high=1.0):
    """One colour from the palette, drawn in proportion to how much of the sprite it covers.

    The band narrows the choice to a slice of the sprite's brightness range - the dark end
    for a twig, the whole of it for a leaf - and within that slice the weights decide.
    """
    colours, weights = palette
    first = int(low * (len(colours) - 1))
    last = max(first + 1, int(high * (len(colours) - 1)) + 1)
    span, mass = colours[first:last], weights[first:last]

    return rng.choices(span or colours, weights=mass or None, k=1)[0]


def needle_spray(rng, colours):
    """A conifer's spray: a rachis running out from the trunk, needles either side."""
    card = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    draw = ImageDraw.Draw(card)

    axis = SIZE * 0.5
    root, tip = SIZE * 0.02, SIZE * 0.98
    twigs = max(1, SIZE // 128)

    def needles(x0, y0, reach, sweep, thickness):
        for side in (-1, 1):
            for _ in range(rng.randint(5, 8)):
                length = reach * rng.uniform(0.55, 1.05)
                # Swept back towards the trunk and fanned across it, which is what makes a
                # spray read as a spray instead of as a comb.
                angle = math.radians(rng.uniform(22.0, 68.0))
                end = (x0 + math.cos(angle) * length * sweep,
                       y0 + side * math.sin(angle) * length)
                draw.line([(x0, y0), end],
                          fill=shade(rng, colours, 0.28, 0.88) + (255,),
                          width=thickness)

    # Side twigs first, so the main rachis is drawn over their roots.
    for _ in range(rng.randint(5, 7)):
        along = rng.uniform(0.05, 0.72)
        x = root + (tip - root) * along
        out = rng.choice((-1.0, 1.0))
        span = SIZE * 0.30 * (1.0 - along)
        end = (x + span * 0.85, axis + out * span * 0.85)
        draw.line([(x, axis), end], fill=shade(rng, colours, 0.0, 0.30) + (255,),
                  width=twigs)

        steps = 7
        for step in range(steps):
            at = (step + 1) / steps
            needles(x + (end[0] - x) * at, axis + (end[1] - axis) * at,
                    SIZE * 0.17 * (1.0 - at * 0.7) * (1.0 - along), 1.0, twigs)

    draw.line([(root, axis), (tip, axis)], fill=shade(rng, colours, 0.0, 0.30) + (255,),
              width=twigs + 1)

    steps = 34
    for step in range(steps):
        along = step / (steps - 1)
        x = root + (tip - root) * along
        # Needles shorten towards the tip, which is the whole of a conifer's outline.
        reach = SIZE * 0.34 * (1.0 - along) ** 0.65 + SIZE * 0.025
        needles(x, axis, reach, 1.0, twigs)

    return card


def leaf_clump(rng, colours, maple=False):
    """A broadleaf's clump: leaves scattered through an oval, with sky between them."""
    card = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    draw = ImageDraw.Draw(card)

    centre = SIZE * 0.5
    across, down = SIZE * 0.46, SIZE * 0.42

    # Twigs under the leaves. Without them a clump is a cloud with nothing holding it up,
    # and the gaps between leaves show sky where there should be wood.
    for _ in range(rng.randint(5, 8)):
        turn = rng.uniform(0.0, math.tau)
        reach = rng.uniform(0.5, 1.0)
        draw.line([(centre, centre),
                   (centre + math.cos(turn) * across * reach,
                    centre + math.sin(turn) * down * reach)],
                  fill=shade(rng, colours, 0.0, 0.25) + (255,),
                  width=max(1, SIZE // 190))

    for _ in range(rng.randint(150, 190)):
        # Rejection-sampled into an oval that thins towards its edge, so the clump has a
        # broken outline instead of the ellipse it was scattered into.
        while True:
            u, v = rng.uniform(-1.0, 1.0), rng.uniform(-1.0, 1.0)
            radius = math.hypot(u, v)
            if radius <= 1.0 and rng.random() > radius ** 2 * 0.62:
                break

        x, y = centre + u * across, centre + v * down
        size = SIZE * rng.uniform(0.038, 0.068) * (1.15 - radius * 0.30)
        turn = rng.uniform(0.0, math.tau)
        colour = shade(rng, colours, 0.20 + radius * 0.30, 0.80 + radius * 0.20)

        draw.polygon(_leaf(x, y, size, turn, maple), fill=colour + (255,))

    return card


def _leaf(x, y, size, turn, maple):
    """A leaf outline: a pointed oval, lobed along its length for a maple."""
    steps = 11
    half = []

    for step in range(steps):
        along = step / (steps - 1)
        # Widest a third of the way along and drawn to a point at the tip, which is the
        # silhouette every broad leaf shares.
        width = math.sin(math.pi * along) ** 0.65 * 0.20
        if maple:
            width *= 0.60 + 0.55 * abs(math.cos(along * math.pi * 2.5))
        half.append((along - 0.15, width))

    outline = half + [(along, -width) for along, width in reversed(half)]
    cos, sin = math.cos(turn), math.sin(turn)

    return [(x + (dx * cos - dy * sin) * size * 2.0,
             y + (dx * sin + dy * cos) * size * 2.0)
            for dx, dy in outline]


def match(card, wanted):
    """Scales a drawn card until its average colour is the sprite's average colour.

    A card built from a weighted palette is already close, and close is not the same as
    right: the leaves overlap, so the ones on top are seen more than their share and the
    average drifts towards whatever colour happened to be drawn last. Measured and
    corrected, a modelled spruce sits in a hillside of unreplaced cards without anybody
    being able to point at which trees were changed - which is the only test that matters
    while most of the corpus is still flat.
    """
    red, green, blue, alpha = card.split()
    opaque = alpha.point(lambda a: 255 if a >= 128 else 0)
    seen = sum(count for value, count in enumerate(opaque.histogram()) if value >= 128)

    if seen == 0:
        return card

    fixed = []
    for channel, target in zip((red, green, blue), wanted):
        total = sum(value * count for value, count
                    in enumerate(Image.composite(channel, opaque.point(lambda _: 0),
                                                 opaque).histogram()))
        average = total / float(seen)
        scale = 1.0 if average <= 1.0 else min(max(target / average, 0.55), 1.9)
        fixed.append(channel.point(lambda v, s=scale: min(255, int(v * s))))

    return Image.merge("RGBA", (fixed[0], fixed[1], fixed[2], alpha))


def finish(card):
    """Softens the cut edge without letting the card become a translucent rectangle.

    The shader tests alpha at a half and mips are generated, so a card whose alpha is a
    hard step aliases badly at distance while one that is broadly semi-transparent passes
    the test everywhere and turns back into a solid rectangle. A one-texel feather is the
    whole of the difference: enough for the mip chain to have something to average, not
    enough to fill the holes.
    """
    red, green, blue, alpha = card.split()

    # Colour is spread outwards under the transparent texels first, so that filtering at
    # the cut edge pulls in leaf rather than black. This is the same reasoning as
    # TextureKeying's nearest-opaque fill, for the same reason.
    spread = Image.merge("RGB", (red, green, blue)).filter(ImageFilter.MaxFilter(5))
    under = Image.composite(Image.merge("RGB", (red, green, blue)), spread, alpha.point(
        lambda a: 255 if a > 0 else 0))

    softened = alpha.filter(ImageFilter.GaussianBlur(0.6)).point(
        lambda a: 0 if a < 40 else min(255, int(a * 1.6)))

    red, green, blue = under.split()
    return Image.merge("RGBA", (red, green, blue, softened))


def atlas(card):
    """The card at every occlusion level, tiled into one texture.

    Row-major from the top left, which is the order ``AO_LEVELS`` is written in and the
    order ``grow_trees.py`` indexes. Alpha is the same in every tile - occlusion changes
    how much light a leaf gets, not what shape it is - so the mip chain blurring one tile
    into the next only ever averages two brightnesses of the same leaves, which is what a
    tree seen from far enough away should look like anyway.
    """
    sheet = Image.new("RGBA", (SIZE * ATLAS, SIZE * ATLAS), (0, 0, 0, 0))
    red, green, blue, alpha = card.split()

    for index, factor in enumerate(AO_LEVELS):
        shaded = Image.merge("RGBA", tuple(
            channel.point(lambda v, f=factor: min(255, int(v * f)))
            for channel in (red, green, blue)) + (alpha,))

        sheet.paste(shaded, ((index % ATLAS) * SIZE, (index // ATLAS) * SIZE))

    return sheet


def coverage(card):
    """What fraction of the card the shader will keep."""
    alpha = card.split()[3]
    kept = sum(count for value, count in enumerate(alpha.histogram()) if value >= 128)
    return kept / float(SIZE * SIZE)


def main(argv=None):
    parser = argparse.ArgumentParser(description="Draw foliage cards for GK3Reborn's trees.")
    parser.add_argument("--workspace", required=True)
    parser.add_argument("--source", default=None,
                        help="Where the converted sprites are "
                             "(default <workspace>/normalized/textures).")
    parser.add_argument("--species", nargs="*", default=None)
    options = parser.parse_args(argv)

    source = options.source or os.path.join(options.workspace, "normalized", "textures")
    out = os.path.join(options.workspace, "enhanced", "trees")
    os.makedirs(out, exist_ok=True)

    wanted = options.species or sorted(CARDS)
    records = []

    for species in wanted:
        if species not in CARDS:
            print("unknown species: " + species, file=sys.stderr)
            return 2

        card = CARDS[species]
        sprite = os.path.join(source, card["source"] + ".PNG")

        if not os.path.exists(sprite):
            print("missing sprite: " + sprite, file=sys.stderr)
            return 3

        colours = palette(sprite)
        # Seeded by the texture's own name, so redrawing the set does not reshuffle a
        # species nobody asked to change.
        rng = random.Random(card["texture"])

        if card["kind"] == "needles":
            drawn = needle_spray(rng, colours)
        else:
            drawn = leaf_clump(rng, colours, maple=card["kind"] == "maple")

        drawn = match(finish(drawn), mean_colour(sprite))
        atlas(drawn).save(os.path.join(out, card["texture"] + ".PNG"))

        record = {
            "species": species,
            "texture": card["texture"],
            "source": card["source"],
            "kind": card["kind"],
            "size": SIZE,
            "atlas": ATLAS,
            "aoLevels": AO_LEVELS,
            "colours": len(colours[0]),
            "sourceColour": [round(c, 1) for c in mean_colour(sprite)],
            "coverage": round(coverage(drawn), 4),
        }
        records.append(record)
        print("%-14s %-24s from %-10s %d colours, %.0f%% opaque, mean %s"
              % (species, card["texture"], card["source"], len(colours[0]),
                 record["coverage"] * 100.0, record["sourceColour"]))

    with open(os.path.join(out, "cards.json"), "w", encoding="utf-8") as handle:
        json.dump({"schemaVersion": 1, "stage": "C7.foliage-cards", "cards": records},
                  handle, indent=1)

    print("drew " + str(len(records)) + " cards")
    return 0


if __name__ == "__main__":
    sys.exit(main())
