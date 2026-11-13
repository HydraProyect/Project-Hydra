// Pruebas del módulo wwwroot/js/zona-soltar-archivo.js sin navegador: un DOM mínimo a mano.
// Lo que fijan: Ctrl+V funciona sin que la zona tenga el foco (oyente a nivel de documento
// mientras haya una zona montada), se ignora con el foco en un campo de texto, se desengancha
// al desmontar, y rechaza con motivo lo que no es un archivo admitido.
// Se ejecutan con `node --test` (las lanza ZonaSoltarArchivoJsTests desde dotnet test).
import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const aqui = dirname(fileURLToPath(import.meta.url));
const ruta = resolve(aqui, '../../../src/CaeManager.Web/wwwroot/js/zona-soltar-archivo.js');
const fuente = readFileSync(ruta, 'utf8');

class ElementoFalso {
    constructor(etiqueta, { type, dataset = {}, visible = true, clases = [], deshabilitado = false, editable = false } = {}) {
        this.nodeType = 1;
        this.tagName = etiqueta.toUpperCase();
        this.type = type;
        this.dataset = dataset;
        this.isConnected = true;
        this.isContentEditable = editable;
        this.disabled = deshabilitado;
        this.hijos = [];
        this.visible = visible;
        this._clases = new Set(clases);
        this._oyentes = new Map();
        this.clics = 0;
        this.cambios = 0;
        this.files = null;
        this.classList = {
            add: c => this._clases.add(c),
            remove: c => this._clases.delete(c),
            contains: c => this._clases.has(c),
        };
    }
    contains(nodo) { return nodo === this || this.hijos.some(h => h.contains(nodo)); }
    getClientRects() { return this.visible ? [{}] : []; }
    addEventListener(tipo, f) { (this._oyentes.get(tipo) ?? this._oyentes.set(tipo, []).get(tipo)).push(f); }
    removeEventListener(tipo, f) { this._oyentes.set(tipo, (this._oyentes.get(tipo) ?? []).filter(x => x !== f)); }
    dispatchEvent(e) { if (e.type === 'change') this.cambios++; return true; }
    click() { this.clics++; }
    closest(selector) { return selector === '[data-zona-cambiar]' && this.dataset.zonaCambiar ? this : null; }
}

let oyentesDocumento;
let documentoFalso;

beforeEach(() => {
    oyentesDocumento = { paste: [] };
    documentoFalso = {
        addEventListener: (tipo, f) => { (oyentesDocumento[tipo] ??= []).push(f); },
        removeEventListener: (tipo, f) => { oyentesDocumento[tipo] = (oyentesDocumento[tipo] ?? []).filter(x => x !== f); },
        modalesAbiertos: [],
        querySelectorAll(selector) { return selector === '[aria-modal="true"]' ? this.modalesAbiertos : []; },
    };
    globalThis.document = documentoFalso;
    globalThis.Element = ElementoFalso;
    globalThis.DataTransfer = class {
        constructor() { this._f = []; this.items = { add: f => this._f.push(f) }; }
        get files() { return this._f; }
    };
});

// Un módulo nuevo por prueba: el registro de zonas y el oyente son estado de módulo.
let contador = 0;
async function cargarModulo() {
    const url = 'data:text/javascript;base64,' + Buffer.from(`${fuente}\n// prueba ${contador++}`).toString('base64');
    return import(url);
}

function montar(modulo, { accept = '', multiple = false, maxBytes, visible = true, deshabilitada = false } = {}) {
    const dataset = { accept, multiple: String(multiple) };
    if (maxBytes !== undefined) dataset.maxBytes = String(maxBytes);
    const zona = new ElementoFalso('div', { dataset, visible, clases: deshabilitada ? ['zona-soltar-archivo-deshabilitada'] : [] });
    const input = new ElementoFalso('input', { type: 'file' });
    zona.hijos.push(input);
    const llamadas = [];
    const dotNet = { invokeMethodAsync: (metodo, ...args) => { llamadas.push([metodo, ...args]); return Promise.resolve(); } };
    const instancia = modulo.inicializar(zona, input, dotNet);
    return { zona, input, llamadas, instancia };
}

