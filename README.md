INSIDE mod patcher
==================

Installs INSIDE mods (.insidepatch files) into your own copy of the game. 

Works on the Steam version of INSIDE.

Each mod only contains the few game objects it changes, so you can install several at once and switch each one
on or off by itself.

No current guide on how to make a patch yourself ,the source is provided as is just for reference.

HOW TO USE
1. Close INSIDE.
2. Put the .insidepatch mods you want into the "mods" folder next to InsidePatcher.exe.
3. Run InsidePatcher.exe. It finds the game by itself (Steam libraries); if not, drag your INSIDE folder into
   the window and press Enter.
4. The list shows every mod and whether it is installed:
     INSTALLED                 the mod is in your game files right now
     not installed             ready to install
     partly installed          only some of its changes are in the game (usually a mod that shares part of it)
     conflicts with #n         changes the same thing as mod n in a different way; only one of the two can be on
     different game version    your game files are not the version the mod was made for; nothing is changed
5. Type a number and press Enter to switch that mod on or off (several at once: 1 3 4).
   A = install all, U = uninstall all (puts the original game files back), D <number> = details,
   R = reload the list after adding mods, Q = quit.

The status is read from the game files themselves, so it is always accurate, also after Steam updates or
"Verify integrity of game files" (which puts the originals back: the mods then show as not installed).

SAFETY
- Before a game file is changed for the first time, an untouched copy is kept next to it
  (<file>.insidepatch-backup). Every change rebuilds the file from that copy with exactly the mods you have on,
  so switching mods on and off never piles up. When the last mod for a file is switched off, the original is
  put back and the copy removed.
- Every result is checked object by object before it is written; if anything doesn't match, the file is left
  as it was.
- Windows may show a SmartScreen warning because the program isn't signed: "More info" -> "Run anyway".

COMMAND LINE
  InsidePatcher.exe list
  InsidePatcher.exe install <number | file name | all>
  InsidePatcher.exe uninstall <number | file name | all>
  options: --game <INSIDE folder>   --mods <mods folder>

INCLUDED MOD
TestSubjectA_WindowMoment - Education Rooms: when the boy walks up to the test chamber, test subject A plays his
  19 s window moment (Gen_HuddleSurgeryA_Moment), which the game never calls, and the chamber curtains follow
  their animation again. It plays once per level load or respawn (the trigger switches itself off after firing,
  as in the original game).
