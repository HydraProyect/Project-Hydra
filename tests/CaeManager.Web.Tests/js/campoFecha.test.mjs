// Pruebas de src/CaeManager.Web/wwwroot/js/campo-fecha.js con un document falso: la máscara dd/mm/aaaa da lo mismo que
// MascaraFecha.Aplicar (C#, mismos casos que CampoFechaTests) y el cursor queda donde toca. Las ejecuta CampoFechaTests
// (dotnet test) con el Node de la máquina, sin dependencias.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const ruta = fileURLToPath(new URL('../../../src/CaeManager.Web/wwwroot/js/campo-fecha.js', import.meta.url));
const codigo = readFileSync(ruta, 'utf8');

function entorno(globales = {}, ventana = {}) {
    const oyentes = {};
    const document = { addEventListener(tipo, f, captura) { oyentes[tipo] = { f, captura }; } };
    const window = ventana;
    vm.runInNewContext(codigo, { document, window, ...globales });
    return { oyentes, aplicar: window.talvegCampoFecha.aplicar, calendario: window.talvegCampoFecha };
}

function campo(valor, cursor, esFecha = true) {
    return {
        value: valor,
        selectionStart: cursor,
        seleccion: null,
        matches: (selector) => esFecha && selector === 'input[data-campo-fecha]',
        setSelectionRange(a, b) { this.seleccion = [a, b]; },
    };
}

const casos = [
    ['1', '1'], ['10', '10'], ['101', '10/1'], ['1012', '10/12'], ['10122', '10/12/2'], ['10122026', '10/12/2026'],
    ['101220261', '10/12/2026'], ['10/12/2026', '10/12/2026'], ['10/', '10/'], ['1/', '01/'], ['1/2/2026', '1/2/2026'], ['10/1/2026', '10/1/2026'],
    ['10//12', '10/12'], ['10-12-2026', '10/12/2026'], ['10.12.2026', '10/12/2026'], ['2026-12-10', '10/12/2026'],
    [' 2026-12-10 ', '10/12/2026'], ['ab10x12', '10/12'], ['', ''], ['/', ''],
];

test('la máscara coincide con MascaraFecha.Aplicar', () => {
    const { aplicar } = entorno();
    for (const [tecleado, esperado] of casos) assert.equal(aplicar(tecleado), esperado, JSON.stringify(tecleado));
});

test('escucha input y keydown en captura, antes que la delegación de Blazor', () => {
    const { oyentes } = entorno();
    assert.equal(oyentes.input.captura, true);
    assert.equal(oyentes.keydown.captura, true);
});

test('al teclear al final el cursor sigue al final, tras la barra insertada', () => {
    const { oyentes } = entorno();
    const c = campo('101', 3);
    oyentes.input.f({ target: c });
    assert.equal(c.value, '10/1');
    assert.deepEqual(c.seleccion, [4, 4]);
});

test('borrar una barra en medio la repone y el cursor no salta al final', () => {
    const { oyentes } = entorno();
    // «10/|12/2026» y Retroceso: el navegador deja «1012/2026» con el cursor en 2.
    const c = campo('1012/2026', 2);
    oyentes.input.f({ target: c });
    assert.equal(c.value, '10/12/2026');
    assert.deepEqual(c.seleccion, [2, 2], 'el cursor queda tras las mismas cifras que tenía delante');
});

test('corregir una cifra en medio no rellena con cero ni empuja el resto', () => {
    const { oyentes } = entorno();
    // «10/12/2026», se borra el «2» del mes: queda «10/1/2026» tal cual, a la espera de la cifra nueva.
    const c = campo('10/1/2026', 4);
    oyentes.input.f({ target: c });
    assert.equal(c.value, '10/1/2026');
    assert.equal(c.seleccion, null);
});

test('lo ya enmascarado no se reescribe (el cursor del navegador manda)', () => {
    const { oyentes } = entorno();
    const c = campo('10/1', 2);
    oyentes.input.f({ target: c });
    assert.equal(c.value, '10/1');
    assert.equal(c.seleccion, null);
});

