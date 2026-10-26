// Pruebas de src/CaeManager.Web/wwwroot/js/trazaSoporte.js con document y window falsos. Las corre el job
// deploy-script-tests de ci.yml con el Node del runner, sin dependencias. El módulo se carga desde su texto
// (data: URL) para no depender de cómo trate esa versión de Node un .js con export fuera de un paquete "module".
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const ruta = fileURLToPath(new URL('../../../src/CaeManager.Web/wwwroot/js/trazaSoporte.js', import.meta.url));
const { iniciar } = await import('data:text/javascript,' + encodeURIComponent(readFileSync(ruta, 'utf8')));

// Como el DOM: añadir dos veces el mismo manejador con la misma fase no lo duplica.
function objetivo() {
    const listeners = [];
    return {
        addEventListener(tipo, fn, captura) {
            if (listeners.some(l => l.tipo === tipo && l.fn === fn && l.captura === !!captura)) return;
            listeners.push({ tipo, fn, captura: !!captura });
        },
        removeEventListener(tipo, fn, captura) {
            const i = listeners.findIndex(l => l.tipo === tipo && l.fn === fn && l.captura === !!captura);
            if (i >= 0) listeners.splice(i, 1);
        },
        disparar(tipo, evento) { for (const l of [...listeners]) if (l.tipo === tipo) l.fn(evento); },
        cuantos(tipo) { return listeners.filter(l => l.tipo === tipo).length; }
    };
}
globalThis.document = objetivo();
globalThis.window = objetivo();
globalThis.location = { pathname: '/clientes' };

const clic = texto => ({ target: { closest: () => ({ getAttribute: a => (a === 'aria-label' ? texto : null), tagName: 'BUTTON' }) } });
const espera = ms => new Promise(r => setTimeout(r, ms));

/** Referencia .NET falsa: anota cada lote y, si se retiene, no resuelve hasta soltar. */
function referencia() {
    let retenidos = [];
    return {
        lotes: [],
        retener: false,
        invokeMethodAsync(metodo, lote) {
            assert.equal(metodo, 'RegistrarInteraccionesAsync');
            this.lotes.push(lote);
            return this.retener ? new Promise(r => retenidos.push(r)) : Promise.resolve();
        },
        soltar() { retenidos.forEach(r => r()); retenidos = []; }
    };
}

test('detener envía el lote pendiente, desengancha y resuelve solo cuando el lote está guardado', async () => {
    const ref = referencia();
    const captura = iniciar(ref);
    assert.equal(document.cuantos('click'), 1);
    assert.equal(window.cuantos('pagehide'), 1);

    document.disparar('click', clic('Uno'));
    document.disparar('click', clic('Dos'));
    ref.retener = true;
    let resuelto = false;
    const parada = captura.detener().then(() => { resuelto = true; });

    assert.deepEqual(ref.lotes, [['Uno — /clientes', 'Dos — /clientes']], 'una sola vez cada clic');
    assert.equal(document.cuantos('click'), 0);
    assert.equal(window.cuantos('pagehide'), 0);
    await espera(10);
    assert.equal(resuelto, false, 'detener no puede resolver antes de que se guarde el lote');
    ref.soltar();
    await parada;

    document.disparar('click', clic('Tras'));
    await espera(2100);
    assert.equal(ref.lotes.length, 1, 'ni temporizador residual ni listener tras detener');
    await captura.detener();
    assert.equal(ref.lotes.length, 1, 'detener dos veces no hace nada');
});

test('detener espera también a los lotes que ya iban en vuelo', async () => {
    const ref = referencia();
    const captura = iniciar(ref);
    ref.retener = true;
    for (let i = 0; i < 25; i++) document.disparar('click', clic('L' + i));
    assert.equal(ref.lotes.length, 1, 'el lote lleno sale al momento');

    let resuelto = false;
    const parada = captura.detener().then(() => { resuelto = true; });
    assert.equal(ref.lotes.length, 1, 'sin pendientes no hay lote nuevo');
    await espera(10);
    assert.equal(resuelto, false);
    ref.soltar();
    await parada;
});

test('dos capturas son independientes: detener una no desengancha la otra', async () => {
    const a = referencia();
    const b = referencia();
    const ca = iniciar(a);
    const cb = iniciar(b);
    assert.equal(document.cuantos('click'), 2);

    document.disparar('click', clic('X'));
    await ca.detener();
    assert.deepEqual(a.lotes, [['X — /clientes']]);
    assert.equal(document.cuantos('click'), 1);

    document.disparar('click', clic('Y'));
    await cb.detener();
    assert.deepEqual(b.lotes, [['X — /clientes', 'Y — /clientes']]);
    assert.equal(document.cuantos('click'), 0);
});

test('pagehide vacía sin desenganchar y el temporizador sigue enviando', async () => {
    const ref = referencia();
    const captura = iniciar(ref);
    document.disparar('click', clic('P'));
    window.disparar('pagehide', {});
    assert.deepEqual(ref.lotes, [['P — /clientes']]);
    assert.equal(document.cuantos('click'), 1, 'la página puede volver de la caché de historial');

    document.disparar('click', clic('T'));
    await espera(2100);
    assert.deepEqual(ref.lotes, [['P — /clientes'], ['T — /clientes']]);
    await captura.detener();
});

test('un circuito caído no rompe detener', async () => {
    const captura = iniciar({ invokeMethodAsync: () => Promise.reject(new Error('Circuito caído')) });
    document.disparar('click', clic('F'));
    await captura.detener();
    assert.equal(document.cuantos('click'), 0);
});
