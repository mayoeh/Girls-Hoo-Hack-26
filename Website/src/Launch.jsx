import { useEffect, useMemo, useRef } from 'react';
import * as THREE from 'three';
import { useFrame, useThree } from '@react-three/fiber';
import { LAUNCH } from './config';
import { flightState } from './flightState';

// Scroll-driven launch: the pad shuttle ignites, climbs past the camera (its smoke covers the
// sky change), then flies off. Everything is a function of the scroll position, so it reverses
// when you scroll back up.

const clamp01 = (x) => Math.min(Math.max(x, 0), 1);
const smooth = (x) => { x = clamp01(x); return x * x * (3 - 2 * x); };
const bump = (u, at, width) => 1 - smooth(Math.abs(u - at) / width);

function softTexture(kind) {
  const c = document.createElement('canvas');
  c.width = c.height = 256;
  const g = c.getContext('2d');
  if (kind === 'puff') {
    const gr = g.createRadialGradient(128, 128, 0, 128, 128, 128);
    gr.addColorStop(0, 'rgba(255,255,255,1)');
    gr.addColorStop(0.45, 'rgba(255,255,255,0.55)');
    gr.addColorStop(1, 'rgba(255,255,255,0)');
    g.fillStyle = gr; g.fillRect(0, 0, 256, 256);
  } else { // 'veil': a cloudy screen-filling haze
    g.fillStyle = 'rgba(235,238,245,0.85)'; g.fillRect(0, 0, 256, 256);
    for (let i = 0; i < 70; i++) {
      const x = Math.random() * 256, y = Math.random() * 256, r = 30 + Math.random() * 70;
      const gr = g.createRadialGradient(x, y, 0, x, y, r);
      const a = 0.15 + Math.random() * 0.25;
      gr.addColorStop(0, `rgba(255,255,255,${a})`); gr.addColorStop(1, 'rgba(255,255,255,0)');
      g.fillStyle = gr; g.fillRect(0, 0, 256, 256);
    }
  }
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  return t;
}

// Points with their own size + alpha per puff (works with the logarithmic depth buffer).
function smokeMaterial(map) {
  return new THREE.ShaderMaterial({
    uniforms: { map: { value: map }, scale: { value: 600 } },
    vertexShader: `
      #include <common>
      #include <logdepthbuf_pars_vertex>
      attribute float aSize; attribute float aAlpha; varying float vAlpha; uniform float scale;
      void main() {
        vec4 mv = modelViewMatrix * vec4(position, 1.0);
        gl_Position = projectionMatrix * mv;
        gl_PointSize = min(aSize * scale / max(-mv.z, 1.0), 700.0);
        vAlpha = aAlpha;
        #include <logdepthbuf_vertex>
      }`,
    fragmentShader: `
      #include <logdepthbuf_pars_fragment>
      uniform sampler2D map; varying float vAlpha;
      void main() {
        #include <logdepthbuf_fragment>
        vec4 c = texture2D(map, gl_PointCoord);
        gl_FragColor = vec4(vec3(0.93, 0.94, 0.97) * c.rgb, c.a * vAlpha);
      }`,
    transparent: true, depthWrite: false,
  });
}

