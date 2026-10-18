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
  Link_Leavitt: { label: 'Pulse', who: 'Henrietta Leavitt', href: 'games/star/', game: 'star' },
  Link_Ride:    { label: 'Orbital Catch',         who: 'Sally Ride',        href: 'games/satellite/', game: 'satellite' },
  Link_Jackson: { label: 'Turbulent Trial',       who: 'Mary Jackson',      href: 'games/shockwave/', game: 'shockwave' },
};

// Ground under the launch pad (a big soft-edged disk). Set GROUND = null to remove it.
export const GROUND = { under: 'Container Bases', radius: 6000, color: '#d6dbc4', sink: 1, until: 5.5 };   // until = hidden after this guide

// Earth: scale the curved Earth patch up so its edges stay out of view (1 = as exported).
// It grows around its top point, so it stays the same height under the camera.
export const EARTH = { name: 'Earth', scale: 3 };

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
  object: 'Link_Jackson', startAt: 'Popup_Jackson_4+0.2', passAt: 'SkyZone_1', endAt: 'Popup_Ride_1-0.6',
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
//   image:  local picture (public/images/...). imageUrl: online copy used if the local file is missing.
//   meta:   small line under the name.   cite: { label, href } small source link at the bottom.
//   game:   which game this card belongs to (clicking that belt star at the end flies back to the first one).
// Decimals are fine (5.5 = halfway between guide 5 and 6). `side`: 'left' | 'right' | 'center'.
// PLACEHOLDER TEXT: replace with your own.
export const POPUPS = [
  { marker: 'Popup_Intro', at: 1, side: 'center', kicker: 'Girls Hoo Hack 2026', title: 'Stellar',
    body: "Three missions. Three women who shaped how we explore space. Complete each one to light a star in Orion's belt.",
    hint: 'Scroll to launch' },

  // ---- Mission 1: Mary Jackson - Turbulent Trial ----
  { marker: 'Popup_Jackson_1',
    at: 1.6,
    side: 'left',
    game: 'shockwave',
    kicker: 'Mission 1 - Turbulent Trial',
    title: 'Mary Jackson',
    meta: 'Engineer  \u2219  1921 - 2005',
    image: 'images/jackson.jpg',
    imageUrl: 'https://upload.wikimedia.org/wikipedia/commons/thumb/b/bd/Mary_Jackson_1979_Portrait_%28LRC-1979-B701_P_F002-07086%29.jpg/480px-Mary_Jackson_1979_Portrait_%28LRC-1979-B701_P_F002-07086%29.jpg',
    cite: { label: 'nasa.gov', href: 'https://www.nasa.gov/people/mary-w-jackson-biography/' } },
  { marker: 'Popup_Jackson_2',
    at: 2.05,
    side: 'right',
    game: 'shockwave',
    kicker: 'Mary Jackson',
    body: "Mary Jackson was a groundbreaking aerospace engineer and mathematician that broke both gender and racial barriers during her academic and engineering career.",
    cite: { label: 'nasa.gov', href: 'https://www.nasa.gov/people/mary-w-jackson-biography/' } },
  { marker: 'Popup_Jackson_3',
    at: 2.5,
    side: 'left',
    game: 'shockwave',
    kicker: 'Mary Jackson',
    body: "She took graduate level classes managed by the University of Virginia, using special permission to join the then-segregated courses. She went on to work as an engineer at NACA (now NASA) and wrote several research papers on turbulence with differing aircraft nose angles.",
    cite: { label: 'nasa.gov', href: 'https://www.nasa.gov/people/mary-w-jackson-biography/' } },
  { marker: 'Popup_Jackson_4',
    at: 2.95,
    side: 'right',
    game: 'shockwave',
    kicker: 'Mary Jackson',
    body: "She was passionate about promoting women joining the STEM workforce and inspired many through her work, with her story also being told in the award-winning movie \"Hidden Figures.\"",
    link: 'Link_Jackson',
    cite: { label: 'nasa.gov', href: 'https://www.nasa.gov/people/mary-w-jackson-biography/' } },

  // ---- Mission 2: Sally Ride - Orbital Catch ----
  { marker: 'Popup_Ride_1',
    at: 7.8,
    side: 'right',
    game: 'satellite',
    kicker: 'Mission 2 - Orbital Catch',
    title: 'Sally Ride',
    meta: 'Astronaut and Physicist  \u2219  1951 - 2012',
    image: 'images/ride.jpg',
    imageUrl: 'https://upload.wikimedia.org/wikipedia/commons/thumb/0/0c/Sally_Ride_%281984%29.jpg/480px-Sally_Ride_%281984%29.jpg',
    cite: { label: 'science.nasa.gov', href: 'https://science.nasa.gov/people/sally-ride/' } },
  { marker: 'Popup_Ride_2',
    at: 8.25,
    side: 'left',
    game: 'satellite',
    kicker: 'Sally Ride',
    body: "Sally Ride was the first American woman in space and a pioneer in space research and discovery.",
    cite: { label: 'science.nasa.gov', href: 'https://science.nasa.gov/people/sally-ride/' } },
  { marker: 'Popup_Ride_3',
    at: 8.7,
    side: 'right',
    game: 'satellite',
    kicker: 'Sally Ride',
    body: "After her education at Stanford, she joined NASA's first co-ed astronaut class in 1978. She completed two missions in space and had a successful career as both an astronaut and as a professor and institute director after she left NASA.",
    cite: { label: 'science.nasa.gov', href: 'https://science.nasa.gov/people/sally-ride/' } },
  { marker: 'Popup_Ride_4',
    at: 9.15,
    side: 'left',
    game: 'satellite',
    kicker: 'Sally Ride',
    body: "Throughout her life, she was passionate about encouraging girls and young women to get involved in STEM, and she has served as an inspiration for thousands.",
    link: 'Link_Ride',
    cite: { label: 'science.nasa.gov', href: 'https://science.nasa.gov/people/sally-ride/' } },

  // ---- Mission 3: Henrietta Leavitt - Pulse ----
  { marker: 'Popup_Leavitt_1',
    at: 10.6,
    side: 'left',
    game: 'star',
    kicker: 'Mission 3 - Pulse',
    title: 'Henrietta Leavitt',
    meta: 'Astronomer  \u2219  1868 - 1921',
    image: 'images/leavitt.jpg',
    imageUrl: 'https://cdn.britannica.com/66/158566-004-EBCD4212/Henrietta-Swan-Leavitt.jpg',
    cite: { label: 'womensinnovations.org', href: 'https://womensinnovations.org/women-innovator/henrietta-swan-leavitt/' } },
  { marker: 'Popup_Leavitt_2',
    at: 11.05,
    side: 'right',
    game: 'star',
    kicker: 'Henrietta Leavitt',
    body: "Henrietta Leavitt was a pioneer in astronomy research and is the reason why we can measure incredible astronomical distances in space.",
    cite: { label: 'womensinnovations.org', href: 'https://womensinnovations.org/women-innovator/henrietta-swan-leavitt/' } },
  { marker: 'Popup_Leavitt_3',
    at: 11.5,
    side: 'left',
    game: 'star',
    kicker: 'Henrietta Leavitt',
    body: "Despite the restrictions placed on women, such as bans from using the research telescopes at her Harvard College Observatory, she made several groundbreaking findings on variable stars.",
    cite: { label: 'womensinnovations.org', href: 'https://womensinnovations.org/women-innovator/henrietta-swan-leavitt/' } },
  { marker: 'Popup_Leavitt_4',
    at: 11.95,
    side: 'right',
    game: 'star',
    kicker: 'Henrietta Leavitt',
    body: "She discovered that stars have periods of brightness and dimness, which could be used to calculate how bright stars are. Her discovery allowed for breakthroughs on how to measure distances between objects in space.",
    cite: { label: 'womensinnovations.org', href: 'https://womensinnovations.org/women-innovator/henrietta-swan-leavitt/' } },
  { marker: 'Popup_Leavitt_5',
    at: 12.4,
    side: 'left',
    game: 'star',
    kicker: 'Henrietta Leavitt',
    body: "Despite her limited recognition during her lifetime, she serves as an inspiration for breaking through gender-based barriers and making an impact on the world of astronomical research.",
    link: 'Link_Leavitt',
    cite: { label: 'womensinnovations.org', href: 'https://womensinnovations.org/women-innovator/henrietta-swan-leavitt/' } },
];
