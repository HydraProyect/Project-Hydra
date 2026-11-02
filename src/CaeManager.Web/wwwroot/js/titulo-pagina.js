// Fija el título de la pestaña desde una página sin prerender. El HeadOutlet del documento es estático:
// el <PageTitle> de una página que solo existe en el circuito no le llega, y la pestaña se quedaba sin título.
let activo = null;

function reponer() {
    // Una navegación mejorada a la misma ruta (otro filtro en la URL) trae un <head> sin título y el componente
    // no se vuelve a pintar: se repone solo mientras se siga en la ruta donde se fijó.
    if (activo && location.pathname === activo.ruta && document.title !== activo.titulo) {
        document.title = activo.titulo;
    }
}

export function fijarTitulo(titulo) {
    if (!activo && window.Blazor?.addEventListener) {
        window.Blazor.addEventListener('enhancedload', reponer);
    }
    activo = { titulo, ruta: location.pathname };
    document.title = titulo;
}

export function liberar() {
    activo = null;
    window.Blazor?.removeEventListener?.('enhancedload', reponer);
}
