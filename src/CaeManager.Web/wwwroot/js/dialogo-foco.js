// Focus trap para overlays modales (Modal/Drawer, ver Modal.razor/Drawer.razor).
// Escape ya se maneja en C# (@onkeydown) — esto cubre lo que Blazor no puede
// sin interop: recorrer el DOM para encontrar los elementos enfocables del
// diálogo y devolver el foco a quien lo tenía al cerrarse (document.activeElement
// no tiene equivalente en C#). Hallazgo P0-8 de docs/business/MATURITY_REVIEW.md:
// sin esto, Tab se escapa del diálogo hacia la página de debajo y el foco se
// pierde al cerrar.
const SELECTOR_ENFOCABLE = 'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])';

export function atraparFoco(contenedor) {
    const elementoPrevio = document.activeElement;

    const enfocables = () => Array.from(contenedor.querySelectorAll(SELECTOR_ENFOCABLE))
        .filter(el => el.offsetParent !== null);

    // El primer elemento enfocable real (p. ej. el primer campo del
    // formulario) si existe; si el diálogo no tiene ninguno (solo texto y el
    // botón de cerrar, que sí cuenta), cae al propio contenedor gracias al
    // tabindex="-1" que ya trae el div del diálogo.
    (enfocables()[0] ?? contenedor).focus();

    function manejarTab(evento) {
        if (evento.key !== 'Tab') return;

        const items = enfocables();
        if (items.length === 0) return;

        const primero = items[0];
        const ultimo = items[items.length - 1];

        if (evento.shiftKey && document.activeElement === primero) {
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
            if (elementoPrevio instanceof HTMLElement && document.contains(elementoPrevio))
                elementoPrevio.focus();
        }
    };
}
