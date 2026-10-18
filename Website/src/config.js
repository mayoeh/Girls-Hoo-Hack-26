// ============================================================================
//  EVERYTHING YOU'LL WANT TO EDIT LIVES IN THIS FILE.
//  Save and the page reloads by itself (while `npm run dev` is running).
// ============================================================================

// The 3D scene exported from Unity (public/models/...).
export const MODEL_URL = 'models/SiteMap.glb';

// Camera guides: 'auto' = every CamGuide_* object in the model, in number order, CamGuide_End last.
// Add, remove or renumber them in Unity and re-export; nothing to change here.
// (You can still list names by hand instead: ['CamGuide_01', 'CamGuide_02', ...].)
export const GUIDES = 'auto';

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

// Shuttle launch (scroll-driven). Each timing is a guide number (like `at` in POPUPS) or a Unity marker
// name with an optional offset in guides, e.g. 'Popup_Ride-0.6' = a bit before the Ride popup marker.
//   startAt: ignition + lift-off begins     passAt: it flies right past the camera (put this on the sky change)
//   endAt:   it has arrived at the orbit spot (Satellite Catch shuttle)   passDistance: how close it passes (in shuttle-heights)
//   veilStrength: how much the exhaust covers the screen at passAt (0 = not at all, 1 = fully)
// Set LAUNCH = null to turn the launch off.
export const LAUNCH = {
  object: 'Link_Jackson', startAt: 'Popup_Jackson_3+0.2', passAt: 'SkyZone_1', endAt: 'Popup_Ride_1-0.6',
  fallback: { startAt: 3.2, passAt: 5, endAt: 7.4 },   // used when those markers aren't in the model
  passDistance: 0.9, veilWidth: 0.55, veilStrength: 0.92,
  // Where it ends up: the stack lines up with the Satellite Catch shuttle and swaps with it.
  arrive: { object: 'Link_Ride', match: ['Shuttle_Rig_1', 'Shuttle_2'], tank: 'Shuttle_Tank_1' },
};

// Scroll snapping: when the visitor stops scrolling near one of these guide numbers, the page
// glides to it. Defaults to every popup's `at` plus the end. Set SNAP_RANGE to 0 to turn snapping off.
export const SNAP_RANGE = 0.45;   // how close (in guides) you must stop for it to snap
export const SNAP_DELAY = 160;    // ms of no scrolling before it snaps

// Little hint at the very end (no card over the constellation). Set to '' to hide it.
export const END_HINT = 'Hover a star to see its mission';

// Popups along the scroll.
//   marker: name of a Popup_ empty in Unity. The card shows when the camera passes closest to it.
//   at:     guide number (1 = CamGuide_01) - used when there's no marker, or the marker isn't in the model.
//   offset: optional nudge in guides (e.g. 0.3 = a bit later than the marker).
// Decimals are fine (5.5 = halfway between guide 5 and 6). `side`: 'left' | 'right' | 'center'.
// PLACEHOLDER TEXT: replace with your own.
export const POPUPS = [
  { marker: 'Popup_Intro', at: 1, side: 'center', kicker: 'Girls Hoo Hack 2026', title: 'Stellar',
    body: "Three missions. Three women who shaped how we explore space. Complete each one to light a star in Orion's belt.",
    hint: 'Scroll to launch' },

  // ---- Mary Jackson (3) ----
  { marker: 'Popup_Jackson_1', at: 1.8, side: 'left', kicker: 'Mission 3 - Engineer', title: 'Mary Jackson',
    body: "In 1958 Mary Jackson became NASA's first Black female engineer, working at Langley Research Center in Virginia." },
  { marker: 'Popup_Jackson_2', at: 2.4, side: 'right', kicker: 'Mary Jackson - The research', title: 'Smooth or turbulent?',
    body: "She studied how air flows over cone-shaped models in a supersonic wind tunnel, and how the nose angle changes where smooth flow turns turbulent." },
  { marker: 'Popup_Jackson_3', at: 3, side: 'left', kicker: 'Mary Jackson - Your mission', title: 'Shockwave',
    body: "Shape the nose, fire the tunnel, and keep the airflow smooth as far back along the model as you can.",
    link: 'Link_Jackson' },

  // ---- Sally Ride (3) ----
  { marker: 'Popup_Ride_1', at: 8, side: 'right', kicker: 'Mission 2 - Astronaut', title: 'Sally Ride',
    body: "On June 18, 1983, aboard Space Shuttle Challenger (STS-7), Sally Ride became the first American woman in space." },
  { marker: 'Popup_Ride_2', at: 8.6, side: 'left', kicker: 'Sally Ride - The arm', title: 'Release and catch',
    body: "She operated the shuttle's robotic arm, the Canadarm, to release the SPAS-01 satellite into space and catch it again." },
  { marker: 'Popup_Ride_3', at: 9.2, side: 'right', kicker: 'Sally Ride - Your mission', title: 'Satellite Catch',
    body: "Your phone becomes the robotic arm. Tilt it to release the satellite, then bring it home.",
    link: 'Link_Ride' },

  // ---- Henrietta Leavitt (4) ----
  { marker: 'Popup_Leavitt_1', at: 10.8, side: 'left', kicker: 'Mission 1 - Astronomer', title: 'Henrietta Leavitt',
    body: "At the Harvard College Observatory, Henrietta Leavitt measured stars on thousands of glass photographic plates." },
  { marker: 'Popup_Leavitt_2', at: 11.4, side: 'right', kicker: 'Henrietta Leavitt - The discovery', title: 'Stars that pulse',
    body: "She studied Cepheid stars, which brighten and dim in a steady rhythm. In 1912 she showed that the slower a Cepheid pulses, the brighter it truly is." },
  { marker: 'Popup_Leavitt_3', at: 12, side: 'left', kicker: 'Henrietta Leavitt - Why it matters', title: 'Measuring the universe',
    body: "Her rule made Cepheids into cosmic measuring sticks. Edwin Hubble later used it to show that other galaxies lie far beyond our own." },
  { marker: 'Popup_Leavitt_4', at: 12.5, side: 'right', kicker: 'Henrietta Leavitt - Your mission', title: 'Breathe with the Star',
    body: "Follow a pulsing star with your breathing, read live from your webcam.",
    link: 'Link_Leavitt' },
];
