import { useEffect, useRef } from 'react';
import { POPUPS, LINKS, SNAP_RANGE, SNAP_DELAY, END_HINT } from './config';
import { flightState } from './flightState';

const gameInfo = (game) => Object.values(LINKS).find((l) => l.game === game);

// HTML cards on top of the 3D view. Each fades in around its guide (`at`) while scrolling.
// Runs its own animation loop and writes styles directly, so scrolling stays smooth.
export default function Overlay({ done }) {
  const cards = useRef([]);
  const tip = useRef(null);
  const bar = useRef(null);
  const hint = useRef(null);

  // ---------- popups, progress bar, hover tooltip ----------
  useEffect(() => {
    let raf;
    const loop = () => {
      const u = flightState.u;
      const last = Math.max(1, flightState.count - 1);
      // Only the card nearest to the camera shows; it hands off to the next one halfway between them.
      let near = -1, nd = Infinity;
      POPUPS.forEach((p, i) => { const d = Math.abs(u - (p.at - 1)); if (d < nd) { nd = d; near = i; } });
      POPUPS.forEach((p, i) => {
        const el = cards.current[i];
        if (!el) return;
        let o = 0;
        if (i === near) {
          const at = p.at - 1, side = Math.sign(u - at) || 1;
          let gap = 0.35;                                   // longest a card stays up on each side
          for (const q of POPUPS) { const d = (q.at - 1 - at) * side; if (q !== p && d > 0.001) gap = Math.min(gap, d / 2); }
          const d = Math.abs(u - at);
          const x = Math.min(Math.max((d - gap * 0.6) / (gap * 0.4), 0), 1);
          o = 1 - x * x * (3 - 2 * x);
        }
        el.style.opacity = o.toFixed(3);
        el.style.transform = `translateY(${((u - (p.at - 1)) * -40).toFixed(1)}px)`;
        el.style.pointerEvents = o > 0.5 ? 'auto' : 'none';
        el.style.visibility = o > 0.001 ? 'visible' : 'hidden';
      });
      if (bar.current) bar.current.style.transform = `scaleX(${(u / last).toFixed(4)})`;
      if (hint.current) hint.current.style.opacity = Math.min(Math.max((u - (last - 0.35)) / 0.3, 0), 1).toFixed(3);

      const h = flightState.hover;
      if (tip.current) {
        tip.current.style.opacity = h ? 1 : 0;
        tip.current.style.transform = `translate(${flightState.pointer.x + 18}px, ${flightState.pointer.y + 18}px)`;
        if (h && tip.current.dataset.shown !== h) {
          tip.current.dataset.shown = h;
          let label, who, cta;
          if (h.startsWith('star:')) {
            const game = h.slice(5), info = gameInfo(game);
            label = info?.label ?? game;
            who = `${info?.who ?? ''}  |  ${done[game] ? 'Star lit' : 'Not lit yet'}`;
            cta = 'Click to fly back';
          } else {
            const info = LINKS[h];
            label = info.label;
            who = `${info.who}${done[info.game] ? '  |  Completed' : ''}`;
            cta = 'Click to play';
          }
          tip.current.querySelector('.tip-label').textContent = label;
          tip.current.querySelector('.tip-who').textContent = who;
          tip.current.querySelector('.tip-cta').textContent = cta;
        }
      }
      raf = requestAnimationFrame(loop);
    };
    loop();
    const move = (e) => { flightState.pointer.x = e.clientX; flightState.pointer.y = e.clientY; };
    window.addEventListener('pointermove', move);
    return () => { cancelAnimationFrame(raf); window.removeEventListener('pointermove', move); };
  }, [done]);

  // ---------- snap scrolling toward the game points ----------
  useEffect(() => {
    if (!SNAP_RANGE || new URLSearchParams(location.search).has('studio')) return;
    let el = null, timer = 0, raf, lastTop = 0, dir = 0;
    const onScroll = () => {
      if (el.scrollTop !== lastTop) { dir = Math.sign(el.scrollTop - lastTop); lastTop = el.scrollTop; }
      clearTimeout(timer);
      timer = setTimeout(() => {
        const last = Math.max(1, flightState.count - 1);
        const max = el.scrollHeight - el.clientHeight;
        if (max <= 0) return;
        const u = (el.scrollTop / max) * last;
        const points = [0, last, ...POPUPS.map((p) => p.at - 1)];
        let best = null, bd = Infinity;
        // Only glide forward (the way you were scrolling) - never pull you back to where you came from.
        for (const s of points) { const d = Math.abs(s - u); if ((s - u) * dir >= -0.02 && d < bd) { bd = d; best = s; } }
        if (best !== null && bd < SNAP_RANGE && bd > 0.01) flightState.flyTo(best);
      }, SNAP_DELAY);
    };
    const attach = () => {   // the scroll element appears once the 3D scene has loaded
      el = flightState.scrollEl;
      if (el) el.addEventListener('scroll', onScroll, { passive: true });
      else raf = requestAnimationFrame(attach);
    };
    attach();
    return () => { cancelAnimationFrame(raf); clearTimeout(timer); if (el) el.removeEventListener('scroll', onScroll); };
  }, []);

  // Let the mouse wheel keep scrolling the flight even when it's over a card.
  const passWheel = (e) => { if (flightState.scrollEl) flightState.scrollEl.scrollTop += e.deltaY; };

  return (
    <div className="overlay" onWheel={passWheel}>
      <div className="progress"><div ref={bar} /></div>
      {POPUPS.map((p, i) => {
        const link = p.link && LINKS[p.link];
        return (
          <section key={i} ref={(el) => (cards.current[i] = el)} className={`card card-${p.side || 'left'}${p.image ? ' card-portrait' : ''}`}>
            {p.image && (
              <div className="photo">
                <img src={p.image} alt={p.title || ''} loading="eager"
                  onError={(e) => { if (p.imageUrl && e.currentTarget.dataset.fallback !== '1') { e.currentTarget.dataset.fallback = '1'; e.currentTarget.src = p.imageUrl; } }} />
              </div>
            )}
            <div className="card-text">
              {p.kicker && <div className="kicker">{p.kicker}</div>}
              {p.title && <h2>{p.title}</h2>}
              {p.meta && <div className="meta">{p.meta}</div>}
              {p.body && <p>{p.body}</p>}
              {link && (
                <a className="btn" href={link.href}>
                  {done[link.game] ? `Play ${link.label} again` : `Play ${link.label}`}
                </a>
              )}
              {p.hint && <div className="hint">{p.hint}<span className="chev" /></div>}
              {p.cite && <a className="cite" href={p.cite.href} target="_blank" rel="noreferrer">Source: {p.cite.label}</a>}
            </div>
          </section>
        );
      })}
      {END_HINT && <div className="end-hint" ref={hint}>{END_HINT}</div>}
      <div className="tip" ref={tip}>
        <div className="tip-label" />
        <div className="tip-who" />
        <div className="tip-cta" />
      </div>
    </div>
  );
}