function pegar(destino, archivos) {
    const evento = {
        type: 'paste', target: destino, defaultPrevented: false, prevented: false,
        clipboardData: { files: archivos },
        preventDefault() { this.prevented = true; this.defaultPrevented = true; },
    };
    for (const f of [...oyentesDocumento.paste]) f(evento);
    return evento;
}

const cuerpo = () => new ElementoFalso('body');
const archivo = (name, type, size = 100) => ({ name, type, size });

test('Ctrl+V entra sin que la zona haya tenido el foco: el oyente cuelga del documento', async () => {
    const m = await cargarModulo();
    const { input, llamadas } = montar(m, { accept: '.pdf,image/png' });

    assert.equal(oyentesDocumento.paste.length, 1, 'un oyente de paste en el documento nada más montar la zona');
    const evento = pegar(cuerpo(), [archivo('captura.png', 'image/png')]);

    assert.equal(input.files.length, 1);
    assert.equal(input.cambios, 1, 'dispara change: InputFile lanza OnChange como con el selector');
    assert.deepEqual(llamadas, [['AnunciarOrigen', 'pegado']]);
    assert.equal(evento.prevented, true);
});

test('con el foco en un campo de texto el pegado se ignora', async () => {
    const m = await cargarModulo();
    const { input, llamadas } = montar(m);

    for (const campo of [new ElementoFalso('textarea'), new ElementoFalso('input', { type: 'text' }),
        new ElementoFalso('input', { type: 'search' }), new ElementoFalso('select'), new ElementoFalso('div', { editable: true })]) {
        const evento = pegar(campo, [archivo('a.pdf', 'application/pdf')]);
        assert.equal(evento.prevented, false);
    }
    assert.equal(input.cambios, 0);
    assert.deepEqual(llamadas, []);
});

test('un input que no es de texto (botón, casilla) no se queda con el Ctrl+V', async () => {
    const m = await cargarModulo();
    const { input } = montar(m);

    pegar(new ElementoFalso('input', { type: 'checkbox' }), [archivo('a.pdf', 'application/pdf')]);
    pegar(new ElementoFalso('button'), [archivo('a.pdf', 'application/pdf')]);

    assert.equal(input.cambios, 2);
});

test('con el foco en el propio input de archivo de la zona sí se pega', async () => {
    const m = await cargarModulo();
    const { zona, input } = montar(m);

    pegar(zona.hijos[0], [archivo('a.pdf', 'application/pdf')]);

    assert.equal(input.cambios, 1);
});

test('sin archivo en el portapapeles avisa con el motivo y no toca el input', async () => {
    const m = await cargarModulo();
    const { input, llamadas } = montar(m);

    const evento = pegar(cuerpo(), []);

    assert.deepEqual(llamadas, [['RechazarEntrada', 'sin-archivo', null]]);
    assert.equal(input.cambios, 0);
    assert.equal(evento.prevented, false, 'no se secuestra un pegado de texto');
});

test('un tipo no admitido se rechaza con el nombre del archivo', async () => {
    const m = await cargarModulo();
    const { input, llamadas } = montar(m, { accept: '.pdf,.jpg,image/jpeg' });

    pegar(cuerpo(), [archivo('notas.txt', 'text/plain')]);

    assert.deepEqual(llamadas, [['RechazarEntrada', 'tipo', 'notas.txt']]);
    assert.equal(input.cambios, 0);
});

test('accept admite extensión, tipo exacto y comodín de familia', async () => {
    const m = await cargarModulo();
    const { input } = montar(m, { accept: '.PDF,image/*' });

    pegar(cuerpo(), [archivo('INFORME.pdf', '')]);
    pegar(cuerpo(), [archivo('sin-extension', 'image/webp')]);
    pegar(cuerpo(), [archivo('datos.csv', 'text/csv')]);

    assert.equal(input.cambios, 2);
});

