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
  ['standing', 290, 'Side'],
  ['standing', 20, 'Back'],
  ['hanging', 210, 'Hanging by one hand'],
  ['inventory', 195, 'Checking inventory'],
  ['inventory', 270, 'Inventory, side'],
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
    const materials = model.textures.map((b64, i) => {
      const bytes = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
      const tex = new THREE.DataTexture(bytes, model.size, model.size, THREE.RGBAFormat);
      tex.magFilter = THREE.NearestFilter; tex.minFilter = THREE.NearestFilter;
      tex.wrapS = tex.wrapT = THREE.RepeatWrapping; tex.colorSpace = THREE.SRGBColorSpace; tex.needsUpdate = true;
      return new THREE.MeshLambertMaterial({ map: tex, emissive: i === 9 ? 0xfff0c0 : 0x000000, side: THREE.DoubleSide });
    });
    const scenes = {};
    for (const name of Object.keys(model.figures)) {
      const scene = new THREE.Scene();
      scene.background = new THREE.Color(0x16171a);
      scene.add(new THREE.HemisphereLight(0xc8ccd6, 0x2a2420, 1.6));
      const key = new THREE.DirectionalLight(0xffffff, 1.4); key.position.set(-2, 3, 3); scene.add(key);
      const rim = new THREE.DirectionalLight(0x99aaff, 0.6); rim.position.set(2, 2, -3); scene.add(rim);
      const bySlot = {};
      for (const t of model.figures[name]) {
        const g = (bySlot[t[0]] = bySlot[t[0]] || { p: [], n: [], uv: [] });
        for (let v = 0; v < 3; v++) {
          const o = 1 + v * 8;
          g.p.push(t[o], t[o + 1], -t[o + 2]);
          g.n.push(t[o + 3], t[o + 4], -t[o + 5]);
          g.uv.push(t[o + 6], t[o + 7]);
        }
      }
      for (const slot of Object.keys(bySlot)) {
        const g = new THREE.BufferGeometry();
        g.setAttribute('position', new THREE.Float32BufferAttribute(bySlot[slot].p, 3));
        g.setAttribute('normal', new THREE.Float32BufferAttribute(bySlot[slot].n, 3));
        g.setAttribute('uv', new THREE.Float32BufferAttribute(bySlot[slot].uv, 2));
        scene.add(new THREE.Mesh(g, materials[slot]));
      }
      const floor = new THREE.Mesh(new THREE.CircleGeometry(0.6, 32), new THREE.MeshStandardMaterial({ color: 0x24262b }));
      floor.rotation.x = -Math.PI / 2;
      if (name !== 'hanging') scene.add(floor);
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
