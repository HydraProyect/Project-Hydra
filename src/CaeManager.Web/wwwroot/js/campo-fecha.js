// Máscara dd/mm/aaaa de CampoFecha, en el navegador para que el texto y el cursor no esperen la ida y vuelta al
// servidor. Es el mismo algoritmo que MascaraFecha.Aplicar (C#): si los dos coinciden, el servidor no reescribe el
// valor y no hay carrera entre pulsaciones. Se escucha en captura sobre document para ejecutar antes que la delegación
// de eventos de Blazor (que lee e.target.value) y para servir también a los inputs que Blazor recrea.
(function () {
    'use strict';

    var ISO = /^\s*(\d{4})-(\d{2})-(\d{2})\s*$/;
    var SEPARADORES = '/-. ';

    function aplicar(texto) {
        var iso = ISO.exec(texto || '');
        if (iso) return iso[3] + '/' + iso[2] + '/' + iso[1];

        var grupos = ['', '', ''];
        var i = 0;
        for (var k = 0; k < (texto || '').length; k++) {
            var c = texto.charAt(k);
            if (c >= '0' && c <= '9') {
                if (grupos[i].length === (i < 2 ? 2 : 4)) {
                    if (i === 2) continue;
                    i++;
                }
                grupos[i] += c;
            } else if (SEPARADORES.indexOf(c) >= 0 && i < 2 && grupos[i].length > 0) {
                // Como en C#: el cero solo con la barra recién tecleada al final, no al corregir en medio.
                if (grupos[i].length === 1 && k === texto.length - 1) grupos[i] = '0' + grupos[i];
                i++;
            }
        }

        var resultado = grupos[0];
        if (i >= 1) resultado += '/' + grupos[1];
        if (i >= 2) resultado += '/' + grupos[2];
        return resultado;
    }

    function cifrasAntes(texto, posicion) {
        var n = 0;
        for (var k = 0; k < posicion && k < texto.length; k++) {
            var c = texto.charAt(k);
            if (c >= '0' && c <= '9') n++;
        }
        return n;
    }

    function posicionTrasCifras(texto, cifras) {
        if (cifras === 0) return 0;
        var n = 0;
        for (var k = 0; k < texto.length; k++) {
            var c = texto.charAt(k);
            if (c >= '0' && c <= '9' && ++n === cifras) return k + 1;
        }
        return texto.length;
    }

    document.addEventListener('input', function (e) {
        var campo = e.target;
        if (!campo || !campo.matches || !campo.matches('input[data-campo-fecha]')) return;

        var crudo = campo.value;
        var mascara = aplicar(crudo);
        // Un texto sin una sola cifra («mañana» pegado sobre la fecha) no se vacía: dejarlo en «» haría creer al servidor
        // que el campo se borró a propósito («sin fecha», «sin fin»). Se queda a la vista y el servidor lo da por no válido.
        if (mascara === crudo || mascara === '') return;

        var cursor = campo.selectionStart == null ? crudo.length : campo.selectionStart;
        var alFinal = cursor >= crudo.length;
        campo.value = mascara;
        var nuevo = alFinal ? mascara.length : posicionTrasCifras(mascara, cifrasAntes(crudo, cursor));
        try { campo.setSelectionRange(nuevo, nuevo); } catch (_) { /* tipos sin selección */ }
    }, true);

    // En la rejilla del calendario las flechas mueven el día enfocado (lo decide el servidor); aquí solo se evita que
    // además desplacen la página.
    var TECLAS_REJILLA = ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End', 'PageUp', 'PageDown'];
    document.addEventListener('keydown', function (e) {
        var objetivo = e.target;
        if (objetivo && objetivo.closest && objetivo.closest('.campo-fecha-rejilla') && TECLAS_REJILLA.indexOf(e.key) >= 0) {
            e.preventDefault();
        }
    }, true);

    // Calendario en la capa superior del navegador (Popover API, mismo criterio que campo-select-avanzado.js): así no
    // queda bajo la cabecera ni el lateral, ni lo recorta el overflow de un Drawer o un Modal. Se coloca bajo el campo,
    // alineado a su borde derecho, y encima si abajo no cabe. Sin Popover API se queda en la posición absoluta del CSS.
    var MARGEN = 8;
    var HUECO = 4;
    var anclajes = {};

    function colocar(panel, fila) {
        var caja = fila.getBoundingClientRect();
        var anchoVentana = window.innerWidth;
        var altoVentana = window.innerHeight;
        var ancho = Math.min(panel.offsetWidth, anchoVentana - 2 * MARGEN);
        var izquierda = Math.min(Math.max(caja.right - ancho, MARGEN), anchoVentana - ancho - MARGEN);
        var alto = panel.offsetHeight;
        var abajo = altoVentana - caja.bottom - MARGEN - HUECO;
        var arriba = caja.top - MARGEN - HUECO;
        var voltear = alto > abajo && arriba > abajo;

        panel.style.position = 'fixed';
        panel.style.margin = '0';
        panel.style.right = 'auto';
        panel.style.bottom = 'auto';
        panel.style.left = izquierda + 'px';
        panel.style.top = (voltear ? Math.max(caja.top - HUECO - alto, MARGEN) : caja.bottom + HUECO) + 'px';
        panel.dataset.lado = voltear ? 'arriba' : 'abajo';
    }

    function soltar(id) {
        var anclaje = anclajes[id];
        if (!anclaje) return;
        delete anclajes[id];
        anclaje();
    }

    function anclar(panel, fila, id) {
        soltar(id);
        if (!panel || !fila) return;
        if (typeof panel.showPopover === 'function') {
            panel.setAttribute('popover', 'manual');
            try { panel.showPopover(); } catch (_) { /* ya visible */ }
        } else {
            return;
        }
        colocar(panel, fila);

        var cuadro = 0;
        var recolocar = function () {
            cancelAnimationFrame(cuadro);
            cuadro = requestAnimationFrame(function () {
                // Si Blazor quitó el calendario sin pasar por soltar (el componente se desechó abierto), se limpia solo.
                if (!panel.isConnected) { soltar(id); return; }
                colocar(panel, fila);
            });
        };
        window.addEventListener('resize', recolocar);
        window.addEventListener('scroll', recolocar, true);
        // Cambiar de mes o de año vuelve a dibujar la rejilla y su altura cambia (5 o 6 filas): con el panel volteado hacia
        // arriba, el borde de abajo se montaría sobre el campo. Cada redibujado del panel lo coloca de nuevo.
        var observador = null;
        if (typeof MutationObserver === 'function') {
            observador = new MutationObserver(recolocar);
            observador.observe(panel, { childList: true, subtree: true, characterData: true });
        }
        anclajes[id] = function () {
            cancelAnimationFrame(cuadro);
            window.removeEventListener('resize', recolocar);
            window.removeEventListener('scroll', recolocar, true);
            if (observador) observador.disconnect();
        };
    }

    window.talvegCampoFecha = { aplicar: aplicar, anclar: anclar, soltar: soltar };
})();
