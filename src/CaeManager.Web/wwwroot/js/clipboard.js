// Copia texto al portapapeles. navigator.clipboard.writeText requiere un
// contexto seguro (HTTPS o localhost) — si no está disponible, se usa el
// método de respaldo con un <textarea> oculto y document.execCommand.
export async function copiarAlPortapapeles(texto) {
    if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(texto);
        return;
    }

    const areaTexto = document.createElement('textarea');
    areaTexto.value = texto;
    areaTexto.style.position = 'fixed';
    areaTexto.style.opacity = '0';
    document.body.appendChild(areaTexto);
    areaTexto.select();
    const copiado = document.execCommand('copy');
    document.body.removeChild(areaTexto);

    // execCommand devuelve false sin lanzar cuando el navegador deniega la
    // copia (permisos, política del documento) — sin este chequeo la
    // promesa resolvía igual y el llamador anunciaba un éxito que no ocurrió.
    if (!copiado) {
        throw new Error('document.execCommand("copy") devolvió false.');
    }
}
