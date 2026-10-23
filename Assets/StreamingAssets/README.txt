fluid-config.json
=================

Display layout and per-display resolution and window mode.

EASIEST: use the WINDOW MANAGER button (top-right of the control screen).
It sets how many displays are in use and each display's width, height and
mode (Windowed / Borderless). "Save & apply" changes the running app
immediately AND writes this file - no restart, no hand-editing. Everything
below is only needed if you want to edit the file by hand.

In a build this file lives at:
    <YourBuildFolder>/FluidSim_Data/StreamingAssets/fluid-config.json


Default layout (4 displays)
---------------------------
    display 1          display 2   display 3   display 4
    [ controls ]       [ fluid  |    fluid   |   fluid  ]
                       <------ one continuous fluid ---->


Example
-------
    {
        "windowMode": 0,
        "controlDisplay": 0,
        "fluidDisplays": [1, 2, 3],
        "activeScreens": 3,
        "displays": [
            { "display": 0, "width": 1920, "height": 1080 },
            { "display": 1, "width": 1920, "height": 1080 },
            { "display": 2, "width": 2560, "height": 1440 },
            { "display": 3, "width": 1920, "height": 1080 }
        ]
    }


Fields
------
displays
    One entry per display, each with its own resolution.
      display   display index. 0 = display 1, 1 = display 2, and so on.
      width     width in pixels.  0 = the monitor's native width.
      height    height in pixels. 0 = the monitor's native height.
      mode      "windowed" or "borderless". Optional:
                  display 1 without it follows windowMode;
                  displays 2+ without it are borderless.
                Displays 2+ can never have a title bar (a Unity limit):
                "windowed" there is a frameless window of that size centred
                on its monitor. "borderless" fills the monitor and renders
                at width x height, scaled to the panel.

    Each display must be listed only ONCE. A second entry for the same
    display is ignored (with a warning in the log).

    Set width AND height to 0 to run that display at its native resolution.
    Setting only one of them is treated as native. A display with no entry at
    all also runs at native.

    The fluid screens may all differ. The wall is as wide as their widths
    added together (e.g. 1920 + 2560 + 1920 = 6400) and as tall as the tallest
    one. Each screen gets a slice proportional to its own width, so the fluid
    keeps the same scale across the seams instead of stretching on a larger
    panel. Every screen shows the full height of the field, so a shorter panel
    simply shows it at fewer pixels.

fluidDisplays
    Which displays the fluid spans, left to right. 0 = display 1.
    Default [1, 2, 3] = displays 2, 3 and 4.

    This is ONE simulation stretched across them, not a copy per screen, so a
    swirl flows continuously from one screen into the next.

    Order matters - it is the physical left-to-right order of the monitors. If
    the picture comes out in the wrong order, reorder this list, e.g. [3, 2, 1].

activeScreens
    How many of the fluidDisplays screens the fluid uses, counted from the
    left of that list. 0 (or missing) = all of them.
        1 = display 2 only
        2 = displays 2 and 3
        3 = displays 2, 3 and 4
    It can never be more than the number of entries in fluidDisplays - to get
    4 fluid screens, fluidDisplays needs 4 entries, e.g. [1, 2, 3, 4]. The
    window manager's "displays in use" does both for you.

    Displays not in use are not opened at launch, so a spare monitor keeps
    showing the desktop. Taking a display OUT of use while running leaves it
    black until the next launch (Windows cannot close a display window once
    it is open); touches there are ignored.

    To use a different screen for single-screen mode, reorder fluidDisplays:
    e.g. [2, 1, 3] with activeScreens 1 puts the fluid on display 3 only.

controlDisplay
    Display showing the control panel. 0 = display 1.
    Must not appear in fluidDisplays.

windowMode
    Applies to DISPLAY 1 ONLY (the main window - normally the control screen).
    Unity always opens displays 2 and up as a window filling their monitor, so
    this setting does not affect the fluid screens.

    0 = Windowed    a normal window, sized to display 1's width/height
    1 = Borderless  fills the monitor with no title bar. The width/height are
                    rendered then upscaled to fit, so they change the render
                    quality, not the window size.

    Exclusive fullscreen is not available. Windows minimises an exclusive
    fullscreen window the moment it loses focus, which happens whenever another
    screen is clicked. That is OS behaviour and cannot be worked around from
    inside the app. A value of 2 here falls back to Borderless.


How resolution is applied
-------------------------
Display 1 is the main window: its width/height resize the window itself
(visible in Windowed mode).

Displays 2 and up always fill their monitor - that is how Unity multi-display
works and cannot be changed. For those, width/height set the RENDER resolution,
which is then scaled to the panel. So 1280 x 720 on a 1080p monitor renders
at 720p and upscales; it does not shrink the picture to a 1280 x 720 box.


fluid-settings.json
===================

Everything the control panel tunes - simulation, appearance, bloom, sunrays and
timing. Created when you press "Save settings" in the panel, and loaded on the
next launch. If it is absent the app starts from its built-in defaults.

It is kept separate from fluid-config.json on purpose, so the display config
above stays easy to hand-edit.

Attract mode (also in fluid-settings.json, and on the control panel)
    idleMode      true = when nobody is touching, virtual fingers wander across
                  the wall so it is never a black screen. The app starts this
                  way. A touch on any fluid screen takes over immediately.
    idleDelay     seconds without a touch before attract mode resumes (default 8)
    idlePointers  how many virtual fingers (1-8, default 3)
    idleSpeed     how fast they wander (default 0.12)
    attractDuringInteraction
                  true  = the virtual fingers keep running while visitors touch,
                          so real strokes mix with the attract motion (default)
                  false = they step aside the moment someone touches a fluid
                          screen, and return after idleDelay

