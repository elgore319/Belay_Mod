// Renders preview.json (from `dotnet run`) with three.js in headless Chromium: preview.png.
// Needs Playwright with Chromium, and three: `npm i three@0.160.0` (or set THREE_PATH to three.min.js).
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');

const threePath = process.env.THREE_PATH || path.join(path.dirname(require.resolve('three')), 'three.min.js');
const data = fs.readFileSync(process.argv[2] || 'preview.json', 'utf8');
const out = process.argv[3] || 'preview.png';

const views = [
  ['standing', 200, 'Front'],
  ['standing', 270, 'Side'],
  ['standing', 20, 'Back'],
  ['hanging', 210, 'Hanging by one hand'],
  ['hanging', 330, 'Hanging, from behind'],
];

const page = `<html><body style="margin:0;background:#16171a"><canvas id="c"></canvas></body></html>`;

(async () => {
  const browser = await chromium.launch({ args: ['--use-gl=swiftshader', '--enable-unsafe-swiftshader'] });
  const tab = await browser.newPage({ viewport: { width: 360 * views.length, height: 620 }, deviceScaleFactor: 2 });
  await tab.setContent(page);
  await tab.addScriptTag({ path: threePath });
  await tab.evaluate(({ data, views }) => {
    const model = JSON.parse(data);
    const W = 360, H = 620;
    const renderer = new THREE.WebGLRenderer({ canvas: document.getElementById('c'), antialias: true });
    renderer.setPixelRatio(2);
    renderer.setSize(W * views.length, H);
    renderer.setScissorTest(true);
    const materials = [];
    for (let i = 0; i < model.colors.length / 3; i++) {
      const color = new THREE.Color().setRGB(model.colors[i * 3], model.colors[i * 3 + 1], model.colors[i * 3 + 2], THREE.SRGBColorSpace);
      materials.push(new THREE.MeshStandardMaterial({
        color, flatShading: true, roughness: i === 8 ? 0.2 : 0.8, metalness: i === 4 ? 0.6 : 0,
        emissive: i === 9 ? color : new THREE.Color(0), side: THREE.DoubleSide,
      }));
    }
    const scenes = {};
    for (const name of Object.keys(model.figures)) {
      const scene = new THREE.Scene();
      scene.background = new THREE.Color(0x16171a);
      scene.add(new THREE.HemisphereLight(0xbfc8d6, 0x2a2420, 1.1));
      const key = new THREE.DirectionalLight(0xffffff, 1.6); key.position.set(-2, 3, 3); scene.add(key);
      const rim = new THREE.DirectionalLight(0x99aaff, 0.6); rim.position.set(2, 2, -3); scene.add(rim);
      const bySlot = {};
      for (const t of model.figures[name]) {
        (bySlot[t[0]] = bySlot[t[0]] || []).push(
          t[1], t[3], -t[2], t[4], t[6], -t[5], t[7], t[9], -t[8]);
      }
      for (const slot of Object.keys(bySlot)) {
        const g = new THREE.BufferGeometry();
        g.setAttribute('position', new THREE.Float32BufferAttribute(bySlot[slot], 3));
        g.computeVertexNormals();
        scene.add(new THREE.Mesh(g, materials[slot]));
      }
      const floor = new THREE.Mesh(new THREE.CircleGeometry(0.6, 32), new THREE.MeshStandardMaterial({ color: 0x24262b }));
      floor.rotation.x = -Math.PI / 2;
      if (name === 'standing') scene.add(floor);
      scenes[name] = scene;
    }
    views.forEach(([name, angle, label], i) => {
      const camera = new THREE.PerspectiveCamera(30, W / H, 0.1, 50);
      const a = angle * Math.PI / 180;
      camera.position.set(Math.sin(a) * 4.3, 1.35, Math.cos(a) * 4.3);
      camera.lookAt(0, 1.02, 0);
      renderer.setViewport(i * W, 0, W, H);
      renderer.setScissor(i * W, 0, W, H);
      renderer.render(scenes[name], camera);
    });
    const labels = document.createElement('div');
    labels.style.cssText = 'position:absolute;top:14px;left:0;display:flex;font:15px sans-serif;color:#ccd';
    labels.innerHTML = views.map(v => `<div style="width:${W}px;text-align:center">${v[2]}</div>`).join('');
    document.body.appendChild(labels);
  }, { data, views });
  await tab.screenshot({ path: out });
  await browser.close();
  console.log('wrote ' + out);
})();
