import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createServer } from 'node:http';
import { cp, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, extname, resolve, sep, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const raiz = resolve(dirname(fileURLToPath(import.meta.url)), '../..');

test('MV3 arranca e inyecta el content script en el portal real', { timeout: 60_000 }, async () => {
  const temporal = await mkdtemp(join(tmpdir(), 'talveg-chromium-'));
  const portal = join(raiz, 'tools/PortalPruebaExtension');
  const servidor = createServer(async (peticion, respuesta) => {
    try {
      const ruta = resolve(portal, '.' + decodeURIComponent(new URL(peticion.url, 'http://localhost').pathname));
      if (!ruta.startsWith(portal + sep)) {
        respuesta.writeHead(403).end();
        return;
      }
      const contenido = await readFile(ruta);
      respuesta.writeHead(200, { 'Content-Type': extname(ruta) === '.js' ? 'text/javascript; charset=utf-8' : 'text/html; charset=utf-8' });
      respuesta.end(contenido);
    } catch {
      respuesta.writeHead(404).end();
    }
  });
  let contexto;
  try {
    await new Promise((aceptar, rechazar) => {
      servidor.once('error', rechazar);
      servidor.listen(0, '127.0.0.1', aceptar);
    });
    const origen = `http://127.0.0.1:${servidor.address().port}`;
    const extension = join(temporal, 'extension');
    await cp(join(raiz, 'extension'), extension, {
      recursive: true,
      filter: ruta => !['node_modules', 'pruebas'].includes(ruta.split(sep).at(-1)),
    });
    const manifiesto = JSON.parse(await readFile(join(extension, 'manifest.json'), 'utf8'));
    manifiesto.host_permissions.push(`${origen}/*`);
    for (const script of manifiesto.content_scripts) script.matches.push(`${origen}/*`);
    await writeFile(join(extension, 'manifest.json'), JSON.stringify(manifiesto));

    contexto = await chromium.launchPersistentContext(join(temporal, 'perfil'), {
      channel: 'chromium',
      headless: false,
      args: ['--headless=new', `--disable-extensions-except=${extension}`, `--load-extension=${extension}`],
    });
    const trabajador = contexto.serviceWorkers().find(w => w.url().endsWith('/background.js'))
      ?? await contexto.waitForEvent('serviceworker', { timeout: 15_000 });
    assert.match(trabajador.url(), /^chrome-extension:\/\/[a-p]{32}\/background\.js$/);
    assert.ok(contexto.serviceWorkers().includes(trabajador));
    const origenExtension = trabajador.url().replace(/\/background\.js$/, '');

    const pagina = await contexto.newPage();
    const cdp = await contexto.newCDPSession(pagina);
    const mundos = [];
    cdp.on('Runtime.executionContextCreated', ({ context }) => mundos.push(context));
    await cdp.send('Runtime.enable');
    await pagina.goto(`${origen}/input-simple.html`);
    await pagina.locator('#archivo').waitFor();
    // CDP observa el mundo aislado: el portal no puede fingir que cargó content.js.
    let inyectado = false;
    for (let intento = 0; intento < 100 && !inyectado; intento++) {
      for (const mundo of mundos.filter(m => m.origin === origenExtension || m.name === origenExtension)) {
        const resultado = await cdp.send('Runtime.evaluate', {
          contextId: mundo.id,
          expression: 'typeof abrirPanel === "function" && typeof refrescarConexionAsync === "function"',
          returnByValue: true,
        });
        if (resultado.result.value === true) inyectado = true;
      }
      if (!inyectado) await new Promise(r => setTimeout(r, 100));
    }
    assert.ok(inyectado, 'content.js debe ejecutarse en el mundo aislado de la extensión');
  } finally {
    try {
      await contexto?.close();
    } finally {
      await new Promise((aceptar, rechazar) => servidor.close(error => error ? rechazar(error) : aceptar()));
      await rm(temporal, { recursive: true, force: true });
    }
  }
});
