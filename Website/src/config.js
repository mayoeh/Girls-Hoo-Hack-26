// ============================================================================
//  EVERYTHING YOU'LL WANT TO EDIT LIVES IN THIS FILE.
//  Save and the page reloads by itself (while `npm run dev` is running).
// ============================================================================

// The 3D scene exported from Unity (public/models/...).
export const MODEL_URL = 'models/SiteMap.glb';

// Camera guides, in flight order. Each one is a stop on the scroll.
export const GUIDES = [
  'CamGuide_01', 'CamGuide_02', 'CamGuide_03', 'CamGuide_04', 'CamGuide_05', 'CamGuide_06',
  'CamGuide_07', 'CamGuide_08', 'CamGuide_09', 'CamGuide_10', 'CamGuide_11', 'CamGuide_12', 'CamGuide_End',
];

// How many screen-heights of scrolling between two guides (bigger = slower flight).
export const SCROLL_PER_GUIDE = 0.9;

// Vertical field of view, degrees (Unity's Scene view uses 60).
export const FOV = 60;

// Skies: the first one shows at the start; each SkyZone_ marker switches to the next one.
//   cube = six images (Unity "6 Sided" skybox) in a folder: front/back/left/right/up/down.jpg
//   pano  = a real 360-degree panorama (2:1 "equirectangular" image, e.g. 4096x2048). Best option.
//           `turn` (0..1) spins it around you. Example:
//           { name: 'cloudy', type: 'pano', url: 'sky/launch-pano.jpg', turn: 0 },
//   photo = one flat picture wrapped around a sphere. `repeat` = copies around [horizontal, vertical]
//           (mirrored, so there's no seam). More copies = smaller clouds/stars.
export const SKIES = [
  { name: 'cloudy', type: 'pano',  url: 'sky/launch-pano-4k.jpg', turn: 0 },       // start (launch pad)
  { name: 'stars',  type: 'cube',  folder: 'sky/stars' },                         // after SkyZone_1 (StarSkybox04)
  { name: 'deep',   type: 'photo', url: 'sky/deep-space.jpg', repeat: [4, 2] },   // after SkyZone_2
];
export const SKY_ZONES = ['SkyZone_1', 'SkyZone_2'];

// Clickable 3D objects -> pages. `game` is the key that game saves when it's completed.
export const LINKS = {
  Link_Leavitt: { label: 'Breathe with the Star', who: 'Henrietta Leavitt', href: 'games/star/', game: 'star' },
  Link_Ride:    { label: 'Satellite Catch',       who: 'Sally Ride',        href: 'games/satellite/', game: 'satellite' },
  Link_Jackson: { label: 'Shockwave',             who: 'Mary Jackson',      href: 'games/shockwave/', game: 'shockwave' },
};

// Make clickable objects bigger than in Unity so they read from far away (1 = as exported).
// The Leavitt "Pulsing Star" is only 2.5 units across and the camera never gets closer than ~4000.
export const LINK_SCALE = { Link_Leavitt: 300 };

// Which belt star lights up for which game. Hovering a belt star at the end shows its game;
// clicking it flies back to that game's popup.
export const STARS = { star: 'Star_Leavitt', satellite: 'Star_Ride', shockwave: 'Star_Jackson' };

// Materials that light up once all three games are done (the Orion frame).
export const FRAME_LINE_MATERIAL = 'Connection Material';
export const FRAME_STAR_MATERIAL = 'Mat_Star_Constellation';

// Shuttle launch (scroll-driven). Numbers are guide numbers like `at` in POPUPS.
//   startAt: ignition + lift-off begins     passAt: it flies right past the camera (put this on the sky change)
//   endAt:   it has arrived at the orbit spot (Satellite Catch shuttle)   passDistance: how close it passes (in shuttle-heights)
//   veilStrength: how much the exhaust covers the screen at passAt (0 = not at all, 1 = fully)
// Set LAUNCH = null to turn the launch off.
export const LAUNCH = {
  object: 'Link_Jackson', startAt: 2.2, passAt: 5, endAt: 8.4, passDistance: 0.9, veilWidth: 0.55, veilStrength: 0.92,
  // Where it ends up: the stack lines up with the Satellite Catch shuttle and swaps with it.
  arrive: { object: 'Link_Ride', match: ['Shuttle_Rig_1', 'Shuttle_2'], tank: 'Shuttle_Tank_1' },
};

// Scroll snapping: when the visitor stops scrolling near one of these guide numbers, the page
// glides to it. Defaults to every popup's `at` plus the end. Set SNAP_RANGE to 0 to turn snapping off.
export const SNAP_RANGE = 0.45;   // how close (in guides) you must stop for it to snap
export const SNAP_DELAY = 160;    // ms of no scrolling before it snaps

// Little hint at the very end (no card over the constellation). Set to '' to hide it.
export const END_HINT = 'Hover a star to see its mission';

// Popups along the scroll. `at` = guide number (1 = CamGuide_01, 13 = CamGuide_End).
// Decimals are fine (5.5 = halfway between guide 5 and 6). `side`: 'left' | 'right' | 'center'.
// PLACEHOLDER TEXT: replace with your own.
export const POPUPS = [
  { at: 1, side: 'center', kicker: 'Girls Hoo Hack 2026', title: 'Stellar',
    body: "Three missions. Three women who shaped how we explore space. Complete each one to light a star in Orion's belt.",
    hint: 'Scroll to launch' },
  { at: 2, side: 'left', kicker: 'Mission 3 - Engineer', title: 'Mary Jackson',
    body: "Shape a model's nose, fire a supersonic wind tunnel, and keep the airflow smooth for as long as you can.",
    link: 'Link_Jackson' },
  { at: 9, side: 'right', kicker: 'Mission 2 - Astronaut', title: 'Sally Ride',
    body: "Use your phone as the shuttle's robotic arm to release and catch a satellite in orbit.",
    link: 'Link_Ride' },
  { at: 12.5, side: 'right', kicker: 'Mission 1 - Astronomer', title: 'Henrietta Leavitt',
    body: "Follow a pulsing star with your breathing, read live from your webcam.",
    link: 'Link_Leavitt' },
];