test('sin accept se admite cualquier tipo', async () => {
    const m = await cargarModulo();
    const { input } = montar(m);

    pegar(cuerpo(), [archivo('lo-que-sea.xyz', '')]);

    assert.equal(input.cambios, 1);
});

test('un archivo por encima del límite se rechaza con el nombre; sin límite no se comprueba', async () => {
    const m = await cargarModulo();
    const con = montar(m, { maxBytes: 1000 });

    pegar(cuerpo(), [archivo('grande.pdf', 'application/pdf', 1001)]);

    assert.deepEqual(con.llamadas, [['RechazarEntrada', 'tamano', 'grande.pdf']]);
    assert.equal(con.input.cambios, 0);

    pegar(cuerpo(), [archivo('justo.pdf', 'application/pdf', 1000)]);
    assert.equal(con.input.cambios, 1, 'exactamente el límite entra');
    con.instancia.dispose();

    const sin = montar(m);
    pegar(cuerpo(), [archivo('enorme.pdf', 'application/pdf', 10 ** 12)]);
    assert.equal(sin.input.cambios, 1);
});

test('una zona sin multiple se queda con el primero de varios', async () => {
    const m = await cargarModulo();
    const { input } = montar(m, { multiple: false });

    pegar(cuerpo(), [archivo('a.pdf', 'application/pdf'), archivo('b.pdf', 'application/pdf')]);

    assert.deepEqual(input.files.map(f => f.name), ['a.pdf']);
});

test('una zona con multiple recibe todos', async () => {
    const m = await cargarModulo();
    const { input } = montar(m, { multiple: true });

    pegar(cuerpo(), [archivo('a.pdf', 'application/pdf'), archivo('b.pdf', 'application/pdf')]);

    assert.equal(input.files.length, 2);
});

test('con varias zonas responde la última montada y visible', async () => {
    const m = await cargarModulo();
    const pagina = montar(m);
    const drawer = montar(m);
    const oculta = montar(m, { visible: false });

    assert.equal(oyentesDocumento.paste.length, 1, 'un solo oyente de documento para todas las zonas');
    pegar(cuerpo(), [archivo('a.pdf', 'application/pdf')]);

    assert.equal(drawer.input.cambios, 1, 'el drawer, montado después, responde');
    assert.equal(pagina.input.cambios, 0);
    assert.equal(oculta.input.cambios, 0, 'una zona sin caja (oculta) no recibe nada');
});

test('una zona deshabilitada (cargando) no recibe el pegado', async () => {
    const m = await cargarModulo();
    const { input, llamadas } = montar(m, { deshabilitada: true });

    pegar(cuerpo(), [archivo('a.pdf', 'application/pdf')]);

    assert.equal(input.cambios, 0);
    assert.deepEqual(llamadas, []);
});

test('el foco dentro de una zona concreta decide cuál responde, aunque otra se montara después', async () => {
    const m = await cargarModulo();
    const primera = montar(m);
    const segunda = montar(m);

    pegar(primera.zona, [archivo('a.pdf', 'application/pdf')]);

    assert.equal(primera.input.cambios, 1);
    assert.equal(segunda.input.cambios, 0);
});

test('al desmontar la última zona el oyente de documento se suelta; con otra montada sigue', async () => {
    const m = await cargarModulo();
    const a = montar(m);
    const b = montar(m);

    b.instancia.dispose();
    assert.equal(oyentesDocumento.paste.length, 1, 'queda la zona a');
    pegar(cuerpo(), [archivo('x.pdf', 'application/pdf')]);
    assert.equal(a.input.cambios, 1);
    assert.equal(b.input.cambios, 0);

    a.instancia.dispose();
    assert.equal(oyentesDocumento.paste.length, 0, 'sin zonas no queda ningún oyente en el documento');
    pegar(cuerpo(), [archivo('y.pdf', 'application/pdf')]);
    assert.equal(a.input.cambios, 1);
});

