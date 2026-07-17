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
