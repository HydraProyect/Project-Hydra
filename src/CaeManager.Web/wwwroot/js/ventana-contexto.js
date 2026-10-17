// Esc cierra la VentanaContexto abierta (WCAG 1.4.13: el contenido que aparece con
// hover o foco se puede descartar sin mover el cursor ni el foco). El panel se abre
// por CSS (:hover / :focus-within), así que Blazor no sabe cuál está abierta: se
// resuelve aquí, delegado y sin marcado en línea por la CSP, como tooltip.js.
//
// La ventana cerrada con Esc lleva `data-ventana-cerrada` y sigue así hasta que el
// cursor Y el foco la han abandonado; entonces vuelve a abrirse con normalidad.
(function () {
    const SELECTOR = '.ventana-contexto:not(.ventana-contexto-inerte)';
    const CERRADA = 'data-ventana-cerrada';

    // La más interna que esté abierta: la que contiene el foco o, si no, la que
    // tiene el cursor encima.
    const abierta = function () {
        const conFoco = document.activeElement && document.activeElement.closest
            ? document.activeElement.closest(SELECTOR)
            : null;
        if (conFoco && !conFoco.hasAttribute(CERRADA)) return conFoco;
        const bajoCursor = document.querySelectorAll(SELECTOR + ':hover:not([' + CERRADA + '])');
        return bajoCursor.length ? bajoCursor[bajoCursor.length - 1] : null;
    };

    // En `window` y en captura: va antes que el Esc del panel lateral, del Drawer o
    // de la lista, que cuelgan de `document`. Un Esc cierra una sola cosa.
    window.addEventListener('keydown', function (evento) {
        if (evento.key !== 'Escape' || evento.defaultPrevented) return;
        const ventana = abierta();
        if (!ventana) return;

        // Si el foco estaba en un botón del panel, al ocultarlo se perdería: vuelve
        // al disparador (el botón en modo interactivo, el contenedor si no).
        const panel = ventana.querySelector(':scope > .ventana-contexto-panel');
        if (panel && panel.contains(document.activeElement)) {
            const disparador = ventana.querySelector(':scope > .ventana-contexto-disparador') || ventana;
            disparador.focus();
        }
        ventana.setAttribute(CERRADA, '');
        sincronizarExpandida(ventana);
        evento.preventDefault();
        evento.stopImmediatePropagation();
    }, true);

    // aria-expanded del disparador de una ventana interactiva: el panel se abre por CSS (puntero o
    // foco) y Blazor no lo sabe, así que el atributo lo mantiene este script con la misma condición
    // que el CSS —cursor encima o foco dentro— y descontando el cierre por Esc o por el botón de la hoja.
    const sincronizarExpandida = function (ventana) {
        if (!ventana || !ventana.isConnected) return;
        const disparador = ventana.querySelector(':scope > .ventana-contexto-disparador');
        if (!disparador) return;
        const abierta = !ventana.hasAttribute(CERRADA)
            && (ventana.matches(':hover') || ventana.contains(document.activeElement));
        disparador.setAttribute('aria-expanded', abierta ? 'true' : 'false');
    };

    const ventanaDe = function (evento) {
        return evento.target && evento.target.closest ? evento.target.closest(SELECTOR) : null;
    };

    // Botón de cierre de la hoja inferior de móvil (data-ventana-cerrar): marca la ventana como cerrada
    // igual que Esc, y devuelve el foco al disparador para no perderlo al ocultarse el panel.
    document.addEventListener('click', function (evento) {
        const boton = evento.target && evento.target.closest ? evento.target.closest('[data-ventana-cerrar]') : null;
        const ventana = boton ? boton.closest(SELECTOR) : null;
        if (!ventana) return;
        const disparador = ventana.querySelector(':scope > .ventana-contexto-disparador') || ventana;
        disparador.focus();
        ventana.setAttribute(CERRADA, '');
        sincronizarExpandida(ventana);
    });

    // Volver a tocar el disparador de una ventana cerrada con la × (o con Esc) la reabre. En táctil el hover queda
    // pegado y el foco sigue dentro, así que ni pointerout ni focusout llegan a quitar la marca de cerrada.
    document.addEventListener('click', function (evento) {
        const disparador = evento.target && evento.target.closest ? evento.target.closest('.ventana-contexto-disparador') : null;
        const ventana = disparador ? disparador.closest(SELECTOR) : null;
        if (!ventana || !ventana.hasAttribute(CERRADA)) return;
        ventana.removeAttribute(CERRADA);
        sincronizarExpandida(ventana);
    });

    const reabrirSiAbandonada = function (ventana, destino) {
        if (!ventana || ventana.contains(destino)) return;
        // Se comprueba en el siguiente frame: en focusout el foco aún no ha llegado
        // a su destino y :hover puede no haberse recalculado.
        requestAnimationFrame(function () {
            if (!ventana.isConnected) return;
            if (ventana.matches(':hover') || ventana.contains(document.activeElement)) return;
            ventana.removeAttribute(CERRADA);
            sincronizarExpandida(ventana);
        });
    };

    const cerradaDe = function (evento) {
        return evento.target && evento.target.closest ? evento.target.closest('[' + CERRADA + ']') : null;
    };

    // Tras cada entrada o salida de puntero o foco se recalcula aria-expanded en el siguiente frame:
    // :hover y document.activeElement aún no reflejan el destino cuando el evento se dispara.
    const sincronizarTrasEvento = function (evento) {
        const ventana = ventanaDe(evento);
        if (!ventana) return;
        requestAnimationFrame(function () { sincronizarExpandida(ventana); });
    };

    document.addEventListener('pointerover', sincronizarTrasEvento);
    document.addEventListener('focusin', sincronizarTrasEvento);
    document.addEventListener('pointerout', function (evento) {
        reabrirSiAbandonada(cerradaDe(evento), evento.relatedTarget);
        sincronizarTrasEvento(evento);
    });
    document.addEventListener('focusout', function (evento) {
        reabrirSiAbandonada(cerradaDe(evento), evento.relatedTarget);
        sincronizarTrasEvento(evento);
    });
})();
