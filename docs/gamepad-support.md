# Gamepad support

The engine supports mapped gamepads through Silk.NET, with buttons, triggers,
stick input, and detection of connected/disconnected pads. It uses the first
connected pad. Gameplay buttons and pointer buttons can be rebound under
Settings → Controls; changes persist in the settings file.

## Default controls

| Control | Action |
| --- | --- |
| Left stick | Pointer in original mode; walk/strafe in first person |
| Right stick | Look around in first person and original/free camera modes |
| Bottom face (A / Cross) | Primary interaction; menu confirm |
| Right face (B / Circle) | Secondary interaction; menu back |
| Left face (X / Square) | Middle pointer action |
| Top face (Y / Triangle) | Inventory |
| Left trigger | Show hotspots |
| Left shoulder | Reset camera; previous settings section |
| Right shoulder | Next camera; next settings section |
| Left stick click | Move faster while held |
| Right stick click | Release the first-person pointer while held |
| Back / Select | Journal |
| Start | Game menu |
| D-pad | Navigate menus and adjust settings |

First-person movement uses analog speed, a radial dead zone, and the existing
floor/boundary collision rules. Right-stick look uses elapsed time and the
first-person look sensitivity/inversion settings. Holding the pointer-release
button lets the left stick move the cursor without walking the character.

In original mode the left stick normally moves the pointer, so movement is
through world interactions. Disabling “Left stick moves the pointer” lets the
stick move the original/free camera directly. The right stick turns that camera
in either case. Pointer buttons support holds, dragging, and double-clicks.

## Audit fixes

- Clearing a gamepad binding now clears that device's binding and preserves keys.
- Typing a letter while rebinding a pointer button no longer erases its binding.
- Gamepad pointer holds survive frame boundaries, allowing sustained interactions.
- Gamepad pointer clicks now share mouse double-click detection.
- Menu confirmation is no longer overwritten by the simultaneous pointer click.
- Left-stick movement no longer synthesizes D-pad buttons or captures them while rebinding.
- Pointer movement filters stick drift; camera movement preserves analog speed.
- Original/free camera modes now read the sticks, and first person has a default
  pointer-release button that suppresses stick walking while held.

## Remaining limitations and verification

This is not yet complete controller-only support. Text entry (including Sidney)
still needs a keyboard. Menu navigation buttons and stick assignments are fixed;
the binding editor changes gameplay/pointer buttons, not those menu controls or
axes. Binding cancellation/clearing uses Escape/Backspace. There is no exposed
dead-zone adjustment or stick-swap setting. Pointer handling currently depends
on a mouse device being available to the window backend.

Automated validation: warning-free test-project build and 113 passing targeted
tests covering controls, gamepad queries, first-person movement/body, camera
direction, front-end/menu scrolling, and architecture. No physical controller
or end-to-end game session was tested in this audit. Hardware verification should
cover hot-plug/reconnect, Xbox and PlayStation mappings, menu selection, sustained
pointer interactions, and first-person walking/looking through scene changes.
