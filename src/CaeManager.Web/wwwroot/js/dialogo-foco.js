// Focus trap para overlays modales (Modal/Drawer, ver Modal.razor/Drawer.razor).
// Escape ya se maneja en C# (@onkeydown) — esto cubre lo que Blazor no puede
// sin interop: recorrer el DOM para encontrar los elementos enfocables del
// diálogo y devolver el foco a quien lo tenía al cerrarse (document.activeElement
// no tiene equivalente en C#). Hallazgo P0-8 de Project-Hydra-Negocio/MATURITY_REVIEW.md:
// sin esto, Tab se escapa del diálogo hacia la página de debajo y el foco se
// pierde al cerrar.
const SELECTOR_ENFOCABLE = 'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])';

// Últimos elementos que recibieron el foco en el documento, del más antiguo al más reciente. La trampa se activa en
// OnAfterRenderAsync, tras un viaje de ida y vuelta al servidor, y para entonces el foco puede estar ya en un campo
// del diálogo (el usuario, o Playwright, que rellena un campo de texto en dos pasos: enfocar y luego insertar):
// document.activeElement ya no es quien abrió el diálogo, y hay que buscarlo aquí (#2194). Se guarda en el propio
// document con una clave global (Symbol.for) para que lo compartan la copia de este módulo que App.razor carga al
// arrancar, y que empieza a escuchar desde el primer foco de la página, y la que importen los componentes si el
// navegador las tratara como dos módulos distintos. Las referencias son fuertes, pero el tope las acota a unas pocas.
const CLAVE_HISTORIAL_FOCO = Symbol.for('talveg.dialogoFoco.historial');
const TOPE_HISTORIAL_FOCO = 20;

export function rastrearFoco(documento = globalThis.document) {
    if (!documento || documento[CLAVE_HISTORIAL_FOCO]) return;
    const historial = [];
    documento[CLAVE_HISTORIAL_FOCO] = historial;
    const anotar = (elemento) => {
        historial.push(elemento);
        if (historial.length > TOPE_HISTORIAL_FOCO) historial.shift();
    };
    documento.addEventListener('focusin', (evento) => anotar(evento.target), true);
    // Un clic en algo no enfocable (una fila sin tabindex, el fondo) deja el foco en el body sin focusin: se anota el
    // body para no devolver luego el foco a un elemento de fuera mucho más antiguo que el que abrió el diálogo.
    documento.addEventListener('focusout', (evento) => { if (!evento.relatedTarget) anotar(documento.body); }, true);
}

rastrearFoco();

// Contenedores de las trampas activas, de la más antigua a la más reciente, compartidos entre copias del módulo por la
// misma razón que el historial. Una trampa entra al activarse y sale al liberarse; la de un diálogo que desapareció
// sin liberarla se ignora porque su contenedor ya no está en el documento.
const CLAVE_TRAMPAS_ACTIVAS = Symbol.for('talveg.dialogoFoco.trampas');

function trampasActivas() {
    return (document[CLAVE_TRAMPAS_ACTIVAS] ??= []);
}

// El diálogo modal que sigue abierto debajo del que se abre ahora: la trampa activa más reciente cuyo contenedor es
// modal (aria-modal="true") y sigue en el documento. Con varios diálogos anidados es el inmediatamente exterior, el
// único cuyo contenido queda delante de los demás; un elemento de fuera de él queda detrás de un diálogo modal abierto.
// Un panel no modal con trampa (el Context Workspace, role="complementary") no cuenta: lo de fuera de él sigue a la vista.
function dialogoExteriorAbierto(contenedor) {
    const pila = trampasActivas();
    for (let i = pila.length - 1; i >= 0; i--) {
        const exterior = pila[i];
        if (exterior !== contenedor && exterior.getAttribute?.('aria-modal') === 'true' && document.contains(exterior))
            return exterior;
    }
    return null;
}

// Los elementos enfocables y visibles de un contenedor, en orden del documento.
function enfocablesDe(contenedor) {
    return Array.from(contenedor.querySelectorAll(SELECTOR_ENFOCABLE)).filter(el => el.offsetParent !== null);
}

// Quien tenía el foco antes de que entrara en el diálogo: el elemento activo si sigue fuera; si ya está dentro (o es
// el propio contenedor), el último elemento de fuera que tuvo el foco y sigue en el documento (o el body, que como
// antes no recibe el foco de vuelta). En un diálogo abierto desde otro, ese es el botón del diálogo de fuera que lo abrió,
// y la búsqueda no sale de ese diálogo exterior: si el abridor ya no existe, el foco vuelve a su contenedor, no a un
// elemento que quedó detrás de él. Lo mismo si el elemento activo está fuera del diálogo pero tampoco dentro del exterior
// (el abridor se retiró y el foco cayó en el body, o pasó a algo de detrás del diálogo exterior): no vale como previo.
function elementoPrevioAlDialogo(contenedor) {
    const activo = document.activeElement;
    const exterior = dialogoExteriorAbierto(contenedor);
    if (!contenedor.contains(activo) && (!exterior || exterior.contains(activo))) return activo;
    const historial = document[CLAVE_HISTORIAL_FOCO] ?? [];
    for (let i = historial.length - 1; i >= 0; i--) {
        const candidato = historial[i];
        if (!candidato || contenedor.contains(candidato) || !document.contains(candidato)) continue;
        if (exterior && !exterior.contains(candidato)) continue;
        return candidato;
    }
    return exterior;
}

