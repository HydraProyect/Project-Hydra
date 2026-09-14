// Copia texto al portapapeles. navigator.clipboard.writeText requiere un
// contexto seguro (HTTPS o localhost). Si falta o rechaza, se intenta el
// respaldo con un textarea temporal, que siempre se retira del documento.
export async function copiarAlPortapapeles(texto) {
    if (navigator.clipboard && window.isSecureContext) {
        try {
            await navigator.clipboard.writeText(texto);
            return;
        } catch {
            // Un permiso denegado también puede admitir el método de respaldo.
        }
    }

    const elementoPrevio = document.activeElement;
    const seleccionCampo = typeof elementoPrevio?.selectionStart === 'number'
        ? [elementoPrevio.selectionStart, elementoPrevio.selectionEnd, elementoPrevio.selectionDirection] : null;
    const seleccion = window.getSelection();
    const rangos = seleccion ? Array.from({ length: seleccion.rangeCount }, (_, i) => seleccion.getRangeAt(i).cloneRange()) : [];
    const areaTexto = document.createElement('textarea');
    areaTexto.value = texto;
    areaTexto.style.position = 'fixed';
    areaTexto.style.opacity = '0';
    // Un diálogo nativo hace inerte el resto del documento. El respaldo
    // debe estar dentro de la superficie donde el usuario está copiando.
    const contenedor = elementoPrevio?.closest('[role="dialog"][aria-modal="true"], dialog:modal') ?? document.body;
    try {
        contenedor.appendChild(areaTexto);
        areaTexto.focus({ preventScroll: true });
        areaTexto.select();
        if (!document.execCommand('copy'))
            throw new Error('El navegador denegó la copia al portapapeles.');
    } finally {
        areaTexto.remove();
        if (elementoPrevio instanceof HTMLElement && elementoPrevio.isConnected) {
            elementoPrevio.focus({ preventScroll: true });
            if (seleccionCampo) elementoPrevio.setSelectionRange(...seleccionCampo);
        }
        // La selección del documento puede cancelar la selección de un input.
        // Son dos mecanismos alternativos: restauramos solo el que estaba activo.
        if (!seleccionCampo && seleccion && rangos.length > 0) {
            seleccion.removeAllRanges();
            for (const rango of rangos) seleccion.addRange(rango);
        }
    }
}

// Solo selecciona el campo visible que el componente de fechas ha ofrecido
// para copiar a mano. No materializa los valores de BotonCopiar/ValorAsync.
export function seleccionarFechaParaCopiaManual(campo) {
    campo.focus({ preventScroll: true });
    campo.select();
}
