import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createServer } from 'node:http';
import { cp, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, extname, resolve, sep, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { idDeExtension } from '../herramientas/empaquetado.mjs';

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
    // La `key` del manifiesto fija el ID: si Chromium no la respeta, el ID cambia.
    const idEsperado = idDeExtension(manifiesto.key);
    assert.equal(trabajador.url(), `chrome-extension://${idEsperado}/background.js`);
    assert.ok(contexto.serviceWorkers().includes(trabajador));
    const origenExtension = `chrome-extension://${idEsperado}`;

    // CDP observa el mundo aislado de la extensión: el portal no puede fingir
    // desde el suyo que cargó content.js. Se comprueba un efecto en tiempo de
    // ejecución, no solo declaraciones `function` (que existen por elevación
    // aunque el guion lance en su primera sentencia): el listener de `focusin`
    // registrado al final del nivel superior de content.js tiene que haber
    // anotado el campo enfocado en `campoElegido`.
    const contentEjecutado = async (url, intentos) => {
      const pagina = await contexto.newPage();
      const cdp = await contexto.newCDPSession(pagina);
      const mundos = [];
      cdp.on('Runtime.executionContextCreated', ({ context }) => mundos.push(context));
      await cdp.send('Runtime.enable');
      await pagina.goto(url);
      await pagina.locator('#archivo').waitFor();
      try {
        for (let intento = 0; intento < intentos; intento++) {
          await pagina.focus('#archivo');
          for (const mundo of mundos.filter(m => m.origin === origenExtension || m.name === origenExtension)) {
            const resultado = await cdp.send('Runtime.evaluate', {
              contextId: mundo.id,
              expression: 'typeof hayConexion === "boolean" && campoElegido instanceof HTMLInputElement && campoElegido.id === "archivo"',
              returnByValue: true,
            });
            if (resultado.result.value === true) return true;
          }
          await pagina.evaluate(() => document.activeElement?.blur());
          await new Promise(r => setTimeout(r, 100));
        }
        return false;
      } finally {
        await pagina.close();
      }
    };

    assert.ok(await contentEjecutado(`${origen}/input-simple.html`, 100),
      'content.js debe ejecutarse en el mundo aislado de la extensión');
    // Control negativo: el mismo portal por `localhost` no está en los
    // `matches` del manifiesto. Si aquí también «se inyecta», el filtro por
    // origen del mundo aislado es vacuo y el positivo no demuestra nada.
    const origenFueraDeMatches = origen.replace('127.0.0.1', 'localhost');
    assert.equal(await contentEjecutado(`${origenFueraDeMatches}/input-simple.html`, 20), false,
      'content.js no debe ejecutarse en un origen fuera de los matches del manifiesto');
  } finally {
    try {
      await contexto?.close();
    } finally {
      await new Promise((aceptar, rechazar) => servidor.close(error => error ? rechazar(error) : aceptar()));
      await rm(temporal, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
    }
  }
});
