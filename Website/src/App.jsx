import { Suspense, useEffect, useMemo, useState } from 'react';
import { Canvas } from '@react-three/fiber';
import { ScrollControls, useProgress } from '@react-three/drei';
import { SheetProvider } from '@theatre/r3f';
import { EffectComposer, Bloom } from '@react-three/postprocessing';
import { sheet, look } from './theatre';
import { GUIDES, SCROLL_PER_GUIDE, FOV } from './config';
import { readProgress } from './progress';
import Scene from './Scene';
import Overlay from './Overlay';

function Effects() {
  const [v, setV] = useState(look.value);
  useEffect(() => look.onValuesChange(setV), []);
  return (
    <EffectComposer multisampling={new URLSearchParams(location.search).has("ms0") ? 0 : 4}>
      <Bloom mipmapBlur luminanceThreshold={1} luminanceSmoothing={0.2} intensity={v.bloom} />
    </EffectComposer>
  );
}

function Loading() {
  const { progress, active } = useProgress();
  const [gone, setGone] = useState(false);
  useEffect(() => { if (!active && progress >= 100) { const t = setTimeout(() => setGone(true), 600); return () => clearTimeout(t); } }, [active, progress]);
  if (gone) return null;
  return (
    <div className={`loading ${!active && progress >= 100 ? 'fade' : ''}`}>
      <div className="loading-title">Stellar</div>
      <div className="loading-bar"><div style={{ width: `${progress}%` }} /></div>
      <div className="loading-pct">Preparing for launch - {Math.round(progress)}%</div>
    </div>
  );
}

export default function App() {
  const done = useMemo(readProgress, []);
  // Scroll length follows the number of camera guides found in the model.
  const [guideCount, setGuideCount] = useState(Array.isArray(GUIDES) ? GUIDES.length : 13);
  const pages = (guideCount - 1) * SCROLL_PER_GUIDE + 1;
  return (
    <>
      <Canvas
        gl={{ antialias: false, logarithmicDepthBuffer: !new URLSearchParams(location.search).has('nolog'), powerPreference: 'high-performance' }}
        camera={{ fov: FOV, near: 1, far: 1e6 }}
        dpr={[1, 1.75]}
      >
        <color attach="background" args={['#02030a']} />
        <Suspense fallback={null}>
          <ScrollControls pages={pages} damping={0.22}>
            <SheetProvider sheet={sheet}>
              <Scene done={done} onGuideCount={setGuideCount} />
            </SheetProvider>
          </ScrollControls>
          {!new URLSearchParams(location.search).has('nofx') && <Effects />}
        </Suspense>
      </Canvas>
      <Overlay done={done} />
      <Loading />
    </>
  );
}
