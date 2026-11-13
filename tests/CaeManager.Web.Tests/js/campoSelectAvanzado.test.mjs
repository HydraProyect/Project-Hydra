// Pruebas de src/CaeManager.Web/wwwroot/js/campo-select-avanzado.js con un document falso: al cerrar la lista, el foco
// vuelve al disparador si sigue dentro del componente o cayó al body, pero no se le quita a otro control al que ya se
// fue mientras llegaba la orden del servidor (#2194: Playwright rellenaba el campo siguiente y el foco devuelto a
// destiempo lo dejaba vacío). Las ejecuta CampoSelectAvanzadoJsTests (dotnet test) con el Node de la máquina.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const ruta = fileURLToPath(new URL('../../../src/CaeManager.Web/wwwroot/js/campo-select-avanzado.js', import.meta.url));

// Como dialogoFoco.test.mjs: el módulo se carga desde su texto y lee `document` al llamarse, no al importarse.
const { crear } = await import('data:text/javascript,' + encodeURIComponent(readFileSync(ruta, 'utf8')));

function elemento(nombre, documento, padre = null) {
    const el = {
        nombre,
        padre,
        enfoques: 0,
        focus() { el.enfoques++; documento.activeElement = el; },
        contains(otro) {
            for (let actual = otro; actual; actual = actual.padre) if (actual === el) return true;
            return false;
        },
        addEventListener() { },
        removeEventListener() { },
    };
    return el;
}

function escena() {
    const documento = { activeElement: null };
    const html = elemento('html', documento);
    documento.documentElement = html;
    const cuerpo = elemento('body', documento, html);
    documento.body = cuerpo;
    const raiz = elemento('csa', documento, cuerpo);
    const disparador = elemento('disparador', documento, raiz);
    const lista = elemento('lista', documento, raiz);
    const otroCampo = elemento('fecha-de-emision', documento, cuerpo);
    globalThis.document = documento;
    return { documento, cuerpo, raiz, disparador, lista, otroCampo, control: crear(raiz) };
}

test('el foco vuelve al disparador si sigue dentro del componente (la lista que se pulsó)', () => {
    const { documento, disparador, lista, control } = escena();
    documento.activeElement = lista;
    control.devolverFoco(disparador);
    assert.equal(disparador.enfoques, 1);
    assert.equal(documento.activeElement, disparador);
});

test('el foco vuelve al disparador si cayó al body al desaparecer la lista', () => {
    const { documento, cuerpo, disparador, control } = escena();
    documento.activeElement = cuerpo;
    control.devolverFoco(disparador);
    assert.equal(documento.activeElement, disparador);
});

test('el foco vuelve al disparador si no hay elemento activo', () => {
    const { documento, disparador, control } = escena();
    documento.activeElement = null;
    control.devolverFoco(disparador);
    assert.equal(documento.activeElement, disparador);
});

test('el foco vuelve al disparador si está en el documento raíz o en el propio disparador', () => {
    for (const activo of ['documentElement', 'disparador']) {
        const s = escena();
        s.documento.activeElement = activo === 'documentElement' ? s.documento.documentElement : s.disparador;
        s.control.devolverFoco(s.disparador);
        assert.equal(s.disparador.enfoques, 1, activo);
        assert.equal(s.documento.activeElement, s.disparador, activo);
    }
});

test('no quita el foco a otro campo al que ya se fue (#2194)', () => {
    const { documento, disparador, otroCampo, control } = escena();
    documento.activeElement = otroCampo;
    control.devolverFoco(disparador);
    assert.equal(disparador.enfoques, 0);
    assert.equal(documento.activeElement, otroCampo);
});
