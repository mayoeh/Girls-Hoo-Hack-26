import * as THREE from 'three'; import fs from 'fs';
const L=fs.readFileSync('/tmp/pts.txt','utf8').trim().split('\n').map(l=>l.split(' ')).map(([n,p])=>[n,p.split(',').map(Number)]);
const key=n=>/End$/.test(n)?Infinity:parseFloat(n.replace(/^CamGuide_/,''))||0;
const g=L.filter(([n])=>/^CamGuide(_\d+|_?End)$/i.test(n)).sort((a,b)=>key(a[0])-key(b[0]));
const pts=g.map(([,p])=>new THREE.Vector3(...p));const curve=new THREE.CatmullRomCurve3(pts,false,'centripetal');
const last=pts.length-1,ease=+process.argv[2];const sm=f=>f*f*(3-2*f);const t=new THREE.Vector3();
const pathU=p=>{let best=0,bd=Infinity;for(let u=0;u<=last+1e-6;u+=0.01){const i=Math.min(Math.floor(u),last-1),f=u-i;curve.getPoint((i+THREE.MathUtils.lerp(f,sm(f),ease))/last,t);const d=t.distanceToSquared(p);if(d<bd){bd=d;best=u;}}return best;};
for(const [n,p] of L) if(/^(Popup_Jackson|SkyZone)/.test(n)) console.log(n,pathU(new THREE.Vector3(...p)).toFixed(2));
