# Camera bounds

What keeps the player's view inside the room.

## Why not the room itself

A room's own geometry is the wrong thing to collide against. It is a box seen from the
inside with holes in it: doorways stand open, a wall meets a ceiling with a seam, and a
backdrop hangs beyond the window with nothing between. R25 alone has `r25_hal_bkg`, a
thousand triangles of hallway standing outside the room's far wall for the door to be open
onto. A camera pushed against any of that finds its way through, and what it sees on the
other side is the room turned inside out — walls from behind, a floor with nothing under
it, and the black between the scenery.

The game's artists solved this in 1999 and the answer is in the data.

## The shells

**114 models in the corpus are camera bounds**: closed, invisible shells authored around
the space the camera may occupy. A scene names them with `cameraBounds=` in its
initialisation file and nothing draws them. Of the game's 79 locations, **78 name at least
one**. MA2 is the only one that names none, and it names no geometry either — no archive
holds an `MA2.BSP` — so in practice the coverage is complete.

They are models rather than objects in the room's geometry, which is what lets them be
sealed where a room with doorways in it is not. They stand at the world origin — each
mesh's own `MeshToLocal` is the whole of the journey into the room — and their faces are
turned **inward**, towards the camera.

`cameraBounds` is the one general setting the original **adds to** rather than overriding.
R25 names `R25CameraBounds` for the room and `r25_sidcm` in the block covering the
timeblocks where Sidney is out on the desk; at 202P both apply, 248 triangles between them.
RC1 names two at once. `SceneDefinition.CameraBounds` therefore joins where `floor=` and
`boundary=` beside it replace.

    camera bounds: R25CameraBounds, 208 triangles
    camera bounds: R25CameraBounds, r25_sidcm, 248 triangles
    camera bounds: Rc1_CamBnds, Rc1_CamBnds_van, 540 triangles

`check-scenes` counts the distinct shells across the corpus, names any location that
declares none, and names any shell no archive holds — which would otherwise be silent until
somebody flew out through a wall.

## Resolving a step

`CameraBounds.Resolve` takes where the camera is and the offset it wants, and answers where
it ends up. The camera is a **sphere of 16 units**, the reference implementation's radius:
treated as a point it would put its near plane through the wall it is touching, which reads
as the wall vanishing while the camera is still nominally inside the room.

Three things can stop that sphere and all three are tested, nearest wins:

| | why it is not enough on its own |
| --- | --- |
| the face | a step aimed at the seam between two triangles lands inside neither |
| the three edges | a step that clips a rim passes beside the face entirely |
| the three corners | the ends of every edge, where two rims meet |

The sweep is against the whole step rather than against where it ends, so nothing tunnels
however fast the camera is moving.

**A move away from a surface's front is never refused.** Only crossing from the inside out
is barred, which means a camera that starts outside its shell — placed there by a scene's
own viewpoint — can always get back in rather than being trapped. A room that opens that
way says so on the console; `CameraBounds.Contains` answers it by counting crossings along
one awkward ray.

**What is left of a blocked move is redirected along the surface.** A camera that stopped
dead on contact would be unusable — every wall would be flypaper — so the remainder is
projected onto the plane through the new position and tried again. Two passes: the first
turns the move along the wall, the second stops it in a corner, and a third would chase the
last thousandth of a unit.

Every triangle is tested every frame. A shell is a few hundred triangles — the largest in
the corpus is CS2's 2,233 — and there are one or two of them, so a tree would save nothing
worth the code.

## Where it is wired in

`FreeCamera` carries a `Confine` hook rather than the bounds themselves, because what the
camera may not pass through is a question about the room and the room belongs to the game
rather than to the renderer. `Application` fills it in when the scene has a shell.

`--free-camera` leaves it null and gives the old behaviour back. Flying outside a room is
how some of its geometry gets checked, and that is worth keeping — the doc comment on
`FreeCamera` has always said the camera exists to look at scenes from places the game never
looks at them from.
