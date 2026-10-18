// Which games are finished. Each game sets localStorage "stellar_<game>" = "1" when won
// (Unity WebGL builds on the same site can do this through a small .jslib plugin).
// URL helpers for demos:  ?unlock=all   ?unlock=star,satellite   ?reset
import { LINKS } from './config';

const KEY = (g) => 'stellar_' + g;
export const GAMES = Object.values(LINKS).map((l) => l.game);

function safe(fn, fallback) { try { return fn(); } catch { return fallback; } }

export function readProgress() {
  const q = new URLSearchParams(location.search);
  if (q.has('reset')) GAMES.forEach((g) => safe(() => localStorage.removeItem(KEY(g))));
  const unlock = q.get('unlock');
  const forced = unlock === 'all' ? GAMES : unlock ? unlock.split(',') : [];
  const done = {};
  for (const g of GAMES) done[g] = forced.includes(g) || safe(() => localStorage.getItem(KEY(g)) === '1', false);
  return done;
}

export function markDone(game) { safe(() => localStorage.setItem(KEY(game), '1')); }
