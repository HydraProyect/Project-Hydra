// Revisión IA: preferencias de presentación en este navegador (el avance automático al terminar una revisión, D-49).
// Solo presentación: ningún dato de negocio pasa por aquí. localStorage puede estar vacío o bloqueado; cada acceso lo tolera.

export function leer(clave) {
    try {
        return window.localStorage.getItem(clave);
    } catch {
        return null;
    }
}

export function guardar(clave, valor) {
    try {
        window.localStorage.setItem(clave, valor);
        return true;
    } catch {
        return false;
    }
}

// Desplegable de pendientes (listbox de opciones): flechas arriba y abajo, Inicio y Fin recorren las opciones, y al abrirlo
// el foco va a la elegida. Solo teclado; elegir y cerrar siguen en el componente. Los filtros y el pie quedan fuera del listbox.
export function instalarTecladoOpciones(panel) {
    if (!panel || panel.dataset.tecladoOpciones === '1') return;
    panel.dataset.tecladoOpciones = '1';
    panel.addEventListener('keydown', e => {
        if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(e.key)) return;
        if (!e.target.closest || !e.target.closest('[role="listbox"]')) return;
        const opciones = Array.from(panel.querySelectorAll('[role="option"]'));
        if (opciones.length === 0) return;
        const actual = opciones.indexOf(document.activeElement);
        let destino = actual;
        if (e.key === 'ArrowDown') destino = Math.min(opciones.length - 1, actual + 1);
        else if (e.key === 'ArrowUp') destino = Math.max(0, actual < 0 ? 0 : actual - 1);
        else if (e.key === 'Home') destino = 0;
        else destino = opciones.length - 1;
        e.preventDefault();
        opciones[destino].focus();
    });
    const activa = panel.querySelector('[role="option"][aria-selected="true"]') ?? panel.querySelector('[role="option"]');
    if (activa) activa.focus();
}