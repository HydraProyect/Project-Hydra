// Pruebas de src/CaeManager.Web/wwwroot/js/dialogo-foco.js con un document falso: al abrir un diálogo el foco entra en él,
// pero si ya está en un campo (el usuario o Playwright escribiendo antes de que la trampa se active) no se mueve, y al
// cerrar vuelve a quien abrió el diálogo aunque el foco ya estuviera dentro al activarse la trampa (#2194).
// Las ejecuta DialogoFocoJsTests (dotnet test) con el Node de la máquina, sin dependencias.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const ruta = fileURLToPath(new URL('../../../src/CaeManager.Web/wwwroot/js/dialogo-foco.js', import.meta.url));

// Como trazaSoporte.test.mjs, el módulo se carga desde su texto (data: URL) para no depender de cómo trate la versión de
// Node un .js con export fuera de un paquete "module". Lee `document` al llamarse, no al importarse: basta con fijar el
// global antes de cada llamada.
const { atraparFoco, rastrearFoco } = await import('data:text/javascript,' + encodeURIComponent(readFileSync(ruta, 'utf8')));

function emitir(documento, tipo, evento) {
    for (const f of documento.oyentes[tipo] ?? []) f(evento);
}

function elemento(nombre, documento, padre = null) {
    const el = {
        nombre,
        padre,
        hijos: [],
        offsetParent: {},
        oyentes: {},
        // Como el navegador: focusout en el que lo pierde (con el que lo gana como relatedTarget) y focusin en el nuevo.
        focus() {
            const anterior = documento.activeElement;
            if (anterior === el) return;
            if (anterior && anterior !== documento.body) emitir(documento, 'focusout', { target: anterior, relatedTarget: el });
            documento.activeElement = el;
            emitir(documento, 'focusin', { target: el });
        },
        contains(otro) {
            for (let actual = otro; actual; actual = actual.padre) if (actual === el) return true;
            return false;
        },
        addEventListener(tipo, f) { el.oyentes[tipo] = f; },
        removeEventListener(tipo) { delete el.oyentes[tipo]; },
        querySelectorAll() {
            const todos = [];
            const recorrer = (n) => { for (const h of n.hijos) { if (h.enfocable) todos.push(h); recorrer(h); } };
            recorrer(el);
            return todos;
        },
    };
    if (padre) padre.hijos.push(el);
    return el;
}

// rastrear: la copia del módulo que App.razor carga al arrancar ya escucha el foco antes de que se abra el diálogo.
function escena({ conCampos = true, rastrear = true } = {}) {
    const documento = {
        activeElement: null,
        oyentes: {},
        querySelector: () => null,
        addEventListener(tipo, f) { (documento.oyentes[tipo] ??= []).push(f); },
    };
    const cuerpo = elemento('body', documento);
    documento.body = cuerpo;
    documento.contains = (otro) => cuerpo.contains(otro);
    const abridor = elemento('abridor', documento, cuerpo);
    abridor.enfocable = true;
    const dialogo = elemento('dialogo', documento, cuerpo);
    let primero = null;
    let fecha = null;
    if (conCampos) {
        primero = elemento('primero', documento, dialogo);
        primero.enfocable = true;
        fecha = elemento('fecha', documento, dialogo);
        fecha.enfocable = true;
    }
    globalThis.document = documento;
    globalThis.HTMLElement = Object;
    if (rastrear) rastrearFoco(documento);
    // Un clic en algo no enfocable: el foco pasa al body sin focusin, solo con un focusout sin relatedTarget.
    const clicEnNoEnfocable = () => {
        const anterior = documento.activeElement;
        if (anterior) emitir(documento, 'focusout', { target: anterior, relatedTarget: null });
        documento.activeElement = cuerpo;
    };
    return { documento, cuerpo, abridor, dialogo, primero, fecha, clicEnNoEnfocable };
}

function tab(contenedor, { shiftKey = false } = {}) {
    const evento = { key: 'Tab', shiftKey, defaultPrevented: false, prevenido: false, preventDefault() { evento.prevenido = true; } };
    contenedor.oyentes.keydown(evento);
    return evento;
}

test('con el foco fuera, al abrir el diálogo el foco entra en el primer enfocable', () => {
    const { documento, abridor, dialogo, primero } = escena();
    abridor.focus();
    atraparFoco(dialogo);
    assert.equal(documento.activeElement, primero);
});