Color palette (also in fluid-settings.json)
    EASIEST: use the COLOR MANAGER button (top-right of the control screen).

    Every stroke starts on colour 1 and blends through the palette in order, so
    the head of a stroke shows the newest colour and its tail keeps the earlier
    ones. Each attract finger starts a colour further along, so they differ.

    In the colour manager:
      Random / Palette   random rainbow colours (the original demo) or yours
      palette            1 to 8 colours. Tap one to edit it; "+ Add" inserts a
                         new colour after it, "Remove" deletes it, < > reorder.
      editor             drag in the square (saturation / brightness) and the
                         bar (hue), tap a preset, or tap the colour-code box
                         and type FF0000, #FF0000, #F00 or a name like red.
      time per color     seconds to blend from one colour to the next
      after last color   Loop = blend back to colour 1 and keep cycling
                         Stay = stay on the last colour
      Apply              use it now (not saved)
      Save & apply       use it now and keep it for the next launch. Only the
                         palette is written; other saved values are untouched.
      Close              discard anything not applied

    The main panel's COLOR section has a quick "use color palette" on/off and a
    preview strip of one stroke, start to end.

    Keys:
      usePalette              true = palette, false = random colours (default)
      palette                 list of colours, e.g.
                              [ { "r": 0.0, "g": 0.6, "b": 1.0, "a": 1.0 },
                                { "r": 1.0, "g": 0.0, "b": 0.0, "a": 1.0 } ]
                              values 0..1, first = where a stroke starts
      paletteSecondsPerColor  0.1 - 10 (default 1.5)
      paletteLoop             true = loop (default), false = stay on last

Animation style (also in fluid-settings.json)
    EASIEST: the STYLE button (top-right of the control screen). Tap a card
    and the wall switches immediately. Every style runs the SAME simulation,
    so all sliders (force, velocity, vorticity, dissipation, colours, logo
    spotlight...) and the mouse / touch / attract behaviour stay exactly the
    same - only the look changes.

      0 Classic         the original: coloured dye, shading, bloom, sunrays
      1 Water           deep blue water with glitter swirling in the current
      2 Shooting Stars  a night sky; strokes throw white-hot shooting stars
      3 Comets          glowing comet heads with long coloured tails
      4 Neon            the fluid traced as glowing neon outlines on black

    Shooting Stars and Comets draw ONE star / comet per finger (and per
    attract finger): a glowing head at the finger and a single tail along
    the path it just travelled, wide at the head and tapering to a point,
    with sparkle dust along it. Shooting stars: smaller white-hot head, thin
    tail. Comets: big head, wide tail. When the finger stops, the tail
    shrinks back into the head; when it lifts, the comet fades away. The
    colour is the palette colour (or the stroke's random colour).

    Swirls: the "vorticity" slider (SIMULATION) adds the small curly spirals
    to the fluid in every style. 0 = smooth flowing shapes, no spirals
    (the default); around 30 = the original demo's curly smoke.

    "Save as default" in the picker stores only this choice.
    Key: animationStyle  0 - 4 (default 0)

Logo spotlight (also in fluid-settings.json, and on the control panel - LOGO SPOTLIGHT)
    The logo image is HIDDEN behind the fluid. It only shows inside a soft
    circular spotlight that follows the mouse while it hovers over a fluid
    screen, and every finger touching one. The fluid stays on top, swirling
    over the logo. Outside the spotlights the wall is the normal fluid on black.
    A spotlight fades in quickly and fades out gently when the pointer leaves.
    Attract-mode fingers do NOT reveal the logo (see spotlightFollowsAttract).

    Panel:
      spotlight size    radius of the circle (fraction of the wall height)
      fluid over logo   fluid opacity inside the spotlight.
                        0 = clean logo, 1 = the fluid hides it
      logo outside      logo brightness outside the spotlights.
                        0 = hidden (default), 1 = always visible

    The image is "img with 3 logo.jpg.jpeg" from the project's Assets folder,
    built into the app. To use a different image WITHOUT rebuilding, put it in
    this StreamingAssets folder and set revealImagePath to its file name.

    Keys:
      revealMode        true = on (default), false = plain coloured fluid
      revealImagePath   "" = the built-in image (default). Otherwise a PNG or
                        JPG: a file name in StreamingAssets, e.g. "logo.png",
                        or a full path, e.g. "D:/Media/logo.png"
      revealFit         0 = Fill wall   (cover the whole wall, crop edges)
                        1 = Fit wall    (whole image, black bars at the sides)
                        2 = Stretch     (to the wall's exact shape)
                        3 = Each screen (a full copy on every fluid screen)
      spotlightSize     0.03 - 0.6 (default 0.2). Spotlight radius.
      revealFluidOpacity 0 - 1 (default 0.35). Fluid opacity inside a spotlight.
      revealBackground  0 - 1 (default 0). Logo brightness outside spotlights.
      spotlightFollowsAttract
                        false (default) = only real visitors reveal the logo;
                        true = attract-mode fingers carry spotlights too, so
                        the logo glimpses through while the wall is idle.

Attract mode notes
    Only input on a FLUID screen counts as a visitor. Touching or clicking the
    control screen does not stop attract mode, and neither does a mouse cursor
    that is just resting - only one that moves.

"Reset to defaults" deletes both files.


Notes
-----
Resolution, window mode and the number of displays can all be changed while
running, from the window manager.

If the install folder is read-only, both files are written instead to:
    %USERPROFILE%/AppData/LocalLow/<Company>/<Product>/


Command line
------------
Overrides this file, useful for a startup script:

    FluidSim.exe -fluid-width 1920 -fluid-height 1080 -fluid-window windowed

-fluid-width / -fluid-height set EVERY fluid screen to that size (the control
screen keeps its own entry). -fluid-window accepts windowed | borderless.
