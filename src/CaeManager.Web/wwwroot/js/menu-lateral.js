// Comportamiento del menú lateral que es del NAVEGADOR y no del servidor
// (NavMenu.razor no tiene @rendermode propio, así que no hay circuito para un
// @onclick — ver el comentario de MainLayout.razor sobre "todo lo que vive fuera
// de @Body necesita su propio @rendermode explícito"). Todo va por localStorage,
// por navegador: no viaja con la cuenta como el tema de SelectorTema.razor, es
// preferencia personal y cosmética, y nunca concede nada.
//
//  1. Grupos abiertos/cerrados (clave hydra-menu-grupos-estado): SOLO los que el
//     usuario tocó. Un grupo sin registro conserva el "open" del marcado
//     (data-abierto-defecto) — así el colapso por defecto de Control no se pisa.
//  2. El grupo que contiene la página activa se abre solo al llegar a ella, sin
//     guardarlo como decisión del usuario.
//  3. Modo compacto (hydra-menu-compacto): <html data-menu-compacto>. Mientras
//     dura, todos los grupos se abren (para que todos los iconos se vean) y al
//     ensanchar vuelve el estado guardado, intacto. menu-lateral-inicio.js lo
//     pone antes del primer pintado para que no parpadee.
//  4. Fijados (hydra-menu-fijados): lista de identificadores de enlace. Solo se
//     pintan los que YA están en este menú (el que el rol y el contexto de la
//     persona hayan dejado): un identificador ajeno se ignora, así que fijar
//     nunca da acceso a nada.
//  5. Búsqueda: un icono de lupa en la cabecera despliega el campo (animado). Filtra los enlaces del
//     menú y muestra las opciones de dentro de las páginas (Configuración, pestañas de Documentos) que
//     el servidor ya dejó en el marcado según las reglas de visibilidad del catálogo: este script solo
//     muestra u oculta lo que existe, nunca añade un destino. Elegir una navega directo. Esc cierra.
//     Lo escrito y si está abierta no se guardan.
//  6. Tooltip (tooltip.js) con el rótulo de cada enlace cuando está compacto o
//     el rótulo largo se corta con puntos suspensivos.
//
// Se reaplica tras cada navegación "enhanced" de Blazor, que vuelve a pedir el
// HTML y sustituye cada <details> por uno nuevo con el "open" del marcado (mismo
// problema ya resuelto en tema.js), y cuando aparece un menú nuevo en el DOM (el
// del cajón móvil, que es un componente interactivo y no dispara esa navegación).
const CLAVE_ESTADO = 'hydra-menu-grupos-estado';
const CLAVE_COMPACTO = 'hydra-menu-compacto';
const CLAVE_FIJADOS = 'hydra-menu-fijados';

function leerJson(clave, vacio) {
    try {
        const datos = JSON.parse(localStorage.getItem(clave) ?? 'null');
        return datos ?? vacio;
    } catch {
        return vacio;
    }
}

function guardar(clave, valor) {
    try {
        localStorage.setItem(clave, typeof valor === 'string' ? valor : JSON.stringify(valor));
    } catch {
        // Sin almacenamiento (navegación privada, cuota): el menú sigue funcionando, solo no recuerda.
    }
}

function leerEstados() {
    const datos = leerJson(CLAVE_ESTADO, {});
    return (datos && typeof datos === 'object' && !Array.isArray(datos)) ? datos : {};
}

function leerFijados() {
    const datos = leerJson(CLAVE_FIJADOS, []);
    return Array.isArray(datos) ? datos.filter(id => typeof id === 'string') : [];
}

function esCompacto() {
    try {
        return localStorage.getItem(CLAVE_COMPACTO) === '1';
    } catch {
        return false;
    }
}

const normalizar = texto => (texto ?? '').normalize('NFD').replace(/\p{M}/gu, '').toLowerCase().trim();

const menus = () => document.querySelectorAll('.nav-principal');
// El modo compacto es de la barra lateral: el menú del cajón móvil (bajo 1024px) siempre va completo.
const enBarraLateral = nav => nav.closest('.barra-lateral') !== null;
const filtroActivo = nav => normalizar(nav.querySelector('[data-menu-filtro]')?.value);

