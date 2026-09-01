// Colapso individual de cada grupo del menú lateral (Dashboards/Negocio/
// Operación/Control/Administración). El toggle en sí es <details>/<summary>
// nativo (NavMenu.razor no tiene @rendermode propio, así que no hay circuito
// para manejar un @onclick — ver el comentario de MainLayout.razor sobre
// "todo lo que vive fuera de @Body necesita su propio @rendermode
// explícito"). Este script persiste, SOLO para los grupos que el usuario
// tocó alguna vez, si quedaron abiertos o cerrados (localStorage, por
// navegador -- no por cuenta, es cosmético, no viaja entre dispositivos como
// el tema de SelectorTema.razor) y lo reaplica tras cada navegación
// "enhanced" de Blazor, que vuelve a pedir el HTML al servidor y sustituye
// cada <details> por uno nuevo con el "open" que trae el marcado (mismo
// problema ya documentado y resuelto en tema.js). Un grupo sin registro
// guardado conserva el "open" (o su ausencia) del propio marcado -- así el
// colapso por defecto de Control no se pisa con un "abierto" forzado en el
// primer render de cada usuario.
const CLAVE_ALMACENAMIENTO = 'hydra-menu-grupos-estado';

function leerEstados() {
    try {
        const datos = JSON.parse(localStorage.getItem(CLAVE_ALMACENAMIENTO) ?? '{}');
        return (datos && typeof datos === 'object' && !Array.isArray(datos)) ? datos : {};
    } catch {
        return {};
    }
}

function guardarEstados(estados) {
    localStorage.setItem(CLAVE_ALMACENAMIENTO, JSON.stringify(estados));
}

function aplicarEstadoGuardado() {
    const estados = leerEstados();
    document.querySelectorAll('.nav-grupo-detalle[data-grupo]').forEach(detalle => {
        const grupo = detalle.dataset.grupo;
        if (Object.prototype.hasOwnProperty.call(estados, grupo)) {
            detalle.open = estados[grupo];
        }
    });
}

// capture:true -- el evento "toggle" de <details> no burbujea en todos los
// navegadores, pero la fase de captura sí atraviesa hasta el elemento
// objetivo independientemente de eso, así que un único listener en
// document (delegado, sobrevive al morph del DOM tras cada navegación)
// basta sin tener que re-enganchar nada por elemento.
document.addEventListener('toggle', function (evento) {
    const detalle = evento.target;
    if (!detalle.matches?.('.nav-grupo-detalle[data-grupo]')) return;

    const estados = leerEstados();
    estados[detalle.dataset.grupo] = detalle.open;
    guardarEstados(estados);
}, true);

aplicarEstadoGuardado();

// window.Blazor puede no estar listo todavía en el instante exacto en que
// este script se ejecuta (aunque va después de blazor.web.js en App.razor) —
// reintento defensivo en "load", mismo criterio que el resto de módulos.
function engancharNavegacionEnhanced() {
    if (!window.Blazor?.addEventListener) return false;
    window.Blazor.addEventListener('enhancedload', aplicarEstadoGuardado);
    return true;
}

if (!engancharNavegacionEnhanced()) {
    window.addEventListener('load', engancharNavegacionEnhanced, { once: true });
}
