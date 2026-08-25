# Walking

How an actor gets from one side of a room to the other, and how they stay on the floor
while they do it.

Three pieces, each answering a question the others cannot.

| | Question | Data |
| --- | --- | --- |
| `WalkBoundary` | may somebody stand here? | an indexed bitmap over the room, seen from above |
| `WalkPath` | how do they get there? | a search over that bitmap |
| `WalkFloor` | how high is the ground? | the BSP object the scene calls its floor |

## Where an actor may stand

An 8-bit bitmap laid over the room's footprint, named by the scene's `[WALKBOUNDARIES]`
section along with the size in world units it covers and where its corner sits. RC1's is
392×507 texels over 2,350×3,042 units, of which 47,320 are open.

The index is a region rather than a colour. Region 0 is the middle of the floor and the
numbers climb towards the walls, so the value is also a rough distance from the nearest
obstacle. 8, 9 and 255 are closed by default; a script may open and close the rest, which
is how a door that is locked stops being a way through without the room being rebuilt.

## Finding a way across

A* over the texels, on a lattice rather than every texel — a 392×507 room is two hundred
thousand nodes and a search over all of them for every click is not affordable. The route
that comes back is then smoothed: a corner is dropped whenever the straight line between
its neighbours is clear.

**The line that is tested has to be the line that is walked.** This is the whole of the
smoothing's correctness and it was wrong. The old test walked towards the far end one texel
at a time, moving diagonally while both axes differed and straight afterwards — which for
anything but a pure axis or a pure diagonal is a *different path*. From (0,0) to (10,2) it
went diagonally to (2,2) and then along the row, so a wall standing across the middle of
the real line was never sampled, the shortcut was allowed, and the actor then walked the
real line straight through the wall.

`WalkPath.Crosses` walks the actual line: the dominant axis one texel at a time with the
other rounded, which skips no texel because neither axis can move by more than one in a
step.

**A diagonal step must not slip between two blocked texels meeting at a corner.** Two
blocks touching at a point are a wall, whatever the texels say, and a line stepping across
the join passes through solid geometry without ever sampling a blocked texel. Both
orthogonal neighbours of a diagonal step have to be open.

The same routine serves both callers, at two strictnesses: the lattice check accepts any
walkable texel, and the smoothing check additionally refuses to pass closer to a wall than
the shortcut ceiling. One routine, one argument, rather than two nearly-identical loops
that can drift apart — which is what they had done.

## Staying on the floor

The boundary is a picture seen from above. It says where an actor may stand and nothing
whatever about how high the floor is there, and for a long time nothing else did either: a
walk held whatever height it set off at, which is right for a flat room and wrong for every
ramp, step and slope in the game.

The height comes from the room's own geometry. A scene names the object its floor is —

    floor=rc1_floor

— and every room's general `.SIF` does, so this is a lookup rather than a guess. RC1's is
3,050 triangles. They go into a uniform grid keyed on X and Z at about a character's height
per cell, so a query tests a handful rather than sweeping the room, and the answer is
barycentric in the horizontal plane: the same three weights that decide whether the point is
over the triangle mix the corners' heights, so a slope reads as a slope rather than as
steps.

**Rooms are not single-storey.** A stairwell's floor object covers the same ground twice
and a gallery over a hall covers it a third time, so "the triangle under this point" is
several triangles and which one is meant depends on where the actor already is. The nearest
candidate that is not an implausible climb wins — up to a step's height above, rather more
below, because walking off a kerb is ordinary and climbing onto a landing is not — and
failing that, the nearest of any, so an actor who has drifted is put back on something
rather than left in the air.

A vertical triangle inside the floor object has no "under". Its horizontal area is zero and
dividing by it answers with an infinity that then wins every nearest-height comparison in
the room, so it is refused rather than allowed to.

A scene that names no floor object, or names one the geometry does not have, gets no height
query at all and its actors hold the height they start at, exactly as before. The scene
report says which, because the failure is otherwise silent until the first ramp:

    Floor: rc1_floor, 3050 triangles

Both the walk and `Place` snap to it. Placing matters as much as walking: a spot authored
at zero in a room whose floor is not at zero would otherwise start every walk from the
wrong storey, and that is the one mistake the height query cannot recover from afterwards.

## How fast

At the stride's own pace, so the feet and the ground agree. Gabriel's walk covers 49.9
units in 1.40 seconds — 35.6 units a second — and `CHARACTERS.TXT` gives every character in
it a `ContAnim` to measure. An actor with no stride uses 65 units a second, a guess that
crosses R25 in about six seconds.

**A double-click doubles both**, the pace and the rate the stride plays at. That is a
modernisation rather than a restoration: 35.6 units a second is what the game was authored
at and it is genuinely slow to sit through once the player knows where they are going.
Multiplying only the pace would slide the feet; multiplying only the stride would mime
hurrying on the spot.

Not a run animation, because there is none to use. `CHARACTERS.TXT` names no run for
anybody, and the archives hold exactly one general run cycle — `GABERUN`, which belongs to
a cutscene. A stride played faster reads as hurrying; giving Gabriel a run and leaving the
rest of the cast walking would read as a bug.

