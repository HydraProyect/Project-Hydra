// Pruebas de src/CaeManager.Web/wwwroot/js/dialogo-foco.js con un document falso: al abrir un diálogo el foco entra en él,
// pero si ya está dentro (el usuario o Playwright escribiendo en un campo antes de que la trampa se active) no se mueve (#2194).
// Las ejecuta DialogoFocoJsTests (dotnet test) con el Node de la máquina, sin dependencias.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const ruta = fileURLToPath(new URL('../../../src/CaeManager.Web/wwwroot/js/dialogo-foco.js', import.meta.url));

// Como trazaSoporte.test.mjs, el módulo se carga desde su texto (data: URL) para no depender de cómo trate la versión de
// Node un .js con export fuera de un paquete "module". Lee `document` al llamarse, no al importarse: basta con fijar el
// global antes de cada llamada.
const { atraparFoco } = await import('data:text/javascript,' + encodeURIComponent(readFileSync(ruta, 'utf8')));

function elemento(nombre, documento, padre = null) {
    const el = {
        nombre,
        padre,
        hijos: [],
        offsetParent: {},
        oyentes: {},
        focus() { documento.activeElement = el; },
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

function escena({ conCampos = true } = {}) {
    const documento = { activeElement: null, querySelector: () => null };
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
    return { documento, abridor, dialogo, primero, fecha };
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

test('con el foco en el propio contenedor, no se mueve al primer campo', () => {
    const { documento, dialogo } = escena();
    dialogo.focus();
    atraparFoco(dialogo);
    assert.equal(documento.activeElement, dialogo);
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
