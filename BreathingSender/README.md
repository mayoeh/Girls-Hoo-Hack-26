# Breathing Sender (Presage → Unity)

Reads breathing from the webcam with the Presage SmartSpectra C++ SDK and streams it to the
**StarGame** scene over UDP (localhost ports 5005 data, 5006 camera preview).

## Run
1. Double-click `build_and_run.bat`.
2. The first time, it asks for your SDK folder and Presage API key and saves them in
   `local_config.bat` (git-ignored, so the key is never pushed). Delete that file to re-enter them.
3. Press Play on `Assets/Scenes/StarGame.unity`.

The terminal prints `[stats]` every 2 s (camera frames, metrics, samples, rate, status).
In Unity, the debug panel (bottom-left button) shows the same plus a live breathing graph.

## Options
`build_and_run.bat [trace_field] [port] [width] [height] [fps]` — defaults `upper_trace 5005 1280 720 30`.
Try `lower_trace` (belly) if `upper_trace` (chest) is flat.
