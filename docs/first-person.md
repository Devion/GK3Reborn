# First person

GK3 is played with a camera that floats over the room and a character who walks to wherever
you click. First person replaces the first half of that: the camera becomes Gabriel's or
Grace's head, WASD or a stick walks them, the mouse turns them, and a dot in the middle of
the screen is what a click acts on. Everything else — the action files, the approach walks,
the verb bar, the story's own cameras — is untouched.

Turn it on with **Perspective** on the Playing page — "Free cam" or "First person" — or
with `--first-person`. It is a way of playing rather than something to flip mid-room, so
there is no key for it. The row below it, "No-clip camera", is a different question and
keeps its own setting: that one is about the camera passing through the walls, not about
where the player watches from.

## What moves

`Game/Navigation/FirstPerson.cs` is the body. It is not a camera: it owns a position, a
heading and a tilt, and the view is taken from its eyes afterwards. That matters because
everything the story asks about the player — which patch of floor they are standing on,
whether they can see a thing, how far they are from somebody — goes on being answered by
the ego, exactly as it was when the player clicked the floor to get there.

Each frame `Application` reads the ego's position back out of the room, walks the body, and
writes the result back with `SceneUpdate.Step`, which moves the placement and nothing else.
`Place` would have done as well except that it stops every clip the actor is playing, and
a walk that cancelled the idle sixty times a second is a walk that cancels the story.

Where the body may go is the room's own walk boundary — the same indexed bitmap that fences
in a clicked walk — and how high it stands is `WalkFloor`. A room that declares no boundary
falls back to the camera shell, tested at head height, because the shell is what was drawn
around where a camera may be.

Three details are not obvious.

**A frame's travel is taken in pieces of at most ten units.** The boundary is a bitmap, so
a single step longer than a texel can begin on open floor and end on open floor with a wall
between the two. Ten units is a quarter of the narrowest texel any room uses; at the
ordinary pace a sixtieth-of-a-second frame is three units and this never divides anything,
which is the point — it costs nothing until the frame rate drops, which is exactly when
somebody walks through a wall.

**A refused step is retried at half and a quarter of its length before it slides.** Without
it, how near a wall a player may stand depends on how long the frame was.

**A refused step then tries each axis on its own**, so walking at a wall at an angle slides
along it rather than stopping dead. That is what pressing forward into a doorframe means.

**A step off the end of the floor the room named is refused** — but only while the player is
standing on it. Most outdoor boundary bitmaps reach further than the floor object does, and
without this the player walks out over open country at the height of the last thing they
stood on. Allowed when they are already off it, so a room whose floor falls short of its
boundary never traps anybody.

**The ego's position is written only once the player has actually moved or turned.** Until
then they are a passenger: a room arrived at places them a frame or two after it opens, and
writing before that stamps them at the coordinates of the room they came from. The same rule
covers the other end — a `--eye` given on the command line is applied only when it was
given, because that hook runs again every time the story moves the camera and with nothing
to place it wrote the walker's default, which on arrival is the origin.

**A player pressing into something and getting nowhere at all for a second is stood on the
nearest open texel**, the rule `Unstick` already applies. That is what an ego nothing ever
placed looks like: the world origin is outside every boundary there is, so every step from
it is refused. A doorframe refuses a step too, which is why it is a second and not a frame.

The eye sits 60 units above the floor, which is the reference engine's own
`GameCamera::kDefaultHeight` and so the height the game's rooms are furnished against. A
character's `WalkerHeight` — 76 for Gabriel — is the top of their head, and using it put
the view a head too high over every table in the game.

The pace is 160 units a second, two and a half times `Walker.Speed`. The game's own walk
cycle was authored at 65, which is right for watching somebody cross a room and much too
slow for being them: GK3's units put a character around seventy tall, so 65 is a strolling
1.4 m/s and 160 is the 3.5 m/s that every game played from inside a head has settled on.
The Playing page moves it between 80 and 320. Shift adds sixty per cent on top.

**A walk or a turn begins from where the actor is facing now, not from where the scene
first put them.** `SceneUpdate.Walk` read the authored transform, which is the same answer
for an actor nothing has turned and is why this went unnoticed. With the player free to turn
on the spot, every click on a thing began by snapping them back to their authored heading
and turning smoothly from there — reported as the camera spinning round before it looked at
what was clicked. And the heading is read back the way it was written: `FacingArrow.Rotation`
takes the model's own built facing off a placement and `Walker.HeadingOf` does not put it
back, which is a difference of nothing at all for a model built facing the half turn and a
visible jump for any other.

**In first person the player's position is theirs and not their pose's.**
`SceneUpdate.Driven` names them, and `Follow` — which syncs an actor's logical position to
wherever their model's pose has put their feet, every frame, as the reference does in
`LateUpdate` — refuses to move them. Set from the top of the frame, before the world moves,
and for as long as they are on foot rather than only while they walk: the sync is what
dragged them off the spot a room had just put them on the moment they stood still. A clip
that is meant to carry them somewhere still can, because it is refused only while nothing is
animating them. That sync is right for the rest of the game and fatal
here: the eye *is* that point, so Gabriel's idle shifting its weight became a camera that
wandered away across the room and climbed as it went, with nobody touching the controls.
`Place`, `Carry`, `Step` and a walk under way write the position outright and outrank the
guard, because relocating somebody is not the same as posing them.

Nothing is animating these legs, so footfalls are counted rather than heard from a walk
cycle's own landings: one every 55 units, through the same `FOOTSTEPS.TXT` table a walked
step uses, so the ground under the player still decides what they sound like.

## What the mouse does

