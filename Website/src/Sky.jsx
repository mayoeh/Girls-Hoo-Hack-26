import { useMemo, useRef } from 'react';
import * as THREE from 'three';
import { useFrame, useLoader } from '@react-three/fiber';
import { SKIES } from './config';
import { flightState } from './flightState';

// Unity "6 Sided" skyboxes rebuilt as an inside-out box that follows the camera.
// Unity's +Z becomes -Z after the glTF export, so faces are placed to match what Unity showed.
//   right = direction of the image's right edge, up = direction of its top edge (as seen from inside)
const FACES = [
  { face: 'front', center: [0, 0, -1], right: [1, 0, 0], up: [0, 1, 0] },
  { face: 'back', center: [0, 0, 1], right: [-1, 0, 0], up: [0, 1, 0] },
  { face: 'left', center: [1, 0, 0], right: [0, 0, 1], up: [0, 1, 0] },
  { face: 'right', center: [-1, 0, 0], right: [0, 0, -1], up: [0, 1, 0] },
  { face: 'up', center: [0, 1, 0], right: [1, 0, 0], up: [0, 0, 1] },
  { face: 'down', center: [0, -1, 0], right: [1, 0, 0], up: [0, 0, -1] },
];
const SIZE = 200000; // half-width of the sky box (camera far plane is 1e6)

function SkyBox({ folder, order, materialsOut }) {
  const maps = useLoader(THREE.TextureLoader, FACES.map((f) => `${folder}/${f.face}.jpg`));
  const planes = useMemo(() => FACES.map((f, k) => {
    const map = maps[k];
    map.colorSpace = THREE.SRGBColorSpace;
    map.wrapS = map.wrapT = THREE.ClampToEdgeWrapping;
    map.anisotropy = 4;
    const mat = new THREE.MeshBasicMaterial({ map, transparent: true, depthWrite: false, depthTest: true, fog: false, toneMapped: false });
    const x = new THREE.Vector3(...f.right), y = new THREE.Vector3(...f.up), z = new THREE.Vector3(...f.center).negate();
    const q = new THREE.Quaternion().setFromRotationMatrix(new THREE.Matrix4().makeBasis(x, y, z));
    const pos = new THREE.Vector3(...f.center).multiplyScalar(SIZE);
    return { mat, q, pos };
  }), [maps]);
  materialsOut[order] = planes.map((p) => p.mat);
  return planes.map((p, k) => (
    <mesh key={k} position={p.pos} quaternion={p.q} renderOrder={-100 + order} frustumCulled={false} material={p.mat}>
      <planeGeometry args={[SIZE * 2, SIZE * 2]} />
    </mesh>
  ));
}

// One flat photo wrapped around the inside of a sphere, mirrored so the edges meet without a seam.
function PhotoSky({ url, repeat = [3, 1], order, materialsOut }) {
  const map = useLoader(THREE.TextureLoader, url);
  const mat = useMemo(() => {
    map.colorSpace = THREE.SRGBColorSpace;
    map.wrapS = map.wrapT = THREE.MirroredRepeatWrapping;
    map.repeat.set(repeat[0] * 2, repeat[1]);   // x2 so the mirrored pair closes the loop
    map.anisotropy = 4;
    return new THREE.MeshBasicMaterial({ map, side: THREE.BackSide, transparent: true, depthWrite: false, depthTest: true, fog: false, toneMapped: false });
  }, [map, repeat[0], repeat[1]]);
  materialsOut[order] = [mat];
  return (
    <mesh renderOrder={-100 + order} frustumCulled={false} material={mat}>
      <sphereGeometry args={[SIZE, 64, 32]} />
    </mesh>
  );
}

// A real 360-degree panorama (equirectangular, 2:1 image) on the inside of a sphere.
function PanoSky({ url, order, materialsOut, turn = 0 }) {
  const map = useLoader(THREE.TextureLoader, url);
  const mat = useMemo(() => {
    map.colorSpace = THREE.SRGBColorSpace;
    map.wrapS = THREE.RepeatWrapping;
    map.repeat.x = -1;              // seen from inside, so flip it back the right way round
    map.offset.x = turn;            // 0..1 spins the panorama around you
    map.anisotropy = 8;
    return new THREE.MeshBasicMaterial({ map, side: THREE.BackSide, transparent: true, depthWrite: false, depthTest: true, fog: false, toneMapped: false });
  }, [map, turn]);
  materialsOut[order] = [mat];
  return (
    <mesh renderOrder={-100 + order} frustumCulled={false} material={mat}>
      <sphereGeometry args={[SIZE, 96, 48]} />
    </mesh>
  );
}

export default function Sky({ zoneAt }) {
  const group = useRef();
  const matsRef = useRef([]);
  useFrame(({ camera }) => {
    if (!group.current) return;
    group.current.position.copy(camera.position);
    // Sky k fades in around the guide where SkyZone_k sits.
    matsRef.current.forEach((set, k) => {
      if (!set) return;
      let o = 1;
      if (k > 0) {
        const at = zoneAt[k - 1] ?? Infinity;
        const x = THREE.MathUtils.clamp((flightState.u - (at - 0.5)) / 1.0, 0, 1);
        o = x * x * (3 - 2 * x);
      }
      for (const m of set) { m.opacity = o; m.visible = o > 0.001; }
    });
  });
  return (
    <group ref={group}>
      {SKIES.map((s, k) => s.type === 'pano'
        ? <PanoSky key={s.name} url={s.url} turn={s.turn} order={k} materialsOut={matsRef.current} />
        : s.type === 'photo'
        ? <PhotoSky key={s.name} url={s.url} repeat={s.repeat} order={k} materialsOut={matsRef.current} />
        : <SkyBox key={s.name} folder={s.folder} order={k} materialsOut={matsRef.current} />)}
    </group>
  );
}