test('con el foco ya en un campo del diálogo, la trampa no se lo quita (#2194)', () => {
    const { documento, dialogo, fecha } = escena();
    fecha.focus();
    atraparFoco(dialogo);
    assert.equal(documento.activeElement, fecha);
});

test('con el foco en el propio contenedor, pasa al primer enfocable y Mayús+Tab no sale del diálogo', () => {
    const { documento, dialogo, primero, fecha } = escena();
    dialogo.focus();
    atraparFoco(dialogo);
    assert.equal(documento.activeElement, primero);
    const evento = tab(dialogo, { shiftKey: true });
    assert.equal(evento.prevenido, true);
    assert.equal(documento.activeElement, fecha);
});

test('si el contenedor recibe el foco después (clic en el fondo del diálogo), Mayús+Tab va al último enfocable', () => {
    const { documento, abridor, dialogo, fecha } = escena();
    abridor.focus();
    atraparFoco(dialogo);
    dialogo.focus();
    const evento = tab(dialogo, { shiftKey: true });
    assert.equal(evento.prevenido, true);
    assert.equal(documento.activeElement, fecha);
});

test('Tab desde el último enfocable vuelve al primero', () => {
    const { documento, abridor, dialogo, primero, fecha } = escena();
    abridor.focus();
    atraparFoco(dialogo);
    fecha.focus();
    const evento = tab(dialogo);
    assert.equal(evento.prevenido, true);
    assert.equal(documento.activeElement, primero);
});

test('sin enfocables y con el foco fuera, el foco cae en el contenedor', () => {
    const { documento, abridor, dialogo } = escena({ conCampos: false });
    abridor.focus();
    atraparFoco(dialogo);
    assert.equal(documento.activeElement, dialogo);
});

test('sin elemento activo, el foco entra en el diálogo', () => {
    const { documento, dialogo, primero } = escena();
    documento.activeElement = null;
    atraparFoco(dialogo);
    assert.equal(documento.activeElement, primero);
});

test('al liberar la trampa, el foco vuelve a quien abrió el diálogo', () => {
    const { documento, abridor, dialogo } = escena();
    abridor.focus();
    const trampa = atraparFoco(dialogo);
    trampa.dispose();
    assert.equal(documento.activeElement, abridor);
    assert.equal(dialogo.oyentes.keydown, undefined);
});

test('con el foco ya en un campo al activarse la trampa, al cerrar vuelve a quien abrió el diálogo (#2194)', () => {
    const { documento, abridor, dialogo, fecha } = escena();
    abridor.focus();
    fecha.focus();
    const trampa = atraparFoco(dialogo);
    assert.equal(documento.activeElement, fecha);
    trampa.dispose();
    assert.equal(documento.activeElement, abridor);
});

test('en un diálogo abierto desde otro, al cerrar vuelve al botón del diálogo de fuera aunque el foco ya estuviera dentro', () => {
    const { documento, cuerpo, abridor, dialogo, primero } = escena();
    abridor.focus();
    const trampaFuera = atraparFoco(dialogo);
    const interior = elemento('interior', documento, cuerpo);
    const campoInterior = elemento('campoInterior', documento, interior);
    campoInterior.enfocable = true;
    primero.focus();
    campoInterior.focus();
    const trampaDentro = atraparFoco(interior);
    trampaDentro.dispose();
    assert.equal(documento.activeElement, primero);
    trampaFuera.dispose();
    assert.equal(documento.activeElement, abridor);
});

test('si el diálogo se abrió desde algo no enfocable, al cerrar no salta a un elemento de fuera más antiguo', () => {
    const { documento, cuerpo, abridor, dialogo, fecha, clicEnNoEnfocable } = escena();
    abridor.focus();
    clicEnNoEnfocable();
    fecha.focus();
    const trampa = atraparFoco(dialogo);
    trampa.dispose();
    assert.notEqual(documento.activeElement, abridor);
    assert.equal(documento.activeElement, cuerpo);
});

test('sin historial del foco (nadie anotó el foco antes de abrir) y con el foco ya dentro, al cerrar no se mueve', () => {
    const { documento, abridor, dialogo, fecha } = escena({ rastrear: false });
    abridor.focus();
    fecha.focus();
    const trampa = atraparFoco(dialogo);
    trampa.dispose();
    assert.equal(documento.activeElement, fecha);
});
