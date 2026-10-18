import React from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import './styles.css';

// Theatre.js editor: only in dev, and only with ?studio in the URL.
if (import.meta.env.DEV && new URLSearchParams(location.search).has('studio')) {
  Promise.all([import('@theatre/studio'), import('@theatre/r3f/dist/extension')]).then(([studio, ext]) => {
    studio.default.extend(ext.default);
    studio.default.initialize();
  });
}

createRoot(document.getElementById('root')).render(<App />);