// La búsqueda abierta vive en memoria: una navegación "enhanced" repinta el HTML del servidor (cerrado)
// y refrescarTodo la vuelve a abrir si seguía abierta.
let busquedaAbierta = false;

function fijarAbierto(detalle, abierto) {
    // Lo guardado es solo lo que el usuario decide al pulsar la cabecera (ver el listener de
    // "click" más abajo), no lo que el evento "toggle" informe: así ni abrir un grupo por la
    // página activa ni abrirlos todos en modo compacto se confunden con una decisión suya.
    detalle.open = abierto;
    detalle.querySelector(':scope > summary')?.setAttribute('aria-expanded', String(detalle.open));
}

function aplicarGrupos() {
    const estados = leerEstados();
    menus().forEach(nav => {
        const compacto = esCompacto() && enBarraLateral(nav);
        const filtrando = filtroActivo(nav) !== '';
        nav.querySelectorAll('details.nav-grupo-detalle[data-grupo]').forEach(detalle => {
            const coincide = filtrando && detalle.querySelector('.nav-fila:not([hidden])') !== null;
            if (compacto || coincide) {
                fijarAbierto(detalle, true);
                return;
            }

            const grupo = detalle.dataset.grupo;
            const guardado = Object.prototype.hasOwnProperty.call(estados, grupo)
                ? !!estados[grupo]
                : detalle.dataset.abiertoDefecto === 'true';
            // La página activa manda sobre el estado guardado: un grupo cerrado no la esconde.
            const contieneActiva = detalle.querySelector('.nav-item.active') !== null;
            fijarAbierto(detalle, guardado || contieneActiva);
        });
    });
}

// Un enlace (fila) por identificador estable, solo de los grupos; las copias de fijados no cuentan.
const filasOriginales = nav => nav.querySelectorAll('.nav-grupo-detalle .nav-fila[data-enlace]');

function pintarFijados() {
    const fijados = leerFijados();
    menus().forEach(nav => {
        const contenedor = nav.querySelector('[data-menu-fijados]');
        if (!contenedor) return;
        const lista = contenedor.querySelector('[data-menu-fijados-lista]');
        lista.replaceChildren();

        const porId = new Map();
        filasOriginales(nav).forEach(fila => porId.set(fila.dataset.enlace, fila));
        fijados.forEach(id => {
            const fila = porId.get(id);
            if (fila) lista.appendChild(fila.cloneNode(true));
        });

        nav.querySelectorAll('[data-fijar]').forEach(boton => {
            const fijado = fijados.includes(boton.dataset.fijar);
            boton.setAttribute('aria-pressed', String(fijado));
            boton.setAttribute('aria-label', fijado ? boton.dataset.etiquetaQuitar : boton.dataset.etiquetaFijar);
        });
    });
}

function aplicarFiltro(nav) {
    const q = filtroActivo(nav);
    nav.querySelectorAll('.nav-fila').forEach(fila => {
        const texto = normalizar(fila.querySelector('.nav-item-texto')?.textContent);
        fila.hidden = q !== '' && !texto.includes(q);
    });
    nav.querySelectorAll('.nav-grupo-detalle').forEach(grupo => {
        grupo.hidden = q !== '' && grupo.querySelector('.nav-fila:not([hidden])') === null;
    });
    // Opciones de dentro de las páginas: se muestran las que casan con lo escrito, bajo el campo.
    const resultados = nav.querySelector('[data-menu-resultados]');
    if (resultados) {
        let alguno = false;
        resultados.querySelectorAll('[data-subopcion]').forEach(enlace => {
            const texto = normalizar(enlace.querySelector('.nav-resultado-texto')?.textContent);
            const casa = q !== '' && texto.includes(q);
            enlace.hidden = !casa;
            alguno = alguno || casa;
        });
        resultados.hidden = !alguno;
    }
    const contenedor = nav.querySelector('[data-menu-fijados]');
    if (contenedor) {
        const hayFilas = contenedor.querySelector('.nav-fila:not([hidden])') !== null;
        contenedor.hidden = !hayFilas;
    }
}

