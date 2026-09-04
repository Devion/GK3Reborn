"""Correct the roughness of the floors that are meant to be polished.

Reported as "the tile floor in the hotel, the tile floor in the church don't reflect much
at all". They did not, and the material library says why: the classifier reads a texture
and guesses what the surface is, and a photograph of worn stone under a 1999 lightmap looks
like raw concrete to it. The church's nave came out at 0.90 roughness and the threshold for
a reflection being worth tracing at all is 0.60, so nothing in the nave was ever asked.

These are the surfaces a person has looked at in-engine and decided about, which is what
the edit layer is for: see ADR 0006 and `Rendering.Materials.SurfaceFinishes`. An edit also
marks the material *authored*, which is what makes its roughness outrank the generated ORM
map — without that the correction would be overwritten by the same pass that got it wrong.

Run from the repository root:

    python GK3Reborn/tools/rooms/polished-floors.py

then rebuild the packs, because a player has only those: see rebuild-content.cmd.
"""

import json
import os
import sys

EDITS = os.path.join(
    'ContentWorkspace', 'manifests', 'material-library.materials.edits.json')

# What each floor is, and how rough that actually is. Nought is a mirror and one is chalk;
# a swept stone floor with a century of wax on it is about a quarter to a third, and a
# glazed tile a little under that.
FLOORS = {
    # The church of St Mary Magdalen. The nave is dressed stone that the pass called
    # concrete; the runner up the middle and the tiles either side of it are glazed.
    'CHUGRYCNCRT': (0.30, "The church's nave floor, which the classifier called raw "
                          "concrete at 0.90. It is dressed and waxed stone under a "
                          "century of feet, and the room is the one the reflections were "
                          "reported missing from."),
    'CHUTILE': (0.26, "The glazed runner up the middle of the nave."),
    'CHUGRYTILE': (0.28, "The grey tiles either side of the runner."),
    'CHUALTTILE': (0.32, "The tiling round the altar, which the pass called matte outright."),
    'CHUBAPTILE': (0.36, "The tiling round the font."),

    # The chequered marble the runner is made of, which is a separate texture set and was
    # matte in three of its four variants.
    'CHECKERTRANS': (0.26, "Chequered marble."),
    'CHECKER_01': (0.26, "Chequered marble."),
    'CHECKER_02': (0.26, "Chequered marble."),
    'CHECKER_03': (0.26, "Chequered marble."),

    # The Hôtel de Rennes-le-Château.
    'LBYFLOOR': (0.28, "The lobby's glazed tile, the other floor the reflections were "
                       "reported missing from. The ceiling and its hanging lamps are "
                       "directly above it and are what it has to show."),
    'WOODTILE': (0.48, "The lobby's waxed boards. Less than the tile and far less than "
                       "the 0.62 the pass gave it: wax is what a hotel floor has on it."),
    'DINWOODTILE': (0.48, "The dining room's boards, the same floor by another name."),
    'KITTILE': (0.30, "The kitchen's tiling."),
    'BTHTILE1': (0.26, "A bathroom's tiling, which is glazed by definition."),
    'BTHMARBLE': (0.24, "And its marble."),
    'KITMARBLE': (0.24, "The kitchen's marble."),
    'MARBLE': (0.24, "Marble, which the pass had at 0.72 — that is limestone, not marble."),
}


def main():
    if not os.path.exists(EDITS):
        print(f"no edits file at {EDITS}; run this from the repository root", file=sys.stderr)

        return 1

    with open(EDITS, encoding='utf-8') as file:
        edits = json.load(file)

    by_id = {e['targetId']: e for e in edits['edits'] if e.get('operation') == 'modify'}
    added = 0
    changed = 0

    for name, (roughness, why) in FLOORS.items():
        reason = (
            f"{why} Corrected after looking at the room in-engine: the classifier's own "
            f"number kept this surface above the roughness a reflection is worth tracing "
            f"from, so the floor reflected nothing however the reflection settings were "
            f"set.")

        if name in by_id:
            # Only the one field: an edit layer that overwrote its own earlier entries
            # would lose the reasoning for every correction made before this one.
            by_id[name].setdefault('patch', {})['roughness'] = roughness

            # And the note, once. Running this again must not stack the same sentence up
            # behind itself.
            if reason not in by_id[name].get('reason', ''):
                by_id[name]['reason'] = (
                    (by_id[name].get('reason', '') + ' ').lstrip() + reason)

            changed += 1

            continue

        edits['edits'].append({
            "operation": "modify",
            "targetId": name,
            "patch": {"roughness": roughness, "reviewNote": reason},
            "reason": reason,
        })

        added += 1

    with open(EDITS, 'w', encoding='utf-8') as file:
        json.dump(edits, file, indent=1)

    print(f"{added} new, {changed} updated; {len(edits['edits'])} edits in all")

    return 0


if __name__ == '__main__':
    raise SystemExit(main())
