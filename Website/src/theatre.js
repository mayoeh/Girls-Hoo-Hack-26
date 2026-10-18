// Theatre.js setup. Open the editor with  http://localhost:5173/?studio
// After editing, use the editor's "Export" button and save the file over src/theatre-state.json.
import { getProject, types } from '@theatre/core';
import state from './theatre-state.json';

export const project = getProject('Stellar Flight', state && state.sheetsById ? { state } : {});
export const sheet = project.sheet('Flythrough');

// The scroll bar drives this sheet's timeline (top of page = 0, bottom = end of the sequence).
export const flight = sheet.object('Camera Flight', {
  mode: types.stringLiteral('guides', { guides: 'Follow CamGuides', keyframes: 'Free camera (keyframe "Camera")' }),
  // Off: scroll moves evenly between guides. On: keyframe "progress" to slow down / speed up parts of the flight.
  useProgressKeyframes: types.boolean(false),
  progress: types.number(0, { range: [0, 1] }),
  // 0 = constant speed between guides, 1 = glide to a stop at every guide.
  easeAtGuides: types.number(0.65, { range: [0, 1] }),
});

export const look = sheet.object('Look', {
  bloom: types.number(1.1, { range: [0, 4] }),
  sun: types.number(2.6, { range: [0, 8] }),
  ambient: types.number(0.55, { range: [0, 3] }),
  exposure: types.number(1, { range: [0.2, 3] }),
});
