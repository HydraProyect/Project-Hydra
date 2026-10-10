// Atajos de teclado de las fichas 360 (Tanda 2 de FICHAS-360): j/k recorren las filas de la
// lista que se ve, «e» pulsa el botón de la fila enfocada, «f» lleva al buscador de la lista
// y 1–9 cambian de pestaña.
//
// Todo se resuelve en el DOM, sin pasar por C#: la ficha se reconoce por la marca
// data-atajos-ficha de CuerpoConLateral, las filas por la pieza compartida (FilaRelacion) o por
// la fila de documento de Trabajador, y cada tecla acaba en el .click() o el .focus() del
// control que el usuario ya tiene delante. Por eso no hay nada que conceder por rol: si el
// rol Consulta no ve el botón de la fila, «e» no encuentra qué pulsar.
//
// Lo registra atajos-globales.js (una vez, en MainLayout): ninguna ficha monta un componente
// de interop propio.
import { hayDialogoModalAbierto, atajosDeLista } from './atajos-contexto.js';

// Debe coincidir con CatalogoAtajos.Ficha (CatalogoAtajosSincronizadoConJsTests). Las cifras
// 1–9 van aparte: no son una lista de teclas sino un rango.
const TECLAS_FICHA = ['j', 'k', 'e', 'f'];

// «e» es también un destino de «g + letra» (g e → /empresas): mismo recuerdo del prefijo que
// atajos-lista.js. Debe coincidir con VENTANA_PREFIJO_MS de atajos-globales.js.
const VENTANA_PREFIJO_GLOBAL_MS = 900;

const SELECTOR_FICHA = '[data-atajos-ficha]';
const SELECTOR_COLUMNA = '.cuerpo-con-lateral-principal';
const SELECTOR_LATERAL = '.cuerpo-con-lateral-lateral';
const SELECTOR_FILA = '[data-pieza="fila"], .fila-documento-requerido';
const ATRIBUTO_FILA_ENFOCADA = 'data-fila-enfocada';

// El botón de la fila: la ranura Acciones de FilaRelacion, el enlace de acción de las filas de
// documento, el de la subfila por Centro de Tipo de documento, o lo que una ficha marque a mano. El botón 360 no cuenta: abre el panel de la
// entidad, que no es la acción de la fila.
const SELECTOR_ACCION_DE_FILA = '.fila-relacion-acciones button, .fila-relacion-acciones a[href], ' +
    '.accion-fila-enlace, .tipo360-subfila-accion button, [data-accion-fila]';
const SELECTOR_NO_ES_ACCION = '.boton-360';

const SELECTOR_BUSCADOR = '[data-buscador-ficha], [data-filtro-pantalla], input[type="search"]';
const SELECTOR_ACCIONES_CABECERA = '.cabecera-identidad-acciones, .acciones-cabecera';
const SELECTOR_SUPERFICIE_AJENA = '[role="dialog"], [role="alertdialog"], [role="menu"], [role="listbox"]';

const TIPOS_INPUT_NO_TEXTO = new Set([
    'checkbox', 'radio', 'button', 'submit', 'reset', 'range', 'color', 'file', 'image'
]);

// Una fila «display: contents» (las de documento de Trabajador 360: sus celdas entran en la
// rejilla del padre) no tiene caja propia; la que se mide es la de su primera celda.
function cajaDe(elemento) {
    if (elemento.getClientRects().length > 0) return elemento;
    return getComputedStyle(elemento).display === 'contents' ? elemento.firstElementChild : null;
}

function esVisible(elemento) {
    const caja = cajaDe(elemento);
    return !!caja && caja.getClientRects().length > 0 && getComputedStyle(caja).visibility === 'visible';
}

function estaHabilitado(elemento) {
    return !elemento.disabled && elemento.getAttribute('aria-disabled') !== 'true';
}

function fichaVisible() {
    return Array.from(document.querySelectorAll(SELECTOR_FICHA)).find(esVisible) ?? null;
}

// Las pestañas de la ficha: la primera lista de pestañas de la página. Según la ficha, envuelve
// al cuerpo (Vehículo, Proyecto), lo precede como hermana (Subcontrata) o vive en su columna
// principal (las demás), así que no se busca por parentesco. Las de un panel, un diálogo o el
// lateral no cuentan, ni las anidadas en una pestaña (van después en el documento).
function pestanasDeFicha() {
    if (!fichaVisible()) return [];
    const lista = Array.from(document.querySelectorAll('[role="tablist"]')).find(candidata =>
        esVisible(candidata) &&
        !candidata.closest(SELECTOR_SUPERFICIE_AJENA + ', ' + SELECTOR_LATERAL));
    return lista ? Array.from(lista.querySelectorAll('[role="tab"]')) : [];
}