test('volver a montar tras desmontar engancha de nuevo', async () => {
    const m = await cargarModulo();
    montar(m).instancia.dispose();

    const { input } = montar(m);
    pegar(cuerpo(), [archivo('a.pdf', 'application/pdf')]);

    assert.equal(oyentesDocumento.paste.length, 1);
    assert.equal(input.cambios, 1);
});

test('soltar un archivo lo entrega y anuncia el origen', async () => {
    const m = await cargarModulo();
    const { zona, input, llamadas } = montar(m);
    const drop = zona._oyentes.get('drop').at(-1);

    drop({ preventDefault() { }, stopPropagation() { }, dataTransfer: { files: [archivo('a.pdf', 'application/pdf')] } });

    assert.equal(input.cambios, 1);
    assert.deepEqual(llamadas, [['AnunciarOrigen', 'soltado']]);
});

test('«Cambiar» abre el selector con el clic del usuario', async () => {
    const m = await cargarModulo();
    const { zona, input } = montar(m);
    const clic = zona._oyentes.get('click').at(-1);

    clic({ target: new ElementoFalso('button', { dataset: { zonaCambiar: 'true' } }) });
    clic({ target: new ElementoFalso('button') });

    assert.equal(input.clics, 1, 'solo el botón marcado con data-zona-cambiar');
});

test('con un diálogo modal abierto, el pegado no sube el archivo a la zona de la página de detrás', async () => {
    const m = await cargarModulo();
    const pagina = montar(m);
    const modal = new ElementoFalso('div');
    documentoFalso.modalesAbiertos = [modal];

    pegar(cuerpo(), [archivo('a.pdf', 'application/pdf')]);
    assert.equal(pagina.input.cambios, 0, 'el modal abierto no tiene zona: no se pega en la de detrás');

    const dentro = montar(m);
    modal.hijos.push(dentro.zona);
    pegar(cuerpo(), [archivo('b.pdf', 'application/pdf')]);
    assert.equal(dentro.input.cambios, 1, 'la zona de dentro del modal sí');
    assert.equal(pagina.input.cambios, 0);
});

test('con un drawer y un diálogo de confirmación apilados, el pegado no llega a la zona del drawer', async () => {
    const m = await cargarModulo();
    const drawer = new ElementoFalso('div');
    const dialogo = new ElementoFalso('div');
    const zonaDelDrawer = montar(m);
    drawer.hijos.push(zonaDelDrawer.zona);
    // El diálogo se pinta después del drawer: el último del DOM queda encima.
    documentoFalso.modalesAbiertos = [drawer, dialogo];

    pegar(cuerpo(), [archivo('a.pdf', 'application/pdf')]);

    assert.equal(zonaDelDrawer.input.cambios, 0, 'la confirmación abierta bloquea el pegado en el drawer de detrás');
});

test('si la última zona está ocupada no se cae a otra zona anterior', async () => {
    const m = await cargarModulo();
    const pagina = montar(m);
    const drawer = montar(m, { deshabilitada: true });

    pegar(cuerpo(), [archivo('a.pdf', 'application/pdf')]);

    assert.equal(drawer.input.cambios, 0);
    assert.equal(pagina.input.cambios, 0, 'el archivo no acaba en la zona de detrás');
});

test('tras desmontar, la zona ya no avisa a .NET', async () => {
    const m = await cargarModulo();
    const { zona, instancia, llamadas } = montar(m);
    const drop = zona._oyentes.get('drop').at(-1);

    instancia.dispose();
    drop({ preventDefault() { }, stopPropagation() { }, dataTransfer: { files: [archivo('a.pdf', 'application/pdf')] } });

    assert.deepEqual(llamadas, []);
});