While the player is in the room with nothing in front of it, the pointer is pinned and
hidden and every movement of it turns the view — `IGameInput.PointerLocked`, which is
GLFW's raw motion through Silk, falling back to a plain pinned cursor and then to nothing.
A platform that will not pin a cursor reads the flag back as false, and first person is
then looked around with a button held, the way the free camera always was.

Everything that is not the room gives the pointer straight back: the inventory, Sidney, a
close-up, the fingerprint kit, the verb bar, a film, the console, the pause menu. Holding
`C` — "Show the pointer", rebindable — gives it back in the room as well, for clicking something off to one side without
turning to face it. The cursor comes back in the middle of the window, which is where the
crosshair was and so where whatever has just opened is anchored.

With the pointer pinned, what the player is acting on is what they are looking at: the pick
ray is cast through the middle of the framebuffer rather than through the pointer, the
crosshair is drawn there, and the noun label and the verb bar hang off the same point. The
dot grows and takes the accent colour over anything actionable, which is the only feedback
there is that turning another degree would put you on it.

## Walking into the way out

A way out taken by somebody who is walking is a way out walked into. Each frame the player
moves, a ray is cast from their eyes along the way they are **travelling** — not the way
they are looking, so crossing a doorway sideways with your head turned does not leave the
room — and if it meets a way out within reach, the exit's own action is performed. Nothing
else is special-cased: the approach walk, the line Gabriel says about it and the
`SetLocation` all happen exactly as they do for a click.

What counts as a way out is what the game itself marks as one: a noun of the `EXIT`*n*
shape, or a verb the shipped `VERBS.TXT` gives a `c_exit_*` cursor — `EXIT`, `EXIT_UP`,
`EXIT_DOWN`, `EXIT_LEFT`, `EXIT_RIGHT`, `GO_UP`, `GO_DOWN`, `ENTER`. A cupboard that
answers to `OPEN` is furniture, and walking into the furniture opens nothing.

Reach is 90 units ordinarily and 190 when the step was refused. Being stopped is what
separates walking *into* a way out from walking *past* one, and it is what an outdoor scene
looks like from the inside: the ground runs out, the player leans on the edge of it, and the
hotspot for the road is a little way beyond. Two guards keep it from firing twice — a way
out that answers with a line rather than a door is not run again for three seconds, and no
way out takes at all in the first second in a room, or walking through a door with the key
still held would send you straight back out of it.

## The camera during a conversation

The story still gets the camera, and it should: GK3's conversations are cut with shots its
artists framed, and watching two people talk from inside one of their heads throws all of
that away. Three things change in first person.

**The story has the camera only once it has actually pointed one.** `SceneUpdate.Framed` is
false from the moment an action begins and true when the story moves the view. An action
that walks the player across the room and says a line names no camera at all; taking the
view off them for it left them watching their own back walk away.

**Leaving the player's own eyes is a move, never a cut.** `Cut` glides whenever the view is
theirs — `GameState.ViewIsTheirs` — unless the script used `ForceCutToCameraAngle`, which is
a script saying it means this one. The glide is eased at both ends now rather than linear: a
camera that starts and stops dead reads as machinery being pushed across the room on a
trolley.

For that to leave from the right place the story has to be told where the view actually is,
because its own answer is the last camera it happened to name — usually the one the room
opened on. `SceneUpdate.Elsewhere` is that hand-off, and a move begins from it. Without it,
the view jumped across the room and then glided politely to the conversation.

The way back is the same move in reverse and belongs to the body: `FirstPerson.ReturnFrom`
takes the shot the story was holding and eases the view back into the player's eyes over
nine tenths of a second, turning the short way round.

It runs only for a view that was already the player's in this room — going into a
conversation and coming back out of one. Arriving is a cut: sliding into the player's head
at every door is a thing to sit through a hundred times an evening.

**An authored shot is checked rather than trusted.** A dialogue camera frames the spot the
actors were meant to be standing on; on foot the player has walked wherever they liked.
`ConversationCamera.Frames` asks whether a named camera really does hold everybody talking,
at half the camera's own field of view rather than the tighter twenty degrees used to rank
one authored shot against another — a wide two-shot puts somebody near the edge of the
picture and loses that contest while holding them perfectly well. It also allows a quarter
past square behind a speaker, because two people talking face each other, so a shot square
on to the pair is at right angles to both of them and the strict rule refuses it on a
rounding error.

When nothing the room names holds the pair, `ConversationCamera.Composed` builds a shot:
square on to the line between the two, on the side the view is already on — crossing the
line would swap left and right and read as the pair having changed places — at head height
with a slight rise, standing back a little more than they are apart. It is pulled in and
then swung to the far side until it is inside the room's camera shell, and gives up rather
than filming from inside a wall. `SceneUpdate.Stage` puts it in front of the cameras the
story names; the room loop takes it away again the moment the player has the view back.

## Photographing it

A headless run has no keyboard, so two switches drive the body:

    GK3Reborn.exe --scene RC1 --timeblock 110A --first-person \
        --eye 1643,80,-2389 --aim 212,0 --push 0,1 \
        --frames 400 --screenshot exit.png --settings scratch.json

`--push X,Y` holds the movement controls for the whole run — the only way a run with no
keyboard can walk anywhere, and the way out of a room, the edge of a terrain and a footstep
on gravel all only happen while walking. `--eye` and `--aim` stand the player somewhere and
point them, rather than only moving the camera, since on foot the camera is worked out from
the body. The log says `GABRIEL: walked into EXIT:EXIT_LEFT` and then `Leaving RC1 for rc2`.