// «1»…«9» para las nueve primeras pestañas de la ficha; '' para cualquier otro elemento.
// keytips.js la usa como letra de la pestaña: la pista que enseña Alt es la tecla que funciona.
export function letraDePestanaDeFicha(elemento) {
    if (elemento.getAttribute?.('role') !== 'tab') return '';
    const indice = pestanasDeFicha().indexOf(elemento);
    return indice >= 0 && indice < 9 ? String(indice + 1) : '';
}

function filasVisibles() {
    const columna = fichaVisible()?.querySelector(SELECTOR_COLUMNA);
    if (!columna) return [];
    return Array.from(columna.querySelectorAll(SELECTOR_FILA)).filter(esVisible);
}

// La fila marcada por j/k; si no hay ninguna, la que contiene el foco (se llegó con Tab).
function filaActual(filas) {
    const marcada = filas.find(fila => fila.hasAttribute(ATRIBUTO_FILA_ENFOCADA));
    if (marcada) return marcada;
    const activo = document.activeElement;
    if (!activo || activo === document.body) return null;
    // La más interior: una fila desplegada contiene las filas de su desplegable.
    return filas.filter(fila => fila.contains(activo)).pop() ?? null;
}

function desmarcar(salvo = null) {
    for (const fila of document.querySelectorAll('[' + ATRIBUTO_FILA_ENFOCADA + ']')) {
        if (fila !== salvo) fila.removeAttribute(ATRIBUTO_FILA_ENFOCADA);
    }
}

// La marca es un atributo que Blazor no pinta: sobrevive a sus repintados mientras la fila
// siga en el DOM. Además la fila recibe foco real (tabindex -1: focable por script, fuera del
// orden de Tab), que es lo que anuncia un lector de pantalla. Una fila sin caja propia no
// puede recibir foco: se queda con la marca y se suelta el foco de la fila anterior.
function marcar(fila, conFoco) {
    desmarcar(fila);
    fila.setAttribute(ATRIBUTO_FILA_ENFOCADA, '');
    if (!conFoco) return;
    if (cajaDe(fila) === fila) {
        if (!fila.hasAttribute('tabindex')) fila.tabIndex = -1;
        fila.focus({ preventScroll: true });
    } else if (document.activeElement?.closest?.(SELECTOR_FILA)) {
        document.activeElement.blur();
    }
    cajaDe(fila)?.scrollIntoView({ block: 'nearest' });
}

function mover(delta) {
    const filas = filasVisibles();
    if (filas.length === 0) return false;
    const actual = filas.indexOf(filaActual(filas));
    // Sin fila enfocada, tanto «j» como «k» empiezan por la primera.
    const destino = actual < 0 ? 0 : Math.max(0, Math.min(filas.length - 1, actual + delta));
    marcar(filas[destino], true);
    return true;
}

function accionDeFila(fila) {
    return Array.from(fila.querySelectorAll(SELECTOR_ACCION_DE_FILA)).find(control =>
        !control.matches(SELECTOR_NO_ES_ACCION) &&
        // No la de una fila anidada en el desplegable de esta.
        control.closest(SELECTOR_FILA) === fila &&
        esVisible(control) && estaHabilitado(control)) ?? null;
}

function accionDeFilaActual() {
    const filas = filasVisibles();
    const fila = filaActual(filas);
    return fila ? accionDeFila(fila) : null;
}

function buscadorDeFicha() {
    const ficha = fichaVisible();
    if (!ficha) return null;
    return Array.from(ficha.querySelectorAll(SELECTOR_BUSCADOR))
        .find(campo => !campo.disabled && esVisible(campo)) ?? null;
}

