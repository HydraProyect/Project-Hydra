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
        if (mascara === crudo) return;

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

    window.talvegCampoFecha = { aplicar: aplicar };
})();
