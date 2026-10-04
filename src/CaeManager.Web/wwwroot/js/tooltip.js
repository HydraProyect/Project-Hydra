// Tooltip propio para botones de solo icono (ficha 13 del acabado). Sustituye al
// `title` nativo ÚNICAMENTE donde el botón no tiene texto: ahí el `title` tarda,
// no se puede estilar y no sale con el foco de teclado. Se activa con
// `data-tooltip="texto"` en el botón; el nombre accesible sigue siendo su
// aria-label (este script no lo toca).
//
// Un solo nodo `role="tooltip"` para toda la página, delegado en document (sin
// marcado en línea por la CSP, como microinteracciones.js) y colocado con
// position:fixed para que ningún overflow de tabla, drawer o modal lo recorte.
// Entra tras 400 ms y se retira al instante; con teclado sale al enfocar
// (solo :focus-visible) y se enlaza con aria-describedby mientras está visible.
(function () {
    const RETARDO_MS = 400;
    const SEPARACION = 8;
    const MARGEN = 8;
    let nodo = null;
    let disparador = null;
    let temporizador = null;
    let vigilante = 0;

    const crear = function () {
        if (nodo) return nodo;
        nodo = document.createElement('div');
        nodo.id = 'tooltip-propio';
        nodo.className = 'tooltip-propio';
        nodo.setAttribute('role', 'tooltip');
        document.body.appendChild(nodo);
        return nodo;
    };

    const ocultar = function () {
        clearTimeout(temporizador);
        temporizador = null;
        cancelAnimationFrame(vigilante);
        if (disparador) {
            disparador.removeAttribute('aria-describedby');
            disparador = null;
        }
        if (nodo) nodo.classList.remove('tooltip-propio-visible');
    };

    // Si el botón desaparece con el tooltip abierto (Blazor lo repinta o navega) no
    // llega ningún evento de salida: se vigila mientras el tooltip está visible.
    const vigilar = function () {
        if (disparador && !disparador.isConnected) { ocultar(); return; }
        if (disparador) vigilante = requestAnimationFrame(vigilar);
    };

    const mostrar = function (boton) {
        const texto = boton.getAttribute('data-tooltip');
        if (!texto || !boton.isConnected) return;
        const el = crear();
        el.textContent = texto;
        el.style.left = '0px';
        el.style.top = '0px';
        el.classList.remove('tooltip-propio-abajo');

        const r = boton.getBoundingClientRect();
        const ancho = el.offsetWidth;
        const alto = el.offsetHeight;
        let arriba = r.top - alto - SEPARACION;
        const abajo = arriba < MARGEN;
        if (abajo) arriba = r.bottom + SEPARACION;
        const centro = r.left + r.width / 2;
        const izquierda = Math.min(Math.max(centro - ancho / 2, MARGEN), window.innerWidth - ancho - MARGEN);

        el.style.left = Math.round(izquierda) + 'px';
        el.style.top = Math.round(arriba) + 'px';
        el.style.setProperty('--tooltip-flecha-x', Math.round(centro - izquierda) + 'px');
        el.classList.toggle('tooltip-propio-abajo', abajo);
        el.classList.add('tooltip-propio-visible');

        disparador = boton;
        boton.setAttribute('aria-describedby', el.id);
        vigilante = requestAnimationFrame(vigilar);
    };

    const programar = function (boton) {
        ocultar();
        temporizador = setTimeout(function () { mostrar(boton); }, RETARDO_MS);
    };

    const origen = function (evento) {
        return evento.target.closest ? evento.target.closest('[data-tooltip]') : null;
    };

    document.addEventListener('pointerover', function (evento) {
        if (evento.pointerType === 'touch') return;
        const boton = origen(evento);
        if (boton && boton !== disparador) programar(boton);
    });
    document.addEventListener('pointerout', function (evento) {
        const boton = origen(evento);
        if (boton && !boton.contains(evento.relatedTarget)) ocultar();
    });
    document.addEventListener('focusin', function (evento) {
        const boton = origen(evento);
        if (boton && boton.matches(':focus-visible')) programar(boton);
    });
    document.addEventListener('focusout', function (evento) {
        if (origen(evento)) ocultar();
    });
    document.addEventListener('pointerdown', ocultar, true);
    document.addEventListener('keydown', function (evento) {
        if (evento.key === 'Escape') ocultar();
    }, true);
    window.addEventListener('scroll', ocultar, true);
})();