export default function Launch({ model, guidePos, guideQuats }) {
  const cfg = LAUNCH;
  const N = 260;

  const rig = useMemo(() => {
    if (!cfg || !cfg.object) return null;
    const stack = model.getObjectByName(cfg.object);
    if (!stack || guidePos.length < 2) return null;
    // Remove a flame left from an earlier mount, and remember the stack's resting pose only once.
    const old = stack.getObjectByName('launchFlame'); if (old) stack.remove(old);
    if (!stack.userData.launchStart) stack.userData.launchStart = { pos: stack.position.clone(), quat: stack.quaternion.clone() };
    stack.position.copy(stack.userData.launchStart.pos); stack.quaternion.copy(stack.userData.launchStart.quat);
    stack.updateMatrixWorld(true);
    const parent = stack.parent;
    const start = { pos: stack.position.clone(), quat: stack.quaternion.clone() };
    const q0 = stack.getWorldQuaternion(new THREE.Quaternion());
    const origin = stack.getWorldPosition(new THREE.Vector3());
    const box = new THREE.Box3().setFromObject(stack);
    const H = box.getSize(new THREE.Vector3()).y;           // stack height
    const up = new THREE.Vector3(0, 1, 0);

    // Where it crosses in front of the camera (at the guide where the sky changes).
    const k = THREE.MathUtils.clamp(Math.round(cfg.passAt - 1), 0, guidePos.length - 1);
    const camPos = guidePos[k];
    const camFwd = new THREE.Vector3(0, 0, -1).applyQuaternion(guideQuats[k]);
    const cross = camPos.clone().addScaledVector(camFwd, H * cfg.passDistance).addScaledVector(up, -H * 0.5);

    const p0 = origin.clone();
    const p1 = origin.clone().addScaledVector(up, Math.max(H * 3, (cross.y - origin.y) * 0.45));
    const away = cross.clone().sub(p1).normalize();
    const p3 = cross.clone().addScaledVector(away, H * 25).addScaledVector(up, H * 30);
    const curve = new THREE.CatmullRomCurve3([p0, p1, cross, p3], false, 'centripetal');

    // Flame + glow at the base of the stack, in the stack's own space.
    const baseWorld = new THREE.Vector3((box.min.x + box.max.x) / 2, box.min.y, (box.min.z + box.max.z) / 2);
    const baseLocal = stack.worldToLocal(baseWorld.clone());
    const downLocal = stack.worldToLocal(origin.clone().addScaledVector(up, -1)).sub(stack.worldToLocal(origin.clone())).normalize();
    const scaleW = stack.getWorldScale(new THREE.Vector3()).y || 1;

    const flame = new THREE.Group();
    flame.name = 'launchFlame';
    flame.position.copy(baseLocal);
    flame.quaternion.setFromUnitVectors(new THREE.Vector3(0, -1, 0), downLocal);
    const flameMat = new THREE.MeshBasicMaterial({ color: new THREE.Color(4, 1.9, 0.6), transparent: true, opacity: 0.9, toneMapped: false, depthWrite: false, blending: THREE.AdditiveBlending });
    const coreMat = new THREE.MeshBasicMaterial({ color: new THREE.Color(6, 5, 3), transparent: true, opacity: 0.95, toneMapped: false, depthWrite: false, blending: THREE.AdditiveBlending });
    const len = (H * 0.55) / scaleW, rad = (H * 0.07) / scaleW;
    const cone = new THREE.Mesh(new THREE.ConeGeometry(rad, len, 20, 1, true), flameMat);
    cone.rotation.x = Math.PI; cone.position.y = -len / 2;
    const core = new THREE.Mesh(new THREE.ConeGeometry(rad * 0.45, len * 0.5, 16, 1, true), coreMat);
    core.rotation.x = Math.PI; core.position.y = -len * 0.25;
    flame.add(cone, core);
    flame.visible = false;
    stack.add(flame);

    // Smoke trail.
    const puff = softTexture('puff');
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(N * 3), 3));
    geo.setAttribute('aSize', new THREE.BufferAttribute(new Float32Array(N), 1));
    geo.setAttribute('aAlpha', new THREE.BufferAttribute(new Float32Array(N), 1));
    const smoke = new THREE.Points(geo, smokeMaterial(puff));
    smoke.frustumCulled = false;
    smoke.renderOrder = 5;
    const jitter = Array.from({ length: N }, () => new THREE.Vector3(Math.random() - 0.5, Math.random() - 0.5, Math.random() - 0.5));

    // Screen veil that washes over the camera as it flies through the exhaust.
    const veilMat = new THREE.MeshBasicMaterial({ map: softTexture('veil'), transparent: true, opacity: 0, depthTest: false, depthWrite: false, toneMapped: false, fog: false });
    const veil = new THREE.Mesh(new THREE.PlaneGeometry(1, 1), veilMat);
    veil.renderOrder = 999; veil.frustumCulled = false;

    return { stack, parent, start, q0, H, curve, flame, flameMat, coreMat, smoke, jitter, veil, veilMat, up };
  }, [model, guidePos, guideQuats]);

  // The veil rides on the camera itself (so it never lags behind a fast-moving camera).
  const { camera, scene } = useThree();
  useEffect(() => {
    if (!rig) return;
    if (!camera.parent) scene.add(camera);
    camera.add(rig.veil);
    rig.veil.position.set(0, 0, -2);
    return () => { camera.remove(rig.veil); };
  }, [rig, camera, scene]);

  const tmp = useMemo(() => ({ p: new THREE.Vector3(), d: new THREE.Vector3(), q: new THREE.Quaternion(), pq: new THREE.Quaternion(), v: new THREE.Vector3() }), []);

  // u (0-based guide position) -> how far along the launch curve (0..1). The crossing point is at 2/3.
  const curveT = (u) => {
    const a = cfg.startAt - 1, b = cfg.passAt - 1, c = cfg.endAt - 1;
    if (u <= a) return 0;
    if (u <= b) return (2 / 3) * Math.pow((u - a) / (b - a), 1.7);   // accelerating climb
    return 2 / 3 + (1 / 3) * clamp01((u - b) / (c - b));
  };

  useFrame(({ camera, clock }) => {
    if (!rig) return;
    const u = flightState.u;
    const t = curveT(u);
    const { stack, parent, start } = rig;

    // ----- the stack -----
    if (t <= 0) {
      stack.position.copy(start.pos); stack.quaternion.copy(start.quat); stack.visible = true;
    } else {
      const { p, d, q, pq } = tmp;
      rig.curve.getPoint(Math.min(t, 1), p);
      rig.curve.getTangent(Math.min(Math.max(t, 0.001), 0.999), d);
      q.setFromUnitVectors(rig.up, d).multiply(rig.q0);          // nose along the flight path
      if (parent) { parent.worldToLocal(p); parent.getWorldQuaternion(pq).invert(); q.premultiply(pq); }
      stack.position.copy(p); stack.quaternion.copy(q);
      stack.visible = u < cfg.endAt - 1;
    }

    // ----- engines -----
    const ignite = smooth((u - (cfg.startAt - 1 - 0.25)) / 0.25);
    rig.flame.visible = ignite > 0.01 && stack.visible;
    if (rig.flame.visible) {
      const flick = 0.85 + 0.15 * Math.sin(clock.elapsedTime * 40) * Math.sin(clock.elapsedTime * 23);
      rig.flame.scale.set(1, (0.4 + 0.6 * ignite) * flick * (1 + 0.8 * clamp01(t * 3)), 1);
      rig.flameMat.opacity = 0.9 * ignite; rig.coreMat.opacity = 0.95 * ignite;
    }

    // ----- smoke: puffs along the path behind the shuttle, plus a cloud at the pad -----
    const pos = rig.smoke.geometry.attributes.position, size = rig.smoke.geometry.attributes.aSize, alpha = rig.smoke.geometry.attributes.aAlpha;
    const H = rig.H, fadeOut = 1 - smooth((u - (cfg.passAt - 1) - 0.15) / 0.7);   // trail clears after the pass
    for (let i = 0; i < N; i++) {
      const f = i / (N - 1);
      let a = 0, s = 0;
      if (i < 40) {                                   // ground cloud around the pad
        const ang = i * 2.4, r = H * (0.15 + 0.25 * f * 6);
        rig.curve.getPoint(0, tmp.v);
        tmp.v.x += Math.cos(ang) * r; tmp.v.z += Math.sin(ang) * r; tmp.v.y += H * 0.05 + rig.jitter[i].y * H * 0.3;
        a = 0.45 * ignite * fadeOut; s = H * (0.18 + 0.1 * rig.jitter[i].x) * (0.4 + ignite);
      } else {                                        // trail
        const g = (i - 40) / (N - 41);                // 0 = pad, 1 = right behind the shuttle
        const tt = Math.max(0, t - 0.004) * g;
        rig.curve.getPoint(tt, tmp.v);
        tmp.v.addScaledVector(rig.jitter[i], H * (0.05 + (1 - g) * 0.25));
        const age = 1 - g;
        a = t > 0 ? (0.25 + 0.25 * age) * fadeOut : 0;
        s = H * (0.12 + age * 0.45);
      }
      pos.setXYZ(i, tmp.v.x, tmp.v.y, tmp.v.z); size.setX(i, s); alpha.setX(i, a);
    }
    pos.needsUpdate = size.needsUpdate = alpha.needsUpdate = true;
    rig.smoke.material.uniforms.scale.value = (window.innerHeight * 0.5) / Math.tan(THREE.MathUtils.degToRad(camera.fov / 2));

    // ----- veil over the camera while flying through the exhaust (hides the sky change) -----
    const vo = bump(u, cfg.passAt - 1, cfg.veilWidth) * cfg.veilStrength;
    rig.veil.visible = vo > 0.002;
    if (rig.veil.visible) {
      const h = 2 * 2 * Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)) * 1.6;
      rig.veil.scale.set(h * camera.aspect, h, 1);
      rig.veilMat.opacity = vo;
    }
  });

  if (!rig) return null;
  return (
    <>
      <primitive object={rig.smoke} />
    </>
  );
}