export function atraparFoco(contenedor, devolverFila = false) {
    rastrearFoco();
    const elementoPrevio = elementoPrevioAlDialogo(contenedor);
    // Fuera de la pila los contenedores que ya no están en el documento: un diálogo que desapareció sin liberar su
    // trampa (el circuito se cortó antes del dispose) no debe retener su nodo hasta que se recargue la página.
    const pila = trampasActivas();
    for (let i = pila.length - 1; i >= 0; i--) if (!document.contains(pila[i])) pila.splice(i, 1);
    pila.push(contenedor);

    const enfocables = () => enfocablesDe(contenedor);

    // El primer elemento enfocable real (p. ej. el primer campo del
    // formulario) si existe; si el diálogo no tiene ninguno (solo texto y el
    // botón de cerrar, que sí cuenta), cae al propio contenedor gracias al
    // tabindex="-1" que ya trae el div del diálogo.
    //
    // Solo si el foco aún no está en un elemento del diálogo: si el usuario (o
    // Playwright) ya está escribiendo en un campo, moverle el foco al primer
    // enfocable le quitaría el campo a media escritura y el texto se perdería
    // (#2194). El propio contenedor no cuenta como «dentro»: desde él,
    // Mayús+Tab saldría del diálogo.
    const activo = document.activeElement;
    if (activo === contenedor || !contenedor.contains(activo))
        (enfocables()[0] ?? contenedor).focus();

    function manejarTab(evento) {
        // Un manejador interno que ya gestionó el Tab (p. ej. el salto de grupo del buscador global) manda.
        if (evento.key !== 'Tab' || evento.defaultPrevented) return;

        const items = enfocables();
        // Sin nada enfocable (solo texto, o todos los controles deshabilitados) el foco se queda en el contenedor: si se
        // dejara pasar el Tab, saldría a la página de debajo del diálogo modal.
        if (items.length === 0) {
            evento.preventDefault();
            if (document.activeElement !== contenedor) contenedor.focus();
            return;
        }

        const primero = items[0];
        const ultimo = items[items.length - 1];

        // El contenedor puede recibir el foco después (un clic en el fondo del
        // diálogo enfoca el div con tabindex="-1"): Mayús+Tab desde él también
        // va al último enfocable en vez de salir a la página de debajo.
        if (evento.shiftKey && (document.activeElement === primero || document.activeElement === contenedor)) {
            evento.preventDefault();
            ultimo.focus();
        } else if (!evento.shiftKey && document.activeElement === ultimo) {
            evento.preventDefault();
            primero.focus();
        }
    }

    contenedor.addEventListener('keydown', manejarTab);

    return {
        dispose: () => {
            contenedor.removeEventListener('keydown', manejarTab);
            const pila = trampasActivas();
            const posicion = pila.lastIndexOf(contenedor);
            if (posicion >= 0) pila.splice(posicion, 1);
            // Puede que el elemento que abrió el diálogo ya no exista (p. ej.
            // se recargó la lista que contenía el botón) — comprobar que
            // sigue en el documento antes de devolverle el foco.
            const fila = devolverFila && document.querySelector('.fila-enfocada, .panel-resolver-item-enfocado');
            if (devolverFila && document.querySelector('[role="dialog"][aria-modal="true"], [role="alertdialog"][aria-modal="true"]')) return;
            const previoVigente = elementoPrevio instanceof HTMLElement && document.contains(elementoPrevio) && elementoPrevio !== document.body;
            // La fila solo gana si el elemento que abrió el panel ya no existe (repintado) o es la propia fila.
            if (fila && (!previoVigente || fila.contains(elementoPrevio))) {
                if (!fila.hasAttribute('tabindex')) fila.tabIndex = -1;
                fila.focus({ preventScroll: true });
            } else if (elementoPrevio instanceof HTMLElement && document.contains(elementoPrevio)) {
                elementoPrevio.focus();
            } else {
                // El elemento previo desapareció mientras este diálogo seguía abierto (p. ej. se repintó el diálogo
                // exterior que contenía su abridor): el foco vuelve al diálogo modal exterior que sigue abierto, a su
                // primer enfocable o, si no tiene, a su contenedor, y no se queda detrás de él.
                const exterior = dialogoExteriorAbierto(contenedor);
                if (exterior) (enfocablesDe(exterior)[0] ?? exterior).focus();
            }
        }
    };
}
