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
        evento.preventDefault();
        evento.stopImmediatePropagation();
    }, true);

    const reabrirSiAbandonada = function (ventana, destino) {
        if (!ventana || ventana.contains(destino)) return;
        // Se comprueba en el siguiente frame: en focusout el foco aún no ha llegado
        // a su destino y :hover puede no haberse recalculado.
        requestAnimationFrame(function () {
            if (!ventana.isConnected) return;
            if (ventana.matches(':hover') || ventana.contains(document.activeElement)) return;
            ventana.removeAttribute(CERRADA);
        });
    };

    const cerradaDe = function (evento) {
        return evento.target && evento.target.closest ? evento.target.closest('[' + CERRADA + ']') : null;
    };

    document.addEventListener('pointerout', function (evento) {
        reabrirSiAbandonada(cerradaDe(evento), evento.relatedTarget);
    });
    document.addEventListener('focusout', function (evento) {
        reabrirSiAbandonada(cerradaDe(evento), evento.relatedTarget);
    });
})();
