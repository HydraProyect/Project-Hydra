// Pruebas de src/CaeManager.Web/wwwroot/js/reto-prueba-trabajo.js (el guion de página del reto) con window, document,
// Worker, reloj y temporizadores falsos, sin dependencias. Las corre el job deploy-script-tests de ci.yml. Fijan la
// retención del envío: se retiene sin solución o antes del tiempo mínimo, se repite sola con el mismo botón, corre en
// captura sobre window (antes que acceso-doble-envio.js, que escucha en captura sobre document) y, si el reto falla,
// deja de retener para no dejar el formulario mudo.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const ruta = fileURLToPath(new URL('../../../src/CaeManager.Web/wwwroot/js/reto-prueba-trabajo.js', import.meta.url));
const texto = readFileSync(ruta, 'utf8');

function montarPagina({ conWorker = true, esperaMs = 3000, vigenciaMs = 1200000 } = {}) {
    let reloj = 0;
    const temporizadores = [];
    const oyentesWindow = [];
    const trabajadores = [];
    const envios = [];
    const oyentesBlazor = {};

    const aviso = { textContent: 'comprobando' };
    const campo = { value: 'x' };
    const atributos = {
        'data-reto': 'token-del-reto', 'data-bits': '8', 'data-espera-minima-ms': String(esperaMs), 'data-vigencia-ms': String(vigenciaMs),
        'data-trabajador': '/js/reto-prueba-trabajo-worker.js',
        'data-texto-listo': 'listo', 'data-texto-esperando': 'esperando', 'data-texto-error': 'error', 'data-texto-caducado': 'caducado',
    };
    const formulario = {
        querySelector: (s) => (s === '[data-reto-prueba-trabajo]' ? contenedor : null),
        requestSubmit: (boton) => envios.push(boton ?? null),
    };
    const contenedor = {
        getAttribute: (n) => atributos[n] ?? null,
        querySelector: (s) => (s === '[data-reto-solucion]' ? campo : s === '[data-reto-estado]' ? aviso : null),
        closest: () => formulario,
        isConnected: true,
    };
    const enDocumento = [contenedor];

    const window = {
        addEventListener: (tipo, fn, captura) => oyentesWindow.push({ tipo, fn, captura: !!captura }),
        Blazor: { addEventListener: (tipo, fn) => { oyentesBlazor[tipo] = fn; } },
    };
    const document = {
        readyState: 'complete',
        querySelectorAll: () => enDocumento,
        addEventListener: () => {},
    };
    class TrabajadorFalso {
        constructor(url) { this.url = url; this.mensajes = []; this.terminado = false; trabajadores.push(this); }
        postMessage(m) { this.mensajes.push(m); }
        terminate() { this.terminado = true; }
    }
    const setTimeout = (fn, ms) => { const t = { fn, vence: reloj + ms }; temporizadores.push(t); return t; };
    const clearTimeout = (t) => { const i = temporizadores.indexOf(t); if (i >= 0) temporizadores.splice(i, 1); };
    const performance = { now: () => reloj };

    new Function('window', 'document', 'Worker', 'performance', 'setTimeout', 'clearTimeout', texto)(
        window, document, conWorker ? TrabajadorFalso : undefined, performance, setTimeout, clearTimeout);

    function enviar(boton = { id: 'boton' }) {
        const evento = {
            target: formulario, submitter: boton, cancelado: false, parado: false,
            preventDefault() { this.cancelado = true; },
            stopImmediatePropagation() { this.parado = true; },
        };
        for (const o of oyentesWindow.filter((o) => o.tipo === 'submit')) o.fn(evento);
        return evento;
    }

    function avanzar(ms) {
        reloj += ms;
        for (const t of temporizadores.filter((t) => t.vence <= reloj)) { clearTimeout(t); t.fn(); }
    }

    function navegarFuera() {
        contenedor.isConnected = false;
        enDocumento.length = 0;
        oyentesBlazor.enhancedload();
    }

    return { oyentesWindow, trabajadores, envios, aviso, campo, enviar, avanzar, navegarFuera };
}

test('arranca el worker al montar con el reto y los bits, sin esperar a la persona', () => {
    const p = montarPagina();
    assert.equal(p.trabajadores.length, 1);
    assert.deepEqual(p.trabajadores[0].mensajes, [{ reto: 'token-del-reto', bits: 8 }]);
    assert.equal(p.campo.value, '');
    assert.ok(p.oyentesWindow.some((o) => o.tipo === 'submit' && o.captura), 'escucha submit en captura sobre window');
});

