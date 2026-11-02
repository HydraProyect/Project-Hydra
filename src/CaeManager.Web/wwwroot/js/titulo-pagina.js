// Fija el título de la pestaña desde una página sin prerender. El HeadOutlet del documento es estático:
// el <PageTitle> de una página que solo existe en el circuito no le llega, y la pestaña se quedaba sin título.
// El módulo es único por documento y varias instancias del componente pueden solaparse al salir y volver a la
// página; cada una se identifica con su id y solo libera lo que fijó ella.
let activo = null;

function reponer() {
    // Una navegación mejorada a la misma ruta (otro filtro en la URL) trae un <head> sin título y el componente
    // no se vuelve a pintar: se repone solo mientras se siga en la ruta donde se fijó.
    if (activo && location.pathname === activo.ruta && document.title !== activo.titulo) {
        document.title = activo.titulo;
    }
}

export function fijarTitulo(titulo, id) {
    if (!activo && window.Blazor?.addEventListener) {
        window.Blazor.addEventListener('enhancedload', reponer);
    }
    activo = { id, titulo, ruta: location.pathname };
    document.title = titulo;
}

export function liberar(id) {
    if (!activo || activo.id !== id) {
        return;
    }

    activo = null;
    window.Blazor?.removeEventListener?.('enhancedload', reponer);
}
