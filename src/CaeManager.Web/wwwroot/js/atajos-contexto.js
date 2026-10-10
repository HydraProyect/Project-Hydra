// Modal, Drawer y la paleta declaran su modalidad en el DOM. Se comprueba
// aunque el foco todavía esté fuera: la apertura y el interop no son atómicos.
export function hayDialogoModalAbierto() {
    return Array.from(document.querySelectorAll(
        '[role="dialog"][aria-modal="true"], [role="alertdialog"][aria-modal="true"], dialog:modal'
    )).some(elemento => elemento.getClientRects().length > 0 &&
        getComputedStyle(elemento).visibility === 'visible');
}

// Cuántas listas con atajos propios (AtajosListaTeclado → atajos-lista.js) hay montadas. Los
// atajos de ficha 360 (atajos-ficha.js) ceden j/k/e/f mientras haya alguna.
export const atajosDeLista = { activos: 0 };
