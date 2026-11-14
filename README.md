# Pedal Master Loop

ReBuzz managed control machine, v1.0.0. Loops each section of the song a set
number of times, then plays straight on into the next section.

It works by moving the song's own loop markers, so every machine in the
sequence follows, not only the drum machines.

## Build

Close ReBuzz, then:

    dotnet build -c Release

The dll is copied to `C:\Program Files\ReBuzz\Gear\Generators\`. For another
install path: `dotnet build -c Release -p:ReBuzzDir="D:\ReBuzz"`.

Needs .NET 10 SDK. References `BuzzGUI.Interfaces.dll` and `BuzzGUI.Common.dll`
from the ReBuzz folder.

## Use

1. With the song stopped and its loop set to the whole song, add
   **Pedal Master Loop** (it is listed with the generators). It reads the loop
   start and end once and divides that range into sections of 256 ticks.
2. Double-click the machine to open its window.
3. Set a repeat count per section with − and +. Below 1 is ∞: the section
   loops until you press Next.
4. Press play. The section under the play position starts looping.

Song looping must be on in ReBuzz. The window warns if it is off.

### Window

Colour carries the state: blue is the section that is playing, amber is held,
green is the last pass before moving on, grey is stopped.

- **Play**: plays from the first tick of the section under the play position.
  It makes the same two calls as F6 in the sequence editor (set the position,
  start), with the position snapped to the section start. Pressed while
  playing, it restarts the current section and counts from pass 1 again.
- **Stop**: same as F8.
- **Section numbers**: while stopped, click a number to cue that section. Play
  then starts there.
- **Now playing**: section, a state badge, pass count with one pip per pass,
  position in the pass, and what happens next.
- **Hold**: stay on this section until released. While held the button reads
  **Held**, and the card, the badge and the section's row turn amber with
  `HELD · pass N`. If the count has already been reached, releasing leaves at
  the end of that pass.
- **Next**: leave at the end of this pass, whatever the count. On the last
  section it goes back to section 01. Lit green while a move is pending.
- **One more**: add one pass to this visit. If a Next or To start is queued, it
  cancels that instead.
- **To start**: go back to section 01 at the end of this pass.
- **Enabled**: off releases the loop to the full range.
- **After last section**: *Hold last* (default) keeps looping the last section;
  *Back to start* cycles the whole song.
- **Set all**: ×1, ×2, ×4, ×8, ∞ for every section.
- **First tick / Section length / Sections**: the layout. *Read from song loop*
  takes the range from the song's markers (only while stopped).
- **Pattern names from**: which sequence the Patterns column is read from. A
  machine whose name starts with the chosen one fills in where the chosen one
  has no patterns (so `DG_01_A48plus` covers sections 25 to 28 for `DG_01_A`).

### Parameters

All are switches except At End, so they can be bound to MIDI or written in a
pattern.

| Parameter | Action |
|---|---|
| Enable | Off: the loop is released to the full range and left alone |
| Hold | As the button |
| Next | Fires on 0 to 1 |
| One More | Fires on 0 to 1 |
| To Start | Fires on 0 to 1 |
| At End | Hold last / Back to start |

Next, One More and To Start fire on the change from 0 to 1, so a momentary
pedal (127 then 0) fires once per press. In the parameter window they must be
set back to 0 before they can fire again; the window buttons have no such
limit.

Play and Stop are window buttons only. A Play parameter would fire when a song
saved with it set is loaded.

Repeat counts, labels and the layout are saved with the song.

## Behaviour to know

- **Stopping** puts the loop markers back to the full range. Markers you move
  while stopped are left alone until you press play.
- **Starting mid-section** counts the rest of that section as pass 1.
- **Sections play in timeline order.** Skipping forward to an arbitrary section
  is not in this version.
- **A pattern that started in an earlier section is not re-triggered** when a
  later section loops. This is ReBuzz's normal loop behaviour.
- **Rendering to disk**: switch Enable off first. The recorder uses the loop
  markers as its range and stops at the first wrap.
- **Timing margin**: marker changes are made on the UI thread, a whole pass
  ahead of when they are needed. If the UI thread stalls for longer than one
  section, a section can repeat once more than asked or wrap to the previous
  section's start. A marker change is never made if it would jump the play
  position.

## Files

- `LoopEngine.cs`: the section logic. No ReBuzz or WPF types.
- `PedalMasterLoop.cs`: machine, parameters, song state, marker updates.
- `MasterLoopGui.cs`: the window.
- `tests/sim_loop.py`: simulation of the logic against a model of ReBuzz's
  song position and loop setters (`python3 tests/sim_loop.py`).

## Test status

- The logic passes the simulation: counts, Hold, Next, One more, To start, both
  At End modes, stop and resume, Play from stopped and while playing, disable
  while playing, 400 fuzz runs with random presses and UI delay, 200 runs with
  long UI stalls.
- The simulation runs a Python port of `LoopEngine.cs`, not the C# itself.
- The C# has been syntax-checked only. It has not been compiled or run in
  ReBuzz: no .NET SDK was available where it was written.

## Licence

GNU General Public License v3.0. See `LICENSE`.
