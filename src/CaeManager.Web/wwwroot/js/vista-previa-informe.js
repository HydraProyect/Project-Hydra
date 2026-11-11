// Vista previa de Reportes: la hoja vive en un panel de altura acotada con su
// propio desplazamiento vertical. Este módulo avisa a la página de si la hoja
// desborda (para activar «Ir al principio» e «Ir al final») y desplaza el panel.
let observador = null;

// Observa el panel y su contenido: el desbordamiento cambia al regenerar la
// vista previa (otras filas), al redimensionar la ventana y al cambiar de tema o
// de zoom, y ninguno de esos casos repinta la página.
export function observarDesbordamiento(panel, referencia) {
    desconectar();
    if (!panel) return;

    let ultimo = null;
    const avisar = () => {
        const desborda = panel.scrollHeight > panel.clientHeight + 1;
        if (desborda === ultimo) return;
        ultimo = desborda;
        referencia.invokeMethodAsync('CambiarDesbordamientoVistaPrevia', desborda).catch(() => { });
    };

    observador = new ResizeObserver(avisar);
    observador.observe(panel);
    for (const hijo of panel.children) observador.observe(hijo);
    avisar();
}

export function desconectar() {
    observador?.disconnect();
    observador = null;
}

// Sin animación para quien pide movimiento reducido; el resto, desplazamiento suave.
export function desplazar(panel, destino) {
    if (!panel) return;
    const reducido = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
    panel.scrollTo({
        top: destino === 'final' ? panel.scrollHeight : 0,
        behavior: reducido ? 'auto' : 'smooth'
    });
}
