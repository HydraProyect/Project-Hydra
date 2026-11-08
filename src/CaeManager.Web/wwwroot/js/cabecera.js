// Comportamiento de la cabecera que no necesita circuito (rediseño de la cabecera,
// propuestas 01, 06 y 07). Todo delegado en document, como microinteracciones.js y
// tooltip.js: sin marcado en línea por la CSP, y sobrevive al reemplazo del DOM que
// hace la navegación "enhanced" de Blazor.
//
//  - Menú de usuario: un botón [data-menu-cabecera] controla el panel cuyo id lleva en
//    aria-controls. Abre y cierra con clic, Esc (devuelve el foco al botón) y clic fuera;
//    con teclado, las flechas recorren los [role="menuitem"] y Inicio/Fin saltan a los
//    extremos. Los selectores de la propia cabecera que son islotes interactivos (campana,
//    vistas) llevan su estado en Blazor y no pasan por aquí.
//  - Cabecera fija: marca [data-desplazada] en .cabecera-fija cuando la página se ha
//    desplazado, para que el CSS le ponga la sombra y el velo.
//  - Banda de avisos del sistema: [data-descartar-aviso] retira su aviso con animación y,
//    cuando no queda ninguno, pliega la banda entera (.cabecera-bandas.cerrada).
(function () {
    const ITEMS = '[role="menuitem"]:not([disabled])';

    const panelDe = function (boton) {
        const id = boton.getAttribute('aria-controls');
        return id ? document.getElementById(id) : null;
    };

    const estaAbierto = function (boton) {
        return boton.getAttribute('aria-expanded') === 'true';
    };

    const poner = function (boton, abierto, conTeclado) {
        const panel = panelDe(boton);
        if (!panel) return;
        boton.setAttribute('aria-expanded', abierto ? 'true' : 'false');
        panel.classList.toggle('abierto', abierto);
        if (abierto && conTeclado) {
            const primero = panel.querySelector(ITEMS);
            if (primero) primero.focus();
        }
    };

    const cerrarTodos = function (salvo, devolverFoco) {
        document.querySelectorAll('[data-menu-cabecera][aria-expanded="true"]').forEach(function (boton) {
            if (boton === salvo) return;
            poner(boton, false, false);
            if (devolverFoco) boton.focus();
        });
    };

    document.addEventListener('click', function (e) {
        const boton = e.target.closest ? e.target.closest('[data-menu-cabecera]') : null;
        if (boton) {
            const abrir = !estaAbierto(boton);
            cerrarTodos(boton, false);
            // detail === 0: el clic viene del teclado (Enter/Espacio sobre el botón).
            poner(boton, abrir, abrir && e.detail === 0);
            return;
        }
        // Un clic dentro del panel no lo cierra salvo que sea una acción que navega o envía.
        const dentro = e.target.closest ? e.target.closest('.panel-cabecera') : null;
        if (dentro && !(e.target.closest('a[href]'))) return;
        cerrarTodos(null, false);
    });

    document.addEventListener('keydown', function (e) {
        const abierto = document.querySelector('[data-menu-cabecera][aria-expanded="true"]');
        if (!abierto) return;

        if (e.key === 'Escape') {
            e.preventDefault();
            cerrarTodos(null, true);
            return;
        }

        const panel = panelDe(abierto);
        if (!panel || !panel.contains(document.activeElement) && document.activeElement !== abierto) return;

        const items = Array.prototype.slice.call(panel.querySelectorAll(ITEMS));
        if (items.length === 0) return;
        const actual = items.indexOf(document.activeElement);
        // Solo se secuestran las flechas con el foco en el botón o en un elemento del menú: un
        // <select> (el idioma) las necesita para sí.
        if (actual === -1 && document.activeElement !== abierto) return;
        let siguiente = -1;
        if (e.key === 'ArrowDown') siguiente = (actual + 1) % items.length;
        else if (e.key === 'ArrowUp') siguiente = actual <= 0 ? items.length - 1 : actual - 1;
        else if (e.key === 'Home') siguiente = 0;
        else if (e.key === 'End') siguiente = items.length - 1;
        if (siguiente < 0) return;
        e.preventDefault();
        items[siguiente].focus();
    });

    // Tab fuera del panel lo cierra: el foco no se queda en un menú que ya no se ve.
    document.addEventListener('focusin', function (e) {
        const abierto = document.querySelector('[data-menu-cabecera][aria-expanded="true"]');
        if (!abierto) return;
        const panel = panelDe(abierto);
        if (panel && !panel.contains(e.target) && e.target !== abierto) poner(abierto, false, false);
    });

    // Sombra y velo al desplazar. El que desplaza es main.contenido (el shell mide la ventana,
    // MainLayout.razor.css), no el documento. El scroll no burbujea: se escucha en captura y se
    // filtra por el contenedor, que sobrevive a la navegación mejorada pero se relee cada vez.
    const contenidoPrincipal = function () { return document.getElementById('contenido-principal'); };
    const marcarDesplazamiento = function () {
        const fija = document.querySelector('.cabecera-fija');
        if (!fija) return;
        const contenido = contenidoPrincipal();
        const desplazada = (contenido ? contenido.scrollTop : 0) > 4;
        if (desplazada) fija.setAttribute('data-desplazada', '');
        else fija.removeAttribute('data-desplazada');
    };
    document.addEventListener('scroll', function (e) {
        if (e.target === contenidoPrincipal()) marcarDesplazamiento();
    }, { capture: true, passive: true });
    window.addEventListener('load', marcarDesplazamiento);

    // Al cambiar de pantalla, .contenido vuelve arriba. Blazor solo devuelve arriba la ventana
    // (window.scrollTo), que ya no desplaza: sin esto la pantalla nueva heredaría el scroll de la
    // anterior. Solo si cambia la ruta: un envío de formulario a la misma URL no salta arriba, y
    // un ancla (#) la resuelve el navegador.
    let rutaMostrada = location.pathname + location.search;
    const volverArribaSiCambiaLaPantalla = function () {
        const ruta = location.pathname + location.search;
        if (ruta === rutaMostrada) return;
        rutaMostrada = ruta;
        const contenido = contenidoPrincipal();
        // 'instant': .contenido lleva scroll-behavior: smooth (base.css) y una pantalla nueva no se anima hasta arriba.
        if (contenido && !location.hash) contenido.scrollTo({ top: 0, behavior: 'instant' });
        marcarDesplazamiento();
    };
    // Mismo patrón que menu-lateral.js: si Blazor aún no expone addEventListener, se reintenta en load.
    let navegacionEnganchada = false;
    const engancharNavegacion = function () {
        if (navegacionEnganchada || !window.Blazor || typeof window.Blazor.addEventListener !== 'function') return;
        window.Blazor.addEventListener('enhancedload', volverArribaSiCambiaLaPantalla);
        navegacionEnganchada = true;
    };
    engancharNavegacion();
    window.addEventListener('load', engancharNavegacion);

    // Teclado sin foco dentro de la página (carga inicial, sin clic): AvPág, RePág, Espacio,
    // Inicio, Fin y las flechas desplazarían el documento, que ya no desplaza. Se aplican a
    // .contenido. Solo con el foco en body: un control enfocado conserva sus teclas, y quien
    // ya las haya atendido (defaultPrevented) gana.
    // Un clic en texto no enfocable de otro scroller (menú lateral, panel de avisos) deja el foco
    // en body: esas teclas son de ese scroller, no de .contenido.
    // El clic deja de contar en cuanto el foco entra en algo que no es body (focusin) o se mueve con Tab:
    // sin esto, un clic viejo en el menú lateral seguiría quitándole las teclas a .contenido.
    let ultimoPulsado = null;
    document.addEventListener('pointerdown', function (e) { ultimoPulsado = e.target; }, { capture: true, passive: true });
    document.addEventListener('focusin', function (e) {
        if (e.target !== document.body && e.target !== document.documentElement) ultimoPulsado = null;
    }, { capture: true, passive: true });
    const dentroDeOtroScroller = function (nodo, contenido) {
        for (let n = nodo instanceof Element ? nodo : null; n && n !== contenido; n = n.parentElement) {
            const oy = getComputedStyle(n).overflowY;
            if ((oy === 'auto' || oy === 'scroll') && n.scrollHeight > n.clientHeight) return true;
        }
        return false;
    };
    // Mismo selector de modal que keytips.js y atajos-contexto.js; incluye <dialog> abierto con showModal() (ReconnectModal).
    const SELECTOR_MODAL = '[role="dialog"][aria-modal="true"], [role="alertdialog"][aria-modal="true"], dialog:modal';
    document.addEventListener('keydown', function (e) {
        if (e.key === 'Tab') { ultimoPulsado = null; return; }
        if (e.defaultPrevented || e.altKey || e.ctrlKey || e.metaKey) return;
        // Mayús + flecha, Inicio, Fin, RePág o AvPág extiende la selección: solo Mayús + Espacio (subir) es nuestro.
        if (e.shiftKey && e.key !== ' ') return;
        const activo = document.activeElement;
        if (activo && activo !== document.body && activo !== document.documentElement) return;
        if (document.querySelector(SELECTOR_MODAL)) return;
        const contenido = contenidoPrincipal();
        if (!contenido) return;
        if (ultimoPulsado && ultimoPulsado.isConnected && dentroDeOtroScroller(ultimoPulsado, contenido)) return;
        const pagina = contenido.clientHeight * 0.875;
        let destino = null;
        switch (e.key) {
            case 'PageDown': destino = contenido.scrollTop + pagina; break;
            case 'PageUp': destino = contenido.scrollTop - pagina; break;
            case ' ': destino = contenido.scrollTop + (e.shiftKey ? -pagina : pagina); break;
            case 'ArrowDown': destino = contenido.scrollTop + 40; break;
            case 'ArrowUp': destino = contenido.scrollTop - 40; break;
            case 'Home': destino = 0; break;
            case 'End': destino = contenido.scrollHeight; break;
            default: return;
        }
        e.preventDefault();
        // Las flechas, sin animar: con la tecla mantenida cada paso se suma a un scrollTop ya asentado.
        const flecha = e.key === 'ArrowDown' || e.key === 'ArrowUp';
        contenido.scrollTo(flecha ? { top: destino, behavior: 'instant' } : { top: destino });
    });

    // Banda de avisos: descartar un aviso y plegar la banda cuando se queda vacía.

    // Un aviso que llega con la banda plegada (los de los islotes interactivos pintan después de
    // la carga: ventana de soporte a punto de terminar) la reabre: la banda nunca esconde un aviso.
    let observador = null;
    const vigilarBanda = function (banda) {
        if (observador) observador.disconnect();
        observador = new MutationObserver(function () {
            if (banda.classList.contains('cerrada') && banda.querySelector('[data-aviso-sistema]:not([data-saliendo])'))
                banda.classList.remove('cerrada');
        });
        observador.observe(banda, { childList: true, subtree: true });
    };
    const plegarSiVacia = function () {
        const banda = document.querySelector('.cabecera-bandas');
        if (!banda) return;
        const quedan = banda.querySelector('[data-aviso-sistema]:not([data-saliendo])');
        vigilarBanda(banda);
        banda.classList.toggle('cerrada', !quedan);
    };

    document.addEventListener('click', function (e) {
        const boton = e.target.closest ? e.target.closest('[data-descartar-aviso]') : null;
        if (!boton) return;
        const aviso = boton.closest('[data-aviso-sistema]');
        if (!aviso) return;
        e.preventDefault();
        aviso.setAttribute('data-saliendo', '');
        const reducido = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        const retirar = function () {
            aviso.remove();
            plegarSiVacia();
        };
        if (reducido) {
            retirar();
            return;
        }
        // El aviso se pliega con su propia transición; la banda, con la suya (grid-template-rows).
        plegarSiVacia();
        aviso.addEventListener('transitionend', retirar, { once: true });
        setTimeout(function () { if (aviso.isConnected) retirar(); }, 600);
    });
})();
