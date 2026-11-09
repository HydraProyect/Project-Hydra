// Pruebas del teclado de src/CaeManager.Web/wwwroot/js/cabecera.js con un document falso: cuando el foco
// está en body, AvPág, RePág, Espacio, Inicio, Fin y las flechas desplazan .contenido (el documento ya no
// desplaza). Las ejecuta ShellDesplazaContenidoTests (dotnet test) con el Node de la máquina, sin dependencias.
// El script se evalúa entero en un vm: cada guarda del manejador se prueba por su efecto (preventDefault y
// scrollTo), no por el texto, de modo que borrar o invertir una guarda pone en rojo el caso que la ejerce.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const ruta = fileURLToPath(new URL('../../../src/CaeManager.Web/wwwroot/js/cabecera.js', import.meta.url));
const codigo = readFileSync(ruta, 'utf8');

class Element {
    constructor(opciones) {
        Object.assign(this, { parentElement: null, overflowY: 'visible', scrollHeight: 0, clientHeight: 0, isConnected: true }, opciones);
    }
}

// Cada nodo es un clic posible. 'menu' y 'panel' desbordan (son scrollers de verdad); 'ajustado' y 'oculto' no cuentan.
function entorno() {
    const oyentes = [];
    const llamadas = [];
    const body = new Element({});
    const raiz = new Element({});
    // Como main.contenido real (MainLayout.razor.css): overflow-y auto y desbordado. Con 'visible' el bucle de
    // dentroDeOtroScroller pararía solo en él aunque perdiera su cota «n !== contenido».
    const contenido = new Element({ overflowY: 'auto', scrollTop: 100, clientHeight: 800, scrollHeight: 5000, id: 'contenido-principal' });
    contenido.scrollTo = function (arg) { llamadas.push(arg); };
    const nodos = {
        texto: new Element({ parentElement: contenido }),
        menu: new Element({ overflowY: 'auto', scrollHeight: 900, clientHeight: 400 }),
        panel: new Element({ overflowY: 'scroll', scrollHeight: 900, clientHeight: 400 }),
        ajustado: new Element({ overflowY: 'auto', scrollHeight: 400, clientHeight: 400 }),
        oculto: new Element({ overflowY: 'hidden', scrollHeight: 900, clientHeight: 400 }),
        desconectado: new Element({ overflowY: 'auto', scrollHeight: 900, clientHeight: 400, isConnected: false }),
        control: new Element({}),
        noElemento: {},
    };
    nodos.menuHijo = new Element({ parentElement: nodos.menu });
    nodos.contenido = contenido; // clic sobre el propio .contenido: el scroller al que pertenecen las teclas
    // El menú y el panel contienen un nodo que no desborda y cuyo ancestro sí: el bucle debe subir.
    const estado = { activo: body, modal: null };
    const documento = {
        body, documentElement: raiz,
        get activeElement() { return estado.activo; },
        addEventListener(tipo, fn) { oyentes.push({ tipo, fn }); },
        getElementById(id) { return id === 'contenido-principal' && !estado.sinContenido ? contenido : null; },
        querySelectorAll() { return []; },
        // El menú de usuario nunca está abierto aquí. Un modal se ve si su variante aparece en el selector pedido.
        querySelector(selector) {
            if (selector.includes('data-menu-cabecera')) return null;
            return estado.modal && selector.includes(estado.modal) ? {} : null;
        },
    };
    const ventana = { addEventListener() {}, matchMedia: () => ({ matches: false }) };
    vm.runInNewContext(codigo, {
        document: documento, window: ventana, Element, location: { pathname: '/', search: '', hash: '' },
        getComputedStyle: (n) => ({ overflowY: n.overflowY }), setTimeout() {}, MutationObserver: class { observe() {} disconnect() {} },
    });
    function emitir(tipo, evento) { for (const o of oyentes.filter(x => x.tipo === tipo)) o.fn(evento); }
    function pulsar(key, extra = {}) {
        llamadas.length = 0;
        const e = { key, shiftKey: false, ctrlKey: false, altKey: false, metaKey: false, defaultPrevented: false, ...extra };
        e.preventDefault = function () { e.defaultPrevented = true; e.llamoPreventDefault = true; };
        emitir('keydown', e);
        return { previene: !!e.llamoPreventDefault, scroll: llamadas[0] ? { ...llamadas[0] } : null }; // copia: el objeto nace en el contexto del vm
    }
    return {
        pulsar, nodos, contenido, body, raiz, estado,
        clic(nodo) { emitir('pointerdown', { target: nodo }); },
        enfocar(nodo) { estado.activo = nodo; emitir('focusin', { target: nodo }); },
    };
}

const LOS_QUE_DESPLAZAN = [
    ['PageDown', 800, undefined], ['PageUp', -600, undefined], [' ', 800, undefined],
    ['ArrowDown', 140, 'instant'], ['ArrowUp', 60, 'instant'], ['Home', 0, undefined], ['End', 5000, undefined],
];

test('con el foco en body cada tecla de desplazamiento desplaza .contenido y evita el desplazamiento del documento', () => {
    for (const [tecla, top, behavior] of LOS_QUE_DESPLAZAN) {
        const r = entorno().pulsar(tecla);
        assert.equal(r.previene, true, `${JSON.stringify(tecla)} debe llamar a preventDefault`);
        assert.deepEqual(r.scroll, behavior ? { top, behavior } : { top }, `${JSON.stringify(tecla)} desplaza .contenido`);
    }
});

test('Mayús + Espacio sube una página', () => {
    const r = entorno().pulsar(' ', { shiftKey: true });
    assert.equal(r.previene, true);
    assert.deepEqual(r.scroll, { top: -600 });
});