// Lo que keytips.js etiqueta en una ficha además de lo declarado con data-keytip: el botón de
// la fila enfocada (E, la misma tecla que lo pulsa sin el modo) y las acciones de la cabecera
// (letra deducida de su rótulo). «yaDeclarados» son los elementos que ya llevan letra propia.
export function objetivosDeFicha(yaDeclarados) {
    if (!fichaVisible()) return [];
    const lista = [];

    const accion = accionDeFilaActual();
    if (accion && !yaDeclarados.has(accion)) {
        lista.push({ elemento: accion, letra: 'E' });
    }

    for (const zona of document.querySelectorAll(SELECTOR_ACCIONES_CABECERA)) {
        if (zona.closest(SELECTOR_SUPERFICIE_AJENA)) continue;
        for (const control of zona.querySelectorAll('button, a[href]')) {
            if (yaDeclarados.has(control) || control.hasAttribute('data-keytip')) continue;
            if (control.closest('[data-keytip-contenedor], [role="menu"]')) continue;
            if (!esVisible(control) || !estaHabilitado(control)) continue;
            lista.push({ elemento: control, letra: '' });
        }
    }
    return lista;
}

function enCampoEditable(activo) {
    return !!activo && (
        activo.tagName === 'TEXTAREA' || activo.tagName === 'SELECT' || activo.isContentEditable ||
        (activo.tagName === 'INPUT' && !TIPOS_INPUT_NO_TEXTO.has(activo.type)));
}

export function registrarAtajosFicha() {
    let ultimaGSuelta = 0;

    const manejador = (evento) => {
        if (evento.ctrlKey || evento.metaKey || evento.altKey) return;

        const trasPrefijoGlobal = Date.now() - ultimaGSuelta < VENTANA_PREFIJO_GLOBAL_MS;
        ultimaGSuelta = 0;

        const esCifra = /^[1-9]$/.test(evento.key);
        const esPrefijoGlobal = evento.key === 'g';
        if (!esCifra && !esPrefijoGlobal && !TECLAS_FICHA.includes(evento.key)) return;
        if (evento.defaultPrevented || evento.isComposing) return;
        if (hayDialogoModalAbierto()) return;

        const activo = document.activeElement;
        if (enCampoEditable(activo)) return;

        if (esPrefijoGlobal) {
            if (!trasPrefijoGlobal && activo?.tagName !== 'INPUT') ultimaGSuelta = Date.now();
            return;
        }
        if (evento.key === 'e' && trasPrefijoGlobal) return;

        // Con el foco en un panel, un menú o un desplegable abiertos, el teclado es suyo.
        if (activo?.closest?.(SELECTOR_SUPERFICIE_AJENA)) return;
        if (document.querySelector('[aria-haspopup="menu"][aria-expanded="true"]')) return;
        if (!fichaVisible()) return;

        if (esCifra) {
            const pestana = pestanasDeFicha()[Number(evento.key) - 1];
            if (!pestana || !estaHabilitado(pestana)) return;
            evento.preventDefault();
            if (pestana.getAttribute('aria-selected') !== 'true') {
                desmarcar();
                pestana.click();
            }
            return;
        }

        // Una lista con sus propios atajos (AtajosListaTeclado) incrustada en la ficha manda
        // sobre j/k/e/f: no se reparten dos veces.
        if (atajosDeLista.activos > 0) return;

        if (evento.key === 'j' || evento.key === 'k') {
            if (mover(evento.key === 'j' ? 1 : -1)) evento.preventDefault();
            return;
        }

        if (evento.key === 'e') {
            const accion = accionDeFilaActual();
            if (!accion) return;
            evento.preventDefault();
            accion.click();
            return;
        }

        const buscador = buscadorDeFicha();
        if (!buscador) return;
        // preventDefault: la «f» no debe acabar escrita dentro del campo recién enfocado.
        evento.preventDefault();
        buscador.focus();
        buscador.select?.();
    };

    // Con una fila ya marcada, el foco que entra en otra fila (Tab, clic) se lleva la marca:
    // «e» y la señal visible hablan siempre de la misma fila.
    const alEntrarElFoco = (evento) => {
        if (!document.querySelector('[' + ATRIBUTO_FILA_ENFOCADA + ']')) return;
        const fila = evento.target?.closest?.(SELECTOR_FILA);
        if (!fila || fila.hasAttribute(ATRIBUTO_FILA_ENFOCADA)) return;
        if (!fila.closest(SELECTOR_FICHA + ' ' + SELECTOR_COLUMNA)) return;
        marcar(fila, false);
    };

    document.addEventListener('keydown', manejador);
    document.addEventListener('focusin', alEntrarElFoco);

    return {
        dispose: () => {
            document.removeEventListener('keydown', manejador);
            document.removeEventListener('focusin', alEntrarElFoco);
        }
    };
}
