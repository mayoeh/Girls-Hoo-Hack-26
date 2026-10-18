# Stellar website (3D scroll flythrough)

React Three Fiber + Theatre.js. The scene is `public/models/SiteMap.glb`, exported from Unity's Site Map scene.

## Run it
```
cd Website
npm install        # first time only
npm run dev        # http://localhost:5173
```
- **Theatre editor:** http://localhost:5173/?studio
- **Demo helpers:** `?unlock=all` lights every star, `?unlock=star,satellite` lights some, `?reset` clears saved progress.

## What to edit
- **`src/config.js`**: popup text and which guide each popup appears at, game links, skies, scroll speed, field of view.
- **Camera path:** comes from the `CamGuide_` objects in Unity. Move them in Unity and re-export.
- **Timing and look in Theatre** (`?studio`):
  - *Camera Flight → easeAtGuides*: how much the camera slows down at each guide.
  - *Camera Flight → useProgressKeyframes*: switch on, then keyframe `progress` on the timeline to set your own pacing.
  - *Camera Flight → mode = Free camera*: keyframe the `Camera` object by hand, as in the Codrops tutorial.
  - *Look*: bloom, sun, ambient light and exposure.
  - Theatre edits are kept in your browser only. To keep them for good, click the project name (**Stellar**) in the editor's outline, press **Export**, and save the file over `src/theatre-state.json`.

## New export from Unity
1. Export `SiteMap_Root` as **.glb** only.
2. Shrink it: run `node tools/optimize-glb.mjs <exported.glb> public/models/SiteMap.glb` (needs `npm i @gltf-transform/core @gltf-transform/extensions @gltf-transform/functions meshoptimizer sharp` in `tools/`), or ask Claude to run it.
3. Keep object and material names the same: `CamGuide_*`, `SkyZone_*`, `Link_*`, `Star_*`, `Connection Material`, `Mat_Star_Constellation`.

## Games
Put each game's Unity WebGL build in `public/games/<star|satellite|shockwave>/`. When a game is won, call
`StellarProgress.MarkComplete("satellite")` (in `Assets/Plugins/StellarProgress.cs`) to light its star.
The build has to be served from the same site as this page for that to work.

## Put it online (Vultr)
`npm run build`, then upload the `dist/` folder. It works from any folder, because paths are relative.