function actualizarTooltips() {
    menus().forEach(nav => {
        const compacto = esCompacto() && enBarraLateral(nav);
        nav.querySelectorAll('.nav-item').forEach(enlace => {
            const rotulo = enlace.querySelector('.nav-item-texto');
            const texto = rotulo?.textContent.trim();
            const cortado = rotulo !== null && rotulo.scrollWidth > rotulo.clientWidth;
            if (texto && (compacto || cortado)) {
                enlace.setAttribute('data-tooltip', texto);
            } else {
                enlace.removeAttribute('data-tooltip');
            }
        });
    });
}

function aplicarCompacto() {
    const compacto = esCompacto();
    document.documentElement.toggleAttribute('data-menu-compacto', compacto);
    document.querySelectorAll('[data-menu-compactar]').forEach(boton =>
        boton.setAttribute('aria-label', compacto ? boton.dataset.etiquetaAmpliar : boton.dataset.etiquetaCompactar));
}

// Despliega o recoge el campo de búsqueda de todos los menús; al recoger se vacía lo escrito.
function fijarBusqueda(abierta, enfocar) {
    busquedaAbierta = abierta;
    menus().forEach(nav => {
        const panel = nav.querySelector('[data-menu-busqueda]');
        const lupa = nav.querySelector('[data-menu-lupa]');
        if (!panel || !lupa) return;
        const teniaFoco = panel.contains(document.activeElement);
        // inert mientras está recogido: sin foco por teclado ni lectura aunque se vea la transición.
        panel.toggleAttribute('inert', !abierta);
        panel.toggleAttribute('data-abierta', abierta);
        lupa.setAttribute('aria-expanded', String(abierta));
        const campo = panel.querySelector('[data-menu-filtro]');
        if (!abierta && campo) campo.value = '';
        if (abierta && enfocar && campo && nav.offsetParent !== null) campo.focus({ preventScroll: true });
        if (!abierta && enfocar && teniaFoco) lupa.focus();
    });
    menus().forEach(aplicarFiltro);
    aplicarGrupos();
}

function refrescarTodo() {
    aplicarCompacto();
    menus().forEach(nav => {
        const panel = nav.querySelector('[data-menu-busqueda]');
        if (!panel) return;
        // Compacto no tiene campo: no puede quedar una búsqueda invisible activa.
        const abierta = busquedaAbierta && !(esCompacto() && enBarraLateral(nav));
        panel.toggleAttribute('inert', !abierta);
        panel.toggleAttribute('data-abierta', abierta);
        nav.querySelector('[data-menu-lupa]')?.setAttribute('aria-expanded', String(abierta));
    });
    pintarFijados();
    menus().forEach(aplicarFiltro);
    aplicarGrupos();
    actualizarTooltips();
}

// capture:true -- el evento "toggle" de <details> no burbujea en todos los
// navegadores, pero la fase de captura sí atraviesa hasta el elemento objetivo,
// así que un único listener en document (delegado, sobrevive al morph del DOM
// tras cada navegación) basta sin tener que re-enganchar nada por elemento.
document.addEventListener('toggle', function (evento) {
    const detalle = evento.target;
    if (!detalle.matches?.('details.nav-grupo-detalle[data-grupo]')) return;
    detalle.querySelector(':scope > summary')?.setAttribute('aria-expanded', String(detalle.open));
}, true);

