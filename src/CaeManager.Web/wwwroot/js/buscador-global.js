// Atajo de teclado global (Ctrl/Cmd+K) para el buscador global — ver
// UX_PATTERNS.md, "Buscar". Blazor no puede capturar keydown a nivel de
// document sin interop porque el foco puede estar en cualquier elemento.
export function registrarAtajoBuscador(dotNetRef) {
    const manejador = (evento) => {
        const esAtajo = (evento.ctrlKey || evento.metaKey) && evento.key.toLowerCase() === 'k';
        if (!esAtajo) return;

        evento.preventDefault();
        dotNetRef.invokeMethodAsync('AbrirDesdeJs');
    };

    document.addEventListener('keydown', manejador);

    return {
        dispose: () => document.removeEventListener('keydown', manejador)
    };
}

export function enfocarElemento(elemento) {
    elemento?.focus();
}

// Tab dentro del input de búsqueda salta de grupo (Entidades → Ir a →
// Acciones, Parte XVI PROMPT 05) en vez de sacar el foco del panel — hace
// falta prevenir el Tab por defecto ANTES del round-trip a Blazor Server
// (evento síncrono en el navegador), no se puede decidir desde el propio
// método de C# que atiende el evento.
export function registrarSaltoDeGrupo(elemento, dotNetRef) {
    const manejador = (evento) => {
        if (evento.key !== 'Tab') return;

        evento.preventDefault();
        dotNetRef.invokeMethodAsync('SaltarGrupoDesdeJs', evento.shiftKey);
    };

    elemento.addEventListener('keydown', manejador);

    return {
        dispose: () => elemento.removeEventListener('keydown', manejador)
    };
}
