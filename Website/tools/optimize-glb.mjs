import { NodeIO } from '@gltf-transform/core';
import { ALL_EXTENSIONS } from '@gltf-transform/extensions';
import { dedup, weld, simplify, prune, textureCompress, meshopt } from '@gltf-transform/functions';
import { MeshoptSimplifier, MeshoptEncoder, MeshoptDecoder } from 'meshoptimizer';
import sharp from 'sharp';
const [,, inp, out, ratio = '0.06', err = '0.002'] = process.argv;
await Promise.all([MeshoptSimplifier.ready, MeshoptEncoder.ready, MeshoptDecoder.ready]);
const io = new NodeIO().registerExtensions(ALL_EXTENSIONS)
  .registerDependencies({ 'meshopt.decoder': MeshoptDecoder, 'meshopt.encoder': MeshoptEncoder });
const doc = await io.read(inp);
const tris = () => { let t = 0; for (const m of doc.getRoot().listMeshes()) for (const p of m.listPrimitives()) t += (p.getIndices()?.getCount() ?? p.getAttribute('POSITION').getCount()) / 3; return t; };
console.log('before tris', tris());
// glassy transmission on the connector material renders oddly/expensively in three.js: drop it
for (const ext of doc.getRoot().listExtensionsUsed()) if (/transmission|ior/.test(ext.extensionName)) ext.dispose();
await doc.transform(
  dedup({ keepUniqueNames: true }),
  weld(),
  simplify({ simplifier: MeshoptSimplifier, ratio: +ratio, error: +err }),
  prune({ keepLeaves: true, keepAttributes: false }),
  textureCompress({ encoder: sharp, targetFormat: 'webp', resize: [2048, 2048], quality: 85 }),
  meshopt({ encoder: MeshoptEncoder, level: 'medium' }),
);
console.log('after tris', tris());
for (const n of doc.getRoot().listNodes()) { const m = n.getMesh(); if (!m) continue; let t = 0; for (const p of m.listPrimitives()) t += (p.getIndices()?.getCount() ?? 0) / 3; if (t > 3000) console.log('  ', n.getName(), t); }
console.log('materials', doc.getRoot().listMaterials().map(m => m.getName()).join(', '));
console.log('nodes', doc.getRoot().listNodes().length);
await io.write(out, doc);