document.addEventListener('click', function (evento) {
    // Decisión del usuario: pulsar la cabecera de un grupo (también con teclado, que dispara el clic).
    const cabecera = evento.target.closest?.('.nav-principal summary.nav-grupo-titulo');
    if (cabecera) {
        const detalle = cabecera.parentElement;
        // El navegador alterna "open" después del clic: se lee al terminar el turno.
        setTimeout(function () {
            const estados = leerEstados();
            estados[detalle.dataset.grupo] = detalle.open;
            guardar(CLAVE_ESTADO, estados);
        }, 0);
        return;
    }

    const compactar = evento.target.closest?.('[data-menu-compactar]');
    if (compactar) {
        guardar(CLAVE_COMPACTO, esCompacto() ? '0' : '1');
        // El campo de búsqueda desaparece en modo compacto: no puede quedar un filtro invisible activo.
        fijarBusqueda(false, false);
        aplicarCompacto();
        aplicarGrupos();
        actualizarTooltips();
        // El ancho se anima: el recorte de los rótulos solo se puede medir al terminar.
        setTimeout(actualizarTooltips, 350);
        return;
    }

    // Elegir un enlace del menú con la búsqueda abierta la recoge: no se queda el texto sobre la página de destino.
    if (busquedaAbierta && evento.target.closest?.('.nav-principal .nav-item')) {
        setTimeout(() => fijarBusqueda(false, false), 0);
        return;
    }

    const lupa = evento.target.closest?.('[data-menu-lupa]');
    if (lupa) {
        fijarBusqueda(!busquedaAbierta, true);
        return;
    }

    // Elegir una opción de dentro de una página: la navegación la hace el propio enlace; aquí solo se
    // recoge la búsqueda para que no se quede abierta con el texto escrito sobre la página de destino.
    if (evento.target.closest?.('[data-subopcion]')) {
        setTimeout(() => fijarBusqueda(false, false), 0);
        return;
    }

    const fijar = evento.target.closest?.('[data-fijar]');
    if (fijar) {
        const id = fijar.dataset.fijar;
        const fijados = leerFijados();
        guardar(CLAVE_FIJADOS, fijados.includes(id) ? fijados.filter(x => x !== id) : [...fijados, id]);
        pintarFijados();
        menus().forEach(aplicarFiltro);
        actualizarTooltips();
    }
});

document.addEventListener('input', function (evento) {
    if (!evento.target.matches?.('[data-menu-filtro]')) return;
    const nav = evento.target.closest('.nav-principal');
    aplicarFiltro(nav);
    aplicarGrupos();
});

document.addEventListener('keydown', function (evento) {
    const campo = evento.target.matches?.('[data-menu-filtro]') ? evento.target : null;
    if (!campo) return;
    if (evento.key === 'Escape') {
        // No cerrar además el cajón u otro panel que escuche Escape.
        evento.stopPropagation();
        fijarBusqueda(false, true);
    } else if (evento.key === 'Enter') {
        const nav = campo.closest('.nav-principal');
        // Sin nada escrito Enter no navega: el primer enlace sería «Inicio».
        if (filtroActivo(nav) === '') return;
        (nav.querySelector('.nav-fila:not([hidden]) .nav-item')
            ?? nav.querySelector('[data-subopcion]:not([hidden])'))?.click();
    }
});

window.addEventListener('resize', actualizarTooltips);

// El menú del cajón móvil nace dentro de un circuito interactivo: ningún evento de navegación
// avisa de que acaba de aparecer, así que se vigila su alta en el DOM.
let pendiente = 0;
new MutationObserver(function (cambios) {
    const nuevo = cambios.some(c => [...c.addedNodes].some(n =>
        n.nodeType === 1 && (n.matches('.nav-principal') || n.querySelector('.nav-principal'))));
    if (!nuevo || pendiente) return;
    pendiente = requestAnimationFrame(() => {
        pendiente = 0;
        refrescarTodo();
    });
}).observe(document.body, { childList: true, subtree: true });

refrescarTodo();

// window.Blazor puede no estar listo todavía en el instante exacto en que
// este script se ejecuta (aunque va después de blazor.web.js en App.razor) —
// reintento defensivo en "load", mismo criterio que el resto de módulos.
function engancharNavegacionEnhanced() {
    if (!window.Blazor?.addEventListener) return false;
    window.Blazor.addEventListener('enhancedload', refrescarTodo);
    return true;
}

if (!engancharNavegacionEnhanced()) {
    window.addEventListener('load', function () {
        engancharNavegacionEnhanced();
        refrescarTodo();
    }, { once: true });
}
