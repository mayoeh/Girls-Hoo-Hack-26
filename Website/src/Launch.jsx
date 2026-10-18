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

    // Arrival: line the stack up so its orbiter sits exactly on the orbiter by the Earth (Link_Ride).
    // Both shuttles are built the same way (Shuttle_Rig_1 in the stack <-> Shuttle_2 at the Earth).
    let final = null, arriveObj = null, tank = null;
    const A = cfg.arrive;
    if (A) {
      arriveObj = model.getObjectByName(A.object);
      const rigNode = stack.getObjectByName(A.match[0]);
      const target = model.getObjectByName(A.match[1]);
      if (arriveObj && rigNode && target && rigNode.parent === stack) {
        arriveObj.visible = true; arriveObj.updateMatrixWorld(true);
        const m = target.matrixWorld.clone().multiply(rigNode.matrix.clone().invert());
        final = { pos: new THREE.Vector3(), quat: new THREE.Quaternion(), scale: new THREE.Vector3() };
        m.decompose(final.pos, final.quat, final.scale);
      } else console.warn('[Stellar] launch arrival: could not match', A);
      tank = A.tank ? stack.getObjectByName(A.tank) : null;
      if (tank && !tank.userData.restPos) { tank.userData.restPos = tank.position.clone(); tank.userData.restQuat = tank.quaternion.clone(); }
    }
    const pts = [p0, p1, cross, p3];
    if (final) {
      const back = p3.clone().sub(final.pos).normalize();
      pts.push(final.pos.clone().addScaledVector(back, H * 5), final.pos.clone());
    }
    const crossT = 2 / (pts.length - 1);                  // where the camera pass sits on the curve
    const curve = new THREE.CatmullRomCurve3(pts, false, 'centripetal');
    const startScale = stack.getWorldScale(new THREE.Vector3());
    // Direction the tank drifts off in (away from the orbiter's belly), in the tank's parent space.
    let tankDir = null;
    if (tank) {
      const orb = tank.parent.children.find((c) => c !== tank && c.isMesh);
      tankDir = orb ? tank.userData.restPos.clone().sub(orb.position).normalize() : new THREE.Vector3(0, 1, 0);
    }

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

    return { stack, parent, start, q0, H, curve, crossT, final, arriveObj, tank, tankDir, startScale, flame, flameMat, coreMat, smoke, jitter, veil, veilMat, up };
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

  const tmp = useMemo(() => ({ p: new THREE.Vector3(), d: new THREE.Vector3(), q: new THREE.Quaternion(), pq: new THREE.Quaternion(), v: new THREE.Vector3(), sc: new THREE.Vector3(), ps: new THREE.Vector3(), tq: new THREE.Quaternion(), d2: new THREE.Vector3() }), []);

  // u (0-based guide position) -> how far along the launch curve (0..1).
  const lastLeg = (u) => clamp01((u - (cfg.passAt - 1)) / (cfg.endAt - cfg.passAt));   // 0 at the pass, 1 on arrival
  const curveT = (u) => {
    const a = cfg.startAt - 1, b = cfg.passAt - 1, X = rig ? rig.crossT : 2 / 3;
    if (u <= a) return 0;
    if (u <= b) { const x = (u - a) / (b - a); return X * (x < 0.5 ? 2 * x * x : 1 - Math.pow(-2 * x + 2, 2) / 2) * 0.35 + X * Math.pow(x, 1.6) * 0.65; }   // soft start, steady climb
    const x = lastLeg(u);
    return X + (1 - X) * (1 - Math.pow(1 - x, 2.2));            // coasts in and slows down on arrival
  };

  const update = ({ camera, clock }) => {
    if (!rig) return;
    const u = flightState.u;
    const t = curveT(u);
    const { stack, parent, start } = rig;

    // ----- the stack -----
    if (t <= 0) {
      stack.position.copy(start.pos); stack.quaternion.copy(start.quat); stack.visible = true;
      rig.sPos = null;
    } else {
      const { p, d, q, pq } = tmp;
      rig.curve.getPoint(Math.min(t, 1), p);
      rig.curve.getTangent(Math.min(Math.max(t, 0.001), 0.999), d);
      if (rig.final && u > cfg.passAt - 1) {
        // After the pass it flies just ahead of the camera (so you see it the whole way),
        // then breaks off and settles into its spot by the Earth.
        const x = lastLeg(u);
        const fwd = camera.getWorldDirection(tmp.v);
        const lead = tmp.ps.copy(camera.position)
          .addScaledVector(fwd, rig.H * THREE.MathUtils.lerp(cfg.passDistance, cfg.escortDistance ?? 2.2, smooth(x / 0.25)))
          .addScaledVector(camera.up.clone().applyQuaternion(camera.quaternion), -rig.H * 0.25 * smooth(x / 0.25));
        const w = smooth((x - 0.78) / 0.22);
        p.lerpVectors(lead, rig.final.pos, w);
        tmp.d2.copy(rig.final.pos).sub(lead).normalize();
        d.copy(fwd).lerp(tmp.d2, smooth((x - 0.7) / 0.2)).normalize();
      }
      q.setFromUnitVectors(rig.up, d).multiply(rig.q0);          // nose along the flight path
      tmp.sc.copy(rig.startScale);
      if (rig.final) {                                           // turn and settle into the orbit pose
        const w = smooth((lastLeg(u) - 0.75) / 0.25);
        q.slerp(rig.final.quat, w);
        tmp.sc.lerp(rig.final.scale, w);
      }
      // Glide toward the target pose instead of jumping to it (no jerks when the camera stops at a guide).
      const now = performance.now(), dt = Math.min(0.1, (now - (rig.lastT || now)) / 1000); rig.lastT = now;
      if (!rig.sPos || rig.sPos.distanceTo(p) > rig.H * 60 || dt === 0) { rig.sPos = p.clone(); rig.sQuat = q.clone(); }
      else {
        const k = 1 - Math.exp(-dt * (cfg.smoothing ?? 4));
        rig.sPos.lerp(p, k); rig.sQuat.slerp(q, k);
      }
      p.copy(rig.sPos); q.copy(rig.sQuat);
      if (parent) {
        parent.getWorldScale(tmp.ps);
        parent.worldToLocal(p); parent.getWorldQuaternion(pq).invert(); q.premultiply(pq);
        tmp.sc.divide(tmp.ps);
      }
      stack.position.copy(p); stack.quaternion.copy(q); stack.scale.copy(tmp.sc);
      stack.visible = u < cfg.endAt - 1;
    }
    if (t <= 0) stack.scale.copy(rig.startScale).divide(parent ? parent.getWorldScale(tmp.ps) : tmp.ps.set(1, 1, 1));
    // The orbiter by the Earth only appears once the launched shuttle has arrived (they swap in place).
    if (rig.arriveObj) rig.arriveObj.visible = u >= cfg.endAt - 1;

    // External tank separates and drifts away on the way to orbit.
    if (rig.tank) {
      const sep = smooth((lastLeg(u) - 0.25) / 0.3);
      rig.tank.position.copy(rig.tank.userData.restPos).addScaledVector(rig.tankDir, sep * 80);
      rig.tank.quaternion.copy(rig.tank.userData.restQuat);
      if (sep > 0) rig.tank.quaternion.multiply(tmp.tq.setFromAxisAngle(rig.up, sep * 0.6));
      rig.tank.visible = sep < 0.97;
    }

    // ----- engines -----
    const lead = cfg.igniteLead ?? 0.25, ignite = smooth((u - (cfg.startAt - 1 - lead)) / lead);
    rig.flame.visible = ignite > 0.01 && stack.visible;
    if (rig.flame.visible) {
      const flick = 0.85 + 0.15 * Math.sin(clock.elapsedTime * 40) * Math.sin(clock.elapsedTime * 23);
      rig.flame.scale.set(1, (0.4 + 0.6 * ignite) * flick * (1 + 0.8 * clamp01(t * 3)), 1);
      const cutoff = 1 - smooth((lastLeg(u) - 0.2) / 0.2);   // engines off as the tank separates
      rig.flameMat.opacity = 0.9 * ignite * cutoff; rig.coreMat.opacity = 0.95 * ignite * cutoff;
      rig.flame.visible = cutoff > 0.01;
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
  };
  // Runs right after the camera moves each frame (see Scene.jsx), so the shuttle never lags behind it.
  useEffect(() => { flightState.afterCamera = update; return () => { if (flightState.afterCamera === update) flightState.afterCamera = null; }; });

  if (!rig) return null;
  return (
    <>
      <primitive object={rig.smoke} />
    </>
  );
}
