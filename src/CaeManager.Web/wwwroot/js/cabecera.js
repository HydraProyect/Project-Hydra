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

    // Sombra y velo al desplazar.
    const marcarDesplazamiento = function () {
        const fija = document.querySelector('.cabecera-fija');
        if (!fija) return;
        const desplazada = (window.scrollY || document.documentElement.scrollTop) > 4;
        if (desplazada) fija.setAttribute('data-desplazada', '');
        else fija.removeAttribute('data-desplazada');
    };
    window.addEventListener('scroll', marcarDesplazamiento, { passive: true });
    window.addEventListener('load', marcarDesplazamiento);

    // Banda de avisos: descartar un aviso y plegar la banda cuando se queda vacía.
    const plegarSiVacia = function () {
        const banda = document.querySelector('.cabecera-bandas');
        if (!banda) return;
        const quedan = banda.querySelector('[data-aviso-sistema]:not([data-saliendo])');
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
