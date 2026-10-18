import { useEffect, useMemo, useRef, useState } from 'react';
import * as THREE from 'three';
import { useFrame, useThree } from '@react-three/fiber';
import { useGLTF, useScroll } from '@react-three/drei';
import { PerspectiveCamera as TheatreCamera } from '@theatre/r3f';
import { val } from '@theatre/core';
import { sheet, flight, look } from './theatre';
import { flightState } from './flightState';
import { MODEL_URL, GUIDES, FOV, LINKS, LINK_SCALE, STARS, FRAME_LINE_MATERIAL, FRAME_STAR_MATERIAL, SKY_ZONES } from './config';
import Sky from './Sky';
import Launch from './Launch';

const FLIP = new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0, 1, 0), Math.PI);
const smooth = (x) => x * x * (3 - 2 * x);
const smoothstep = (a, b, x) => smooth(THREE.MathUtils.clamp((x - a) / (b - a), 0, 1));

function useTheatreValues(obj) {
  const [v, setV] = useState(obj.value);
  useEffect(() => obj.onValuesChange(setV), [obj]);
  return v;
}

export default function Scene({ done }) {
  const { scene: model } = useGLTF(MODEL_URL, false, true);
  const scroll = useScroll();
  const { camera, gl } = useThree();
  const flightVals = useTheatreValues(flight);
  const lookVals = useTheatreValues(look);

  // ---------- one-time setup of the loaded model ----------
  const rig = useMemo(() => {
    model.updateMatrixWorld(true);
    const get = (n) => model.getObjectByName(n);
    const missing = [];

    // Camera guides -> a smooth path through their positions, plus their rotations.
    const pts = [], quats = [];
    for (const n of GUIDES) {
      const o = get(n);
      if (!o) { missing.push(n); continue; }
      pts.push(o.getWorldPosition(new THREE.Vector3()));
      // UnityGLTF mirrors X on export, so a guide's Unity "forward" ends up on its +Z axis.
      // Cameras look down -Z, so turn each guide around 180° to look where it looked in Unity.
      quats.push(o.getWorldQuaternion(new THREE.Quaternion()).multiply(FLIP));
    }
    const curve = new THREE.CatmullRomCurve3(pts, false, 'centripetal');

    // Sky zone markers -> the guide index they sit closest to.
    const zoneAt = SKY_ZONES.map((n) => {
      const o = get(n);
      if (!o) { missing.push(n); return Infinity; }
      const p = o.getWorldPosition(new THREE.Vector3());
      let best = 0, bd = Infinity;
      pts.forEach((q, i) => { const d = q.distanceTo(p); if (d < bd) { bd = d; best = i; } });
      return best;
    });

    // Materials we animate.
    const frameMats = new Set(), starMats = {};
    const linkOf = new Map(); // mesh -> Link_ name
    model.traverse((o) => {
      if (!o.isMesh) return;
      o.frustumCulled = true;
      const mats = Array.isArray(o.material) ? o.material : [o.material];
      for (const m of mats) {
        if (m.name === FRAME_LINE_MATERIAL) {
          m.transparent = true; m.depthWrite = false;
          m.userData.baseOpacity = m.opacity;
          frameMats.add(m);
        }
        if (m.name === FRAME_STAR_MATERIAL) frameMats.add(m);
        // Very bright emissives (the Sun is 31.8x) overflow the bloom buffers and black out the frame.
        if (m.emissiveIntensity > 6) m.emissiveIntensity = 6;
        m.userData.baseEmissive = m.emissive ? m.emissive.clone() : null;
        m.userData.baseIntensity = m.emissiveIntensity ?? 1;
      }
      // Is this mesh inside a Link_ object? Give it its own material copy so hover glow is per-link.
      for (let p = o; p; p = p.parent) {
        if (LINKS[p.name]) {
          const copy = (m) => { const c = m.clone(); c.userData.baseEmissive = m.emissive ? m.emissive.clone() : null; return c; };
          o.material = Array.isArray(o.material) ? o.material.map(copy) : copy(o.material);
          linkOf.set(o, p.name);
          break;
        }
      }
    });
    for (const [game, name] of Object.entries(STARS)) {
      const o = get(name);
      if (!o) { missing.push(name); continue; }
      o.traverse((c) => { if (c.isMesh) starMats[game] = c.material; });
    }
    for (const n of Object.keys(LINKS)) if (!get(n)) missing.push(n);
    for (const [n, k] of Object.entries(LINK_SCALE || {})) {   // relative to the exported size, so it's safe if this runs twice
      const o = get(n); if (!o) continue;
      if (!o.userData.baseScale) o.userData.baseScale = o.scale.clone();
      o.scale.copy(o.userData.baseScale).multiplyScalar(k);
    }
    model.updateMatrixWorld(true);
    if (missing.length) console.warn('[Stellar] Not found in the model:', missing.join(', '));

    const sun = get('Sun');
    const sunPos = sun ? sun.getWorldPosition(new THREE.Vector3()) : new THREE.Vector3(-1, 0.3, 0.4).multiplyScalar(10000);
    const linkMeshes = {};
    for (const [mesh, name] of linkOf) (linkMeshes[name] ||= []).push(mesh);
    return { pts, curve, quats, count: pts.length, zoneAt, frameMats: [...frameMats], starMats, linkOf, linkMeshes, sunPos, missing };
  }, [model]);

  flightState.count = rig.count;
  if (typeof window !== 'undefined') { window.__three = { camera, model, gl }; }
  useEffect(() => { flightState.scrollEl = scroll.el; }, [scroll.el]);
  useEffect(() => { gl.toneMappingExposure = lookVals.exposure; }, [gl, lookVals.exposure]);

  // ---------- the camera flight ----------
  const tmpQ = useMemo(() => new THREE.Quaternion(), []);
  const lastOffset = useRef(-1);
  useFrame((state) => {
    // Scrolling moves the Theatre timeline. Only push when the scroll actually changed,
    // so you can still drag the timeline yourself in the editor (?studio).
    const seqLen = val(sheet.sequence.pointer.length);
    if (Math.abs(scroll.offset - lastOffset.current) > 1e-5) {
      lastOffset.current = scroll.offset;
      sheet.sequence.position = scroll.offset * seqLen;
    }
    const seqT = seqLen > 0 ? sheet.sequence.position / seqLen : scroll.offset;

    const last = rig.count - 1;
    const p = flightVals.useProgressKeyframes ? flight.value.progress : seqT;
    const u = THREE.MathUtils.clamp(p, 0, 1) * last;
    flightState.u = u;
    if (flightVals.mode === 'guides' && rig.count >= 2) {
      const i = Math.min(Math.floor(u), last - 1);
      const f = u - i;
      const e = THREE.MathUtils.lerp(f, smooth(f), flightVals.easeAtGuides);
      rig.curve.getPoint((i + e) / last, camera.position);
      tmpQ.slerpQuaternions(rig.quats[i], rig.quats[i + 1], e);
      camera.quaternion.copy(tmpQ);
      camera.updateMatrixWorld();
    }
    // Things that must follow the camera exactly this frame (the launch) run right after it moves.
    flightState.afterCamera?.(state);
  });

  // ---------- stars, frame, hover glow ----------
  const allDone = Object.values(done).every(Boolean);
  const onColor = useMemo(() => new THREE.Color('#dfe8ff'), []);
  const frameColor = useMemo(() => new THREE.Color('#9db8ff'), []);
  const hoverColor = useMemo(() => new THREE.Color('#7f9cff'), []);
  const pulseColor = useMemo(() => new THREE.Color('#fff2c4'), []);
  useFrame(({ clock }) => {
    const t = clock.elapsedTime;
    // Lit stars bloom in as the camera arrives at the end.
    const reveal = smoothstep(rig.count - 2.2, rig.count - 1.15, flightState.u);
    Object.entries(rig.starMats).forEach(([game, m], k) => {
      if (!m.emissive) return;
      const hovered = flightState.hover === 'star:' + game;
      if (done[game]) {
        m.emissive.copy(onColor);
        m.emissiveIntensity = THREE.MathUtils.lerp(1.5, 7 + 1.2 * Math.sin(t * 2.2 + k * 2), reveal) + (hovered ? 4 : 0);
      } else if (hovered) {
        m.emissive.copy(hoverColor);
        m.emissiveIntensity = 1.6;   // a faint glow so you can tell which one you're on
      } else {
        if (m.userData.baseEmissive) m.emissive.copy(m.userData.baseEmissive);
        m.emissiveIntensity = m.userData.baseIntensity;
      }
    });
    const frame = allDone ? reveal : 0;
    for (const m of rig.frameMats) {
      if (m.name === FRAME_LINE_MATERIAL) {
        m.opacity = THREE.MathUtils.lerp(m.userData.baseOpacity, 0.9, frame);
        m.emissive.copy(frameColor);
        m.emissiveIntensity = THREE.MathUtils.lerp(0.05, 3, frame);
      } else if (m.emissive) {
        m.emissive.lerpColors(m.userData.baseEmissive, onColor, frame);
        m.emissiveIntensity = THREE.MathUtils.lerp(m.userData.baseIntensity, 4, frame);
      }
    }
    // Hover glow on clickable objects; the Leavitt star always pulses (it's a Cepheid, after all).
    for (const [name, meshes] of Object.entries(rig.linkMeshes)) {
      const hovered = flightState.hover === name;
      for (const mesh of meshes) {
        const mats = Array.isArray(mesh.material) ? mesh.material : [mesh.material];
        for (const m of mats) {
          if (!m.emissive) continue;
          if (name === 'Link_Leavitt') {
            m.emissive.copy(pulseColor);
            m.emissiveIntensity = (hovered ? 6 : 2.5) + 2 * Math.sin(t * Math.PI);
          } else {
            if (hovered) m.emissive.copy(hoverColor); else if (m.userData.baseEmissive) m.emissive.copy(m.userData.baseEmissive);
            m.emissiveIntensity = hovered ? 0.35 : m.userData.baseIntensity;
          }
        }
      }
    }
  });

  // ---------- hover / click ----------
  // Link_ objects open their game. Belt stars (Star_*) show their game and fly back to its popup.
  const starGame = useMemo(() => Object.fromEntries(Object.entries(STARS).map(([g, n]) => [n, g])), []);
  const targetOf = (obj) => {
    for (let p = obj; p; p = p.parent) {
      if (LINKS[p.name]) return p.name;
      if (starGame[p.name]) return 'star:' + starGame[p.name];
    }
    return null;
  };
  const handlers = {
    onPointerMove: (e) => {
      const n = targetOf(e.object);
      e.stopPropagation();
      if (n !== flightState.hover) {
        flightState.hover = n;
        document.body.style.cursor = n ? 'pointer' : '';
      }
    },
    onPointerOut: () => { flightState.hover = null; document.body.style.cursor = ''; },
    onClick: (e) => {
      const n = targetOf(e.object);
      if (!n) return;
      e.stopPropagation();
      if (n.startsWith('star:')) flightState.flyToGame(n.slice(5));
      else window.location.href = LINKS[n].href;
    },
  };

  const g0 = rig.quats.length ? model.getObjectByName(GUIDES[0]) : null;
  const startPos = g0 ? g0.getWorldPosition(new THREE.Vector3()).toArray() : [0, 0, 0];
  const startRot = g0 ? new THREE.Euler().setFromQuaternion(g0.getWorldQuaternion(new THREE.Quaternion()).multiply(FLIP)).toArray().slice(0, 3) : [0, 0, 0];

  return (
    <>
      <Sky zoneAt={rig.zoneAt} />
      <ambientLight intensity={lookVals.ambient} />
      <hemisphereLight args={['#c7d2ff', '#141826', 0.5]} />
      <directionalLight position={rig.sunPos.toArray()} intensity={lookVals.sun} color="#fff6e8" />
      <primitive object={model} {...handlers} />
      <Launch model={model} guidePos={rig.pts} guideQuats={rig.quats} />
      {/* Free camera you can keyframe in the Theatre editor (choose "Free camera" in Camera Flight). */}
      <TheatreCamera
        theatreKey="Camera"
        makeDefault={flightVals.mode === 'keyframes'}
        position={startPos}
        rotation={startRot}
        fov={FOV}
        near={1}
        far={1e6}
      />
    </>
  );
}

useGLTF.preload(MODEL_URL, false, true);
