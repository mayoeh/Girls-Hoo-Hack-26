import { useMemo } from 'react';
import { useFrame } from '@react-three/fiber';
import { flightState } from './flightState';
import * as THREE from 'three';
import { GROUND } from './config';

// A wide ground disk under the launch pad so it doesn't float. Soft noise texture, and the edge
// fades out so it blends into the sky instead of ending in a hard line.
function groundTextures() {
  const c = document.createElement('canvas'); c.width = c.height = 512;
  const g = c.getContext('2d');
  g.fillStyle = '#6b6f58'; g.fillRect(0, 0, 512, 512);
  for (let i = 0; i < 9000; i++) {
    const v = 80 + Math.random() * 60, a = 0.08 + Math.random() * 0.12;
    g.fillStyle = `rgba(${v},${v + 6},${v - 12},${a})`;
    const r = 1 + Math.random() * 5; g.fillRect(Math.random() * 512, Math.random() * 512, r, r);
  }
  const map = new THREE.CanvasTexture(c);
  map.wrapS = map.wrapT = THREE.RepeatWrapping; map.colorSpace = THREE.SRGBColorSpace; map.anisotropy = 8;

  const a = document.createElement('canvas'); a.width = a.height = 256;
  const ga = a.getContext('2d');
  const gr = ga.createRadialGradient(128, 128, 0, 128, 128, 128);
  gr.addColorStop(0, '#fff'); gr.addColorStop(0.55, '#fff'); gr.addColorStop(1, '#000');
  ga.fillStyle = gr; ga.fillRect(0, 0, 256, 256);
  const alphaMap = new THREE.CanvasTexture(a);
  return { map, alphaMap };
}

export default function Ground({ model }) {
  const ground = useMemo(() => {
    if (!GROUND) return null;
    const under = model.getObjectByName(GROUND.under) || model.getObjectByName(GROUND.under.replace(/\s+/g, '_'));
    if (!under) { console.warn('[Stellar] ground: no', GROUND.under); return null; }
    const box = new THREE.Box3().setFromObject(under);
    const center = box.getCenter(new THREE.Vector3());
    const { map, alphaMap } = groundTextures();
    map.repeat.set(GROUND.radius / 400, GROUND.radius / 400);
    const geo = new THREE.CircleGeometry(GROUND.radius, 96);
    geo.setAttribute('uv2', geo.attributes.uv);
    const mat = new THREE.MeshStandardMaterial({ map, alphaMap, transparent: true, depthWrite: true, roughness: 1, metalness: 0, color: GROUND.color });
    const mesh = new THREE.Mesh(geo, mat);
    mesh.rotation.x = -Math.PI / 2;
    mesh.position.set(center.x, box.min.y - (GROUND.sink ?? 1), center.z);
    mesh.renderOrder = -50;
    return mesh;
  }, [model]);
  // Only near the launch pad: hidden once you're past the sky change (GROUND.until, in guides).
  useFrame(() => { if (ground) ground.visible = flightState.u < (GROUND.until ?? 4.5) - 1; });
  return ground ? <primitive object={ground} /> : null;
}
