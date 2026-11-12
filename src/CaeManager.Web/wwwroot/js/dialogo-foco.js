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

// Quien tenía el foco antes de que entrara en el diálogo: el elemento activo si sigue fuera; si ya está dentro (o es
// el propio contenedor), el último elemento de fuera que tuvo el foco y sigue en el documento (o el body, que como
// antes no recibe el foco de vuelta). En un diálogo abierto desde otro, ese es el botón del diálogo de fuera que lo abrió.
function elementoPrevioAlDialogo(contenedor) {
    const activo = document.activeElement;
    if (!contenedor.contains(activo)) return activo;
    const historial = document[CLAVE_HISTORIAL_FOCO] ?? [];
    for (let i = historial.length - 1; i >= 0; i--) {
        const candidato = historial[i];
        if (candidato && !contenedor.contains(candidato) && document.contains(candidato)) return candidato;
    }
    return null;
}

export function atraparFoco(contenedor, devolverFila = false) {
    rastrearFoco();
    const elementoPrevio = elementoPrevioAlDialogo(contenedor);

    const enfocables = () => Array.from(contenedor.querySelectorAll(SELECTOR_ENFOCABLE))
        .filter(el => el.offsetParent !== null);

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
        if (items.length === 0) return;

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
            } else if (elementoPrevio instanceof HTMLElement && document.contains(elementoPrevio))
                elementoPrevio.focus();
        }
    };
}