test('con documentElement como elemento activo (o ninguno) también desplaza', () => {
    const a = entorno(); a.estado.activo = a.raiz;
    assert.equal(a.pulsar('PageDown').previene, true, 'documentElement activo');
    const b = entorno(); b.estado.activo = null;
    assert.equal(b.pulsar('PageDown').previene, true, 'sin elemento activo');
});

test('un control enfocado conserva sus teclas (activo distinto de body)', () => {
    for (const [tecla] of LOS_QUE_DESPLAZAN) {
        const e = entorno(); e.estado.activo = e.nodos.control;
        const r = e.pulsar(tecla);
        assert.deepEqual(r, { previene: false, scroll: null }, `${JSON.stringify(tecla)} con un control enfocado`);
    }
});

// Decisión, no omisión: con un modificador (Ctrl, Alt, Meta) el manejador se aparta entero. Protege Ctrl+K, los atajos Alt de
// keytips y Ctrl+RePág/AvPág (cambio de pestaña); el coste es que Ctrl+Inicio/Fin (y Cmd+↑/↓ en macOS) no desplazan con el foco en body.
test('Ctrl, Alt o Meta con cualquier tecla de desplazamiento no se atienden (atajos de la aplicación y del navegador)', () => {
    for (const modificador of ['ctrlKey', 'altKey', 'metaKey']) {
        for (const [tecla] of LOS_QUE_DESPLAZAN) {
            const r = entorno().pulsar(tecla, { [modificador]: true });
            assert.deepEqual(r, { previene: false, scroll: null }, `${modificador} + ${JSON.stringify(tecla)}`);
        }
    }
});

test('Mayús con flechas, Inicio, Fin, RePág y AvPág extiende la selección: no se atiende', () => {
    for (const [tecla] of LOS_QUE_DESPLAZAN.filter(([t]) => t !== ' ')) {
        const r = entorno().pulsar(tecla, { shiftKey: true });
        assert.deepEqual(r, { previene: false, scroll: null }, `Mayús + ${JSON.stringify(tecla)}`);
    }
});

test('quien ya atendió la tecla (defaultPrevented) gana', () => {
    for (const [tecla] of LOS_QUE_DESPLAZAN) {
        const r = entorno().pulsar(tecla, { defaultPrevented: true });
        assert.deepEqual(r, { previene: false, scroll: null }, `${JSON.stringify(tecla)} ya atendida`);
    }
});

test('con un modal abierto, cualquiera de los tres tipos, el teclado es del modal', () => {
    const modales = ['[role="dialog"][aria-modal="true"]', '[role="alertdialog"][aria-modal="true"]', 'dialog:modal'];
    for (const modal of modales) {
        const e = entorno(); e.estado.modal = modal;
        assert.deepEqual(e.pulsar('PageDown'), { previene: false, scroll: null }, modal);
    }
    const sin = entorno();
    assert.equal(sin.pulsar('PageDown').previene, true, 'sin modal abierto sí desplaza');
});

test('un clic en otro scroller que desborda deja las teclas a ese scroller; uno que no desborda o que no es scroller, no', () => {
    const casos = [
        ['menu', false], ['panel', false], ['menuHijo', false],
        ['texto', true], ['contenido', true], ['ajustado', true], ['oculto', true], ['desconectado', true], ['noElemento', true],
    ];
    for (const [nombre, previene] of casos) {
        const e = entorno();
        e.clic(e.nodos[nombre]);
        assert.equal(e.pulsar('PageDown').previene, previene, `clic previo en «${nombre}»`);
    }
});

test('un clic previo caduca cuando el foco entra en algo que no es body (focusin) o con Tab', () => {
    const a = entorno();
    a.clic(a.nodos.menu);
    assert.equal(a.pulsar('PageDown').previene, false, 'premisa: el clic en el menú gana');
    a.enfocar(a.nodos.control);
    a.estado.activo = a.body; // el foco vuelve a body sin otro clic
    assert.equal(a.pulsar('PageDown').previene, true, 'tras focusin en un control el clic viejo ya no cuenta');

    const b = entorno();
    b.clic(b.nodos.menu);
    assert.equal(b.pulsar('Tab').previene, false);
    assert.equal(b.pulsar('PageDown').previene, true, 'tras Tab el clic viejo ya no cuenta');

    const c = entorno();
    c.clic(c.nodos.menu);
    c.enfocar(c.body);
    assert.equal(c.pulsar('PageDown').previene, false, 'focusin en body no borra el clic');
});

test('en una pantalla sin #contenido-principal el manejador no hace nada ni lanza', () => {
    for (const [tecla] of LOS_QUE_DESPLAZAN) {
        const e = entorno(); e.estado.sinContenido = true;
        assert.deepEqual(e.pulsar(tecla), { previene: false, scroll: null }, JSON.stringify(tecla));
    }
});

test('focusin en documentElement tampoco borra el clic previo (el foco sigue sin estar en un control)', () => {
    const e = entorno();
    e.clic(e.nodos.menu);
    e.enfocar(e.raiz);
    e.estado.activo = e.body;
    assert.equal(e.pulsar('PageDown').previene, false);
});

test('Tab, Esc, flechas laterales, 1, 2 y Ctrl+K no se atienden nunca', () => {
    for (const [tecla, extra] of [['Tab', {}], ['Escape', {}], ['ArrowRight', {}], ['ArrowLeft', {}], ['1', {}], ['2', {}], ['k', { ctrlKey: true }], ['K', { ctrlKey: true }]]) {
        assert.deepEqual(entorno().pulsar(tecla, extra), { previene: false, scroll: null }, `${tecla} ${JSON.stringify(extra)}`);
    }
});
