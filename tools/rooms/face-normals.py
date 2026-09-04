"""Turn down the generated normal maps on every character's face.

Reported as "RT high and some NPC's face shadowing, shadow acne I think? Grace has this
significantly during most of her scenes". It is not shadow acne and it is not the shadows:
rendered with --flat the face is clean, rendered with --relief 0 the smear is still there,
and it is there at --rt none as well. It is the normal map.

A face's normal map is generated from its own colour texture, and a GK3 face has its
shading painted into it: the cheekbone, the blush, the shadow beside the nose. The
inference pass read that as relief and wrote fully saturated lobes across both cheeks —
they are plainly visible as red and green blobs in enhanced/normals/GRA_FACE.PNG — and
under any lighting at all that comes out as a faceted grey smear following the triangle
edges, which is exactly what shadow acne looks like.

Turned down rather than off. The eyes, the nostrils and the lips are real relief and worth
keeping, and they survive a quarter strength while the painted shading stops being
geometry.

Run from the repository root:

    python GK3Reborn/tools/rooms/face-normals.py

then rebuild the packs, because a player has only those: see rebuild-content.cmd.
"""

import json
import os
import re
import sys

LIBRARY = os.path.join('ContentWorkspace', 'manifests', 'material-library.json')
EDITS = os.path.join(
    'ContentWorkspace', 'manifests', 'material-library.materials.edits.json')

STRENGTH = 0.25

REASON = (
    "A face's normal map is generated from the colour texture, and a GK3 face has its "
    "shading painted into it - the cheekbone, the blush, the shadow beside the nose. The "
    "pass read that as relief and wrote fully saturated lobes across both cheeks, which "
    "comes out as a faceted grey smear at any lighting quality and is worst where the "
    "camera comes closest. Turned down rather than off: the eyes, the nostrils and the "
    "lips are real relief and worth keeping, and they survive a quarter strength while "
    "the painted shading stops being geometry.")


def is_face(name):
    """Whether a material is a character's face or the forehead patch above it."""
    return bool(re.search(r'(_FACE|_FOREHEAD|FACE\d*$)', name, re.I))


def main():
    if not os.path.exists(LIBRARY):
        print(f"no library at {LIBRARY}; run this from the repository root", file=sys.stderr)

        return 1

    with open(LIBRARY, encoding='utf-8') as file:
        library = json.load(file)

    names = sorted(m['id'] for m in library['materials'] if is_face(m['id']))

    with open(EDITS, encoding='utf-8') as file:
        edits = json.load(file)

    by_id = {e['targetId']: e for e in edits['edits'] if e.get('operation') == 'modify'}
    added = 0
    changed = 0

    for name in names:
        if name in by_id:
            # Only the one field, and the note is appended rather than replacing whatever
            # somebody wrote about the roughness. An edit layer that overwrote its own
            # earlier entries would lose the reasoning for every correction before this.
            by_id[name].setdefault('patch', {})['normalStrength'] = STRENGTH

            # And the note, once. Running this again must not stack the same
            # sentence up behind itself.
            if REASON not in by_id[name].get('reason', ''):
                by_id[name]['reason'] = (
                    (by_id[name].get('reason', '') + ' ').lstrip() + REASON)

            changed += 1

            continue

        edits['edits'].append({
            "operation": "modify",
            "targetId": name,
            "patch": {"normalStrength": STRENGTH, "reviewNote": REASON},
            "reason": REASON,
        })

        added += 1

    with open(EDITS, 'w', encoding='utf-8') as file:
        json.dump(edits, file, indent=1)

    print(f"{len(names)} face materials: {added} new, {changed} updated")

    return 0


if __name__ == '__main__':
    raise SystemExit(main())
