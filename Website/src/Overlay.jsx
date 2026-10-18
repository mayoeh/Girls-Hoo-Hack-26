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
      POPUPS.forEach((p, i) => {
        const el = cards.current[i];
        if (!el) return;
        const d = Math.abs(u - (p.at - 1));
        const x = Math.min(Math.max((d - 0.22) / 0.28, 0), 1);
        const o = 1 - x * x * (3 - 2 * x);
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
    let el = null, timer = 0, raf;
    const onScroll = () => {
      clearTimeout(timer);
      timer = setTimeout(() => {
        const last = Math.max(1, flightState.count - 1);
        const max = el.scrollHeight - el.clientHeight;
        if (max <= 0) return;
        const u = (el.scrollTop / max) * last;
        const points = [0, last, ...POPUPS.map((p) => p.at - 1)];
        let best = null, bd = Infinity;
        for (const s of points) { const d = Math.abs(s - u); if (d < bd) { bd = d; best = s; } }
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
          <section key={i} ref={(el) => (cards.current[i] = el)} className={`card card-${p.side || 'left'}`}>
            {p.kicker && <div className="kicker">{p.kicker}</div>}
            <h2>{p.title}</h2>
            {p.body && <p>{p.body}</p>}
            {link && (
              <a className="btn" href={link.href}>
                {done[link.game] ? 'Play again' : `Play ${link.label}`}
              </a>
            )}
            {p.hint && <div className="hint">{p.hint}<span className="chev" /></div>}
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
