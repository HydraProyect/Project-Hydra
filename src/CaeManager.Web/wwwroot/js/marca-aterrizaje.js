// Quita de la barra de direcciones la marca de un solo uso del aterrizaje tras
// iniciar sesión (?desde=login) SIN navegar.
//
// history.replaceState no dispara popstate ni pasa por el enrutador de Blazor:
// el NavigationManager no emite LocationChanged. Con NavigateTo("/", replace)
// sí lo emitía, tarde (tras el circuito y las consultas de Inicio), y ese
// evento cerraba el selector de empresa recién abierto y podía pisar un
// form.submit() en vuelo.
//
// Solo actúa si la URL actual TODAVÍA lleva la marca: si el usuario ya navegó a
// otra pantalla mientras Inicio cargaba, no se toca la URL de la nueva página.
// Conserva history.state (Blazor guarda ahí el índice de la entrada para
// «Atrás»/«Adelante»), el resto de parámetros y el fragmento.
export function quitarMarca(parametro, valor) {
    const url = new URL(window.location.href);
    if (url.searchParams.get(parametro) !== valor) {
        return;
    }

    url.searchParams.delete(parametro);
    window.history.replaceState(window.history.state, '', url.pathname + url.search + url.hash);
}