test('pegar un texto sin cifras sobre la fecha no lo vacía: se queda a la vista para que el servidor lo dé por no válido', () => {
    const { oyentes } = entorno();
    // «10/12/2030» seleccionado y se pega «mañana»: vaciarlo haría creer al servidor que se borró a propósito (sin fin).
    for (const pegado of ['mañana', 'abc', '/', ' ']) {
        const c = campo(pegado, pegado.length);
        oyentes.input.f({ target: c });
        assert.equal(c.value, pegado, JSON.stringify(pegado));
        assert.equal(c.seleccion, null);
    }
});

test('pegar texto con alguna cifra sí se enmascara (y el servidor lo da por no válido si no es una fecha entera)', () => {
    const { oyentes } = entorno();
    const c = campo('mañana 5', 8);
    oyentes.input.f({ target: c });
    assert.equal(c.value, '5');
});

test('no toca otros campos', () => {
    const { oyentes } = entorno();
    const c = campo('101', 3, false);
    oyentes.input.f({ target: c });
    assert.equal(c.value, '101');
});

test('las flechas no desplazan la página dentro de la rejilla del calendario, y fuera sí', () => {
    const { oyentes } = entorno();
    let evitado = 0;
    const dentro = { closest: (s) => (s === '.campo-fecha-rejilla' ? {} : null) };
    const fuera = { closest: () => null };
    oyentes.keydown.f({ target: dentro, key: 'ArrowDown', preventDefault: () => evitado++ });
    oyentes.keydown.f({ target: dentro, key: 'Enter', preventDefault: () => evitado++ });
    oyentes.keydown.f({ target: fuera, key: 'ArrowDown', preventDefault: () => evitado++ });
    assert.equal(evitado, 1);
});

// El calendario anclado: panel y campo falsos, con el reloj de cuadros síncrono y un MutationObserver que el test dispara.
function calendarioAnclado({ conObservador = true } = {}) {
    const observadores = [];
    class ObservadorFalso {
        constructor(llamada) { this.llamada = llamada; this.opciones = null; this.desconectado = false; observadores.push(this); }
        observe(objetivo, opciones) { this.objetivo = objetivo; this.opciones = opciones; }
        disconnect() { this.desconectado = true; }
    }
    const globales = { requestAnimationFrame: (f) => { f(); return 1; }, cancelAnimationFrame: () => {} };
    if (conObservador) globales.MutationObserver = ObservadorFalso;
    const ventana = { innerWidth: 1000, innerHeight: 600, addEventListener() {}, removeEventListener() {} };
    const { calendario } = entorno(globales, ventana);
    // El campo está pegado al borde inferior de la ventana: abajo no cabe el calendario y se coloca encima.
    const fila = { getBoundingClientRect: () => ({ top: 540, bottom: 570, right: 900 }) };
    const panel = {
        offsetWidth: 300, offsetHeight: 300, isConnected: true, style: {}, dataset: {},
        setAttribute() {}, showPopover() {},
    };
    calendario.anclar(panel, fila, 'cf-1');
    return { calendario, panel, observadores };
}

test('el calendario volteado hacia arriba se recoloca al cambiar de mes y pasar de 5 a 6 filas', () => {
    const { panel, observadores } = calendarioAnclado();
    assert.equal(panel.dataset.lado, 'arriba', 'control: al abrirse no cabe debajo y va encima del campo');
    assert.equal(panel.style.top, (540 - 4 - 300) + 'px');

    // Blazor dibuja el mes siguiente con una fila más: el panel crece 40 px y el observador avisa del redibujado.
    panel.offsetHeight = 340;
    assert.equal(observadores.length, 1, 'el panel se observa mientras está anclado');
    assert.equal(observadores[0].objetivo, panel);
    const { childList, subtree } = observadores[0].opciones; // el objeto viene del contexto de vm: se compara por campos
    assert.deepEqual([childList, subtree], [true, true]);
    observadores[0].llamada([]);

    assert.equal(panel.style.top, (540 - 4 - 340) + 'px', 'el borde de abajo sigue por encima del campo (sin tapar el campo)');
    assert.equal(panel.dataset.lado, 'arriba');
});

test('al soltar el calendario se deja de observar', () => {
    const { calendario, observadores } = calendarioAnclado();

    calendario.soltar('cf-1');

    assert.equal(observadores[0].desconectado, true);
});

test('sin MutationObserver el calendario se ancla igual (se coloca al abrir, en resize y en scroll)', () => {
    const { panel } = calendarioAnclado({ conObservador: false });

    assert.equal(panel.dataset.lado, 'arriba');
});
