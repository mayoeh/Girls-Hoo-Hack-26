// Tiny shared state between the 3D scene and the HTML overlay (no re-renders per frame).
import { POPUPS, LINKS } from './config';

export const flightState = {
  u: 0,            // position along the guides: 0 = CamGuide_01, 1 = CamGuide_02, ...
  count: 1,        // number of guides
  scrollEl: null,  // the scrolling element
  hover: null,     // 'Link_Ride' for a clickable object, 'star:satellite' for a belt star
  pointer: { x: 0, y: 0 },

  // Glide the scroll to a guide (u = 0-based guide index, decimals ok).
  flyTo(u) {
    const el = this.scrollEl;
    if (!el) return;
    const max = el.scrollHeight - el.clientHeight;
    el.scrollTo({ top: (u / Math.max(1, this.count - 1)) * max, behavior: 'smooth' });
  },
  // Fly back to the popup of a game ('star' | 'satellite' | 'shockwave').
  flyToGame(game) {
    const p = POPUPS.find((p) => p.game === game) || POPUPS.find((p) => p.link && LINKS[p.link]?.game === game);
    if (p) this.flyTo(p.at - 1);
  },
};
if (typeof window !== 'undefined') window.__stellar = flightState;