test('un envío sin solución se retiene y se repite solo con el mismo botón al resolverse', () => {
    const p = montarPagina();
    p.avanzar(3500);
    const boton = { id: 'enviar' };
    const evento = p.enviar(boton);

    assert.ok(evento.cancelado && evento.parado, 'ni acceso-doble-envio ni Blazor ven el envío retenido');
    assert.equal(p.aviso.textContent, 'esperando');
    assert.equal(p.envios.length, 0);

    p.trabajadores[0].onmessage({ data: { nonce: '2413' } });
    p.avanzar(0);

    assert.equal(p.campo.value, '2413');
    assert.equal(p.aviso.textContent, 'listo');
    assert.deepEqual(p.envios, [boton]);
    assert.ok(p.trabajadores[0].terminado);
    assert.equal(p.enviar(boton).cancelado, false, 'la repetición pasa');
});

test('resuelto antes de tiempo, el envío espera a la trampa de tiempo y no sale antes', () => {
    const p = montarPagina({ esperaMs: 3000 });
    p.trabajadores[0].onmessage({ data: { nonce: '7' } });
    p.avanzar(1000);

    assert.equal(p.enviar().cancelado, true);
    p.avanzar(1999);
    assert.equal(p.envios.length, 0, 'a los 2,999 s aún no sale');
    p.avanzar(1);
    assert.equal(p.envios.length, 1, 'a los 3 s sale solo');
});

test('resuelto y pasado el tiempo mínimo, el envío sale sin retención', () => {
    const p = montarPagina();
    p.trabajadores[0].onmessage({ data: { nonce: '7' } });
    p.avanzar(3000);
    assert.equal(p.enviar().cancelado, false);
});

test('si el reto falla, se avisa y el siguiente envío sale sin retener', () => {
    const p = montarPagina();
    p.avanzar(3500);
    assert.equal(p.enviar().cancelado, true);

    p.trabajadores[0].onerror();
    assert.equal(p.aviso.textContent, 'error');
    assert.equal(p.envios.length, 0, 'el retenido no se repite solo sin solución');

    assert.equal(p.enviar().cancelado, false);
    assert.equal(p.aviso.textContent, 'error', 'el aviso de error no se pisa');
});

test('sin Web Worker se avisa del error y el formulario no queda retenido', () => {
    const p = montarPagina({ conWorker: false });
    assert.equal(p.aviso.textContent, 'error');
    assert.equal(p.enviar().cancelado, false);
});

test('al caducar el reto se para el cálculo, se avisa y el envío deja de retenerse', () => {
    const p = montarPagina({ vigenciaMs: 60000 });
    p.avanzar(3500);
    assert.equal(p.enviar().cancelado, true, 'sin solución, retenido');

    p.avanzar(60000 - 10000 - 3500 - 1);
    assert.equal(p.trabajadores[0].terminado, false, 'un instante antes del margen sigue calculando');
    p.avanzar(1);

    assert.ok(p.trabajadores[0].terminado, 'el worker se termina al caducar');
    assert.equal(p.aviso.textContent, 'caducado');
    assert.equal(p.envios.length, 0, 'el retenido no se repite con un reto caducado');

    p.trabajadores[0].onmessage({ data: { nonce: '99' } });
    assert.equal(p.campo.value, '', 'una solución tardía no se usa');
    assert.equal(p.aviso.textContent, 'caducado');
    assert.equal(p.enviar().cancelado, false, 'el siguiente envío sale y el servidor responde');
});

test('resuelto y retenido por la trampa de tiempo, caducar cancela la repetición pendiente', () => {
    const p = montarPagina({ esperaMs: 3000, vigenciaMs: 12000 });
    p.trabajadores[0].onmessage({ data: { nonce: '7' } });
    assert.equal(p.enviar().cancelado, true);
    p.avanzar(2000);

    assert.equal(p.aviso.textContent, 'caducado', 'vigencia de 12 s menos el margen de 10 s');
    p.avanzar(5000);
    assert.equal(p.envios.length, 0);
});

test('al salir del formulario por navegación mejorada se termina su worker', () => {
    const p = montarPagina();
    assert.equal(p.trabajadores[0].terminado, false);

    p.navegarFuera();

    assert.ok(p.trabajadores[0].terminado, 'el worker del contenedor retirado no sigue calculando');
    p.avanzar(1200000);
    assert.equal(p.aviso.textContent, 'comprobando', 'tampoco queda vivo el temporizador de caducidad');
});