// Visor de documento a pantalla completa (VisorDocumentoPantallaCompleta.razor). El foco lo atrapa dialogo-foco.js; aquí solo
// queda lo que Blazor no puede hacer sin interop: llevar la hoja elegida (o el principio del adjunto) a la vista del lienzo.
export function irAPagina(lienzo, indice) {
    if (!lienzo) return;
    if (indice <= 0) {
        lienzo.scrollTop = 0;
        return;
    }
    lienzo.querySelector(`[data-visor-pagina="${indice}"]`)?.scrollIntoView({ block: 'start' });
}
