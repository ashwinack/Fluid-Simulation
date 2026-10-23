# Fluid Simulation

A real-time GPU fluid simulation for a multi-display touch wall, built in Unity. It is a port of Pavel Dobryakov's [WebGL Fluid Simulation](https://github.com/PavelDoGreat/WebGL-Fluid-Simulation).

Display 1 shows a control panel. Displays 2 and up show one continuous fluid across 1 to 3 screens. The fluid reacts to touch, mouse drag and mouse hover.

## Features

- **Animation styles**: Classic, Water, Shooting Stars, Comets and Neon. Each style has its own settings.
- **Background image spotlight**: a hidden image shows through a soft circle under each finger or the mouse. You can choose the image or upload your own in the BACKGROUND dialog.
- **Color manager**: sets the palette a stroke runs through.
- **Window manager**: sets the number of displays, each one's resolution, and windowed or borderless mode, live.
- **Attract mode**: virtual fingers keep the wall moving while nobody is touching it.
- **Saved settings**: everything is saved from the UI to `Assets/StreamingAssets/fluid-config.json` and `fluid-settings.json`.

## Requirements

- Unity 6000.6.0f1 with the Built-in Render Pipeline
- Windows (standalone build)

## Getting started

1. Open the project folder in Unity Hub.
2. Open the main scene and press Play. In the editor, displays 2 and up are previewed in Game views.
3. To make the app, use **File > Build Profiles**, target Windows, and build.

`Assets/StreamingAssets/README.txt` covers the config files and the controls on site.
