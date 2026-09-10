# The pointer

The pointer is the port's own: five pictures carried inside the executable, not in
any `.rebarn`, so a game with no packs still has one and a pack cannot swap it.

| Shape | When | Clicks with |
| --- | --- | --- |
| Default | nothing under the pointer answers to a click, the verb bar is open, a film is playing, or a screen such as Sidney covers the room | its corner |
| Exit | the noun is `EXIT`/`EXIT1`..`EXIT5`, or the default verb has a `c_exit_*` cursor in `VERBS.TXT` (`EXIT`, `EXIT_UP`, `GO_DOWN`, ...) | the doorknob |
| Look | the default verb is `LOOK` | the middle of the lens |
| Talk | the default verb is `TALK`, `Z_CHAT`, or a topic | the bubble's tail |
| Interact | any other default verb, or the room's own machinery claims the click | the fingertip |

"Default verb" is what a plain left click does, as `Hover.Default` computes it: the
verb the model names, else the first non-item verb offered. The chooser is
`Game/PointerChoice.cs`; the pictures and their hotspots are `Platform/PointerArt.cs`.

## Size

The sources are 512 square, and are shrunk at run time by area averaging with alpha
applied, so an edge over nothing does not pick up the colour of the clear pixels round it.
The size is a fraction of the framebuffer's height (1/24, so 45 px at 1080p and 90 px at
4K) times the player's *Pointer size* setting on the Controls page, 50% to 200%. The
window remakes the cursor when the framebuffer changes size.

Handed to the platform as a hardware cursor through Silk.NET, so it moves with no frame
of lag and survives a slow frame. A platform that refuses a custom cursor is left with
its own arrow and says so once in the log.