Only the player hurries. The `hurry` flag travels from the click through
`SceneInteraction.Do` and `ActionRunner.Run` to the approach; a script that sends somebody
somewhere passes false, because a script's timings are written against the pace the game
walks at and arriving an actor before their line is worse than making the player wait.

## Clicking the ground

A click that finds nothing to do falls through to the floor: if the ray reached the object
the scene's `floor=` line names, and nothing nearer, the player walks there.
`SceneInteraction.FloorTarget` answers where.

The floor is one object among a hundred and the scene says which, which is what makes this
precise rather than a guess about what looks like ground. A rug, a bed or an open doorway
standing in front of the floor is a click on the rug, the bed or the doorway — the ray
stops at the nearest thing either way, so the same test that resolves a verb resolves this.

Two refusals matter. A floor object the scene also gives a **noun** is a thing rather than
ground, and the noun wins; TE3's floor is declared `noclick` for exactly this reason, which
strips its noun and hands it back to the walker. And a click that **dismisses an open verb
menu** never walks, because "not that after all" is the one gesture that would otherwise be
impossible to make without crossing the room.

Where the ray landed is not where the actor goes. The floor mesh runs under the furniture
and out through the doorways, so the point is put through `WalkBoundary.NearestWalkable`
first and the boundary decides; a spot it cannot reach still walks, as near as the search
gets. The **clicked height is kept** while the boundary decides the ground plan, because
the boundary is a plan seen from above and has no storeys — on a staircase its answer alone
cannot say which of two floors above one another was meant.

This is what the original does. `Scene::Interact` in the reference casts at the BSP when
nothing interactive was hit, compares the hit object's name against the scene's floor model
name, and calls `FindNearestWalkablePosition` before walking the ego there.

## Stopping where the floor does something

A scene file can mark out a rectangle of floor and name a noun for it — see
[scene-text.md](formats/scene-text.md#triggers) — and standing in one does that noun's
`WALK`. A walk the *player* asked for stops where it would step onto one rather than
crossing it: the route is cut at the point it crosses the rectangle's edge, which is
`Walker::FindEarliestPathNodeInsideActiveTriggerRegion` in the reference. Its own comment
gives the case, and it is the lobby on the first morning: the way to the front door goes
through Jean's rectangle, and without this the player walks over it and Jean introduces
himself to somebody already at the door.

Only the player, and only a walk they asked for. A script that sends somebody somewhere
means all the way there — the museum's eavesdrop ends by walking Gabriel into the very
rectangle that started it.

## Stopping where you can see the thing

`WalkToSee` is 2,120 of the corpus's 3,617 approaches — by a distance the commonest thing
anybody in the game does — and it means *walk until you can see it*, not *walk to it*. The
difference shows wherever the thing is behind something: walking to a painting on the far
side of a counter puts Gabriel through the counter, and walking to a door he is already
looking at makes him cross the room for no reason.

Three rules, all the reference's own (`Walker::WalkToSee`):

1. **Already in view is not a walk.** Turn to face it where you stand, and only if it is
   more than about thirty-five degrees off your current facing.
2. **Otherwise walk towards it, and stop where it comes into view.** The planned route is
   sampled at every corner and at three points along each leg — a doorway is usually
   crossed between two corners — and cut one corner *past* the first place the thing can
   be seen, because stopping on the exact frame a sliver of it appears round a corner
   reads as noticing something impossible.
3. **A route that never sees it is walked in full**, which is the old behaviour and the
   right fallback: the thing may be inside the cupboard the walk was meant to end at.

Seeing is `SceneSight`: six rays from the walker's head height — the character's own
`WalkerHeight` — to the middle of each face of the thing's box, against the room's own
triangles. Six rather than one because the middle of a bookcase, a car or a bed is inside
it and visible from nowhere. Nothing can be seen beyond **200 units** whatever the line of
sight, which is the reference's figure and is what stops "walk until you can see it" from
meaning "do not walk" in an open room.

Props and characters do not block sight. A walk is planned once before anybody sets off,
and a route that depended on where somebody was standing would be a different route every
time the story moved them.

The room's triangles are bucketed by where they stand on the ground plan and a ray visits
only the buckets it crosses; a room is ten to twenty thousand triangles and a walk asks
about thirty positions.

## Between the recorded frames

The stride is a clip like any other and is played like one: `ActFile.PoseAt` mixes the poses
either side of the moment rather than holding whole frames — where the two are recorded on
consecutive frames, which for a walk everything except the shoes is. A planted foot does not
move and so is not recorded, and what a gap in the recording means is a hold; see
`formats/vertex-animation.md`, and `known-issues.md` for the stride it was found in. Until 2026-08-24
this one clip asked for whole frames while every other clip in the game was mixed, so a
stride recorded at fifteen poses a second and drawn at a hundred and forty showed each pose
nine times — reported, exactly, as the legs being choppy and nothing else being choppy.

The forward travel that keeps the feet on the ground is taken out at the same fractional
moment that poses the meshes. Taking it from a whole frame while the legs are mixed between
two is the difference between a walk and a skate. What still reads whole frames is what asks
about the clip rather than a moment in it — how far it travels, which sets the pace — and the
footsteps, which are events on numbered frames.
