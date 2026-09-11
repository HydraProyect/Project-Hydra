// Requisitos de la contraseña nueva y coincidencia con su confirmación, en vivo
// mientras se escribe (RequisitosContrasena.razor y las pantallas de cuenta).
//
// Las reglas repiten las de ReglasContrasena.cs, que a su vez copian las del
// PasswordValidator de Identity: mayúscula, minúscula y dígito solo ASCII. Si
// cambian allí, cambian aquí. Esto solo anuncia: quien decide es el servidor al
// guardar, y sin este guion la lista sigue diciendo la política.
//
// Delegado en document, como microinteracciones.js: las pantallas se sirven en
// renderizado estático y la navegación mejorada cambia el DOM sin ejecutar
// scripts, así que un oyente por campo se perdería o se acumularía.
(function () {
    'use strict';

    var reglas = {
        longitud: function (v, li) { return v.length >= minimo(li); },
        mayuscula: function (v) { return /[A-Z]/.test(v); },
        minuscula: function (v) { return /[a-z]/.test(v); },
        numero: function (v) { return /[0-9]/.test(v); },
        simbolo: function (v) { return /[^A-Za-z0-9]/.test(v); },
        distintos: function (v, li) { return new Set(v.split('')).size >= minimo(li); }
    };

    var pausas = {};

    function minimo(li) { return parseInt(li.getAttribute('data-minimo'), 10) || 0; }

    function evaluarRequisitos(lista, valor) {
        var items = lista.querySelectorAll('li[data-regla]');
        var cumplidos = 0;
        for (var i = 0; i < items.length; i++) {
            var li = items[i];
            var regla = reglas[li.getAttribute('data-regla')];
            // Una regla que este guion no conoce no se marca: mejor quedarse
            // corto que dar por cumplido algo que el servidor va a rechazar.
            var ok = regla ? regla(valor, li) : false;
            li.classList.toggle('acceso-requisito-cumplido', ok);
            var estado = li.querySelector('[data-requisito-estado]');
            if (estado) estado.textContent = ok ? ', cumplido' : ', pendiente';
            if (ok) cumplidos++;
        }
        return { cumplidos: cumplidos, total: items.length };
    }

    function anunciar(idCampo, cuenta) {
        var resumen = document.querySelector('[data-requisitos-resumen="' + idCampo + '"]');
        if (!resumen) return;
        clearTimeout(pausas[idCampo]);
        pausas[idCampo] = setTimeout(function () {
            resumen.textContent = cuenta.cumplidos === cuenta.total
                ? 'La contraseña cumple todos los requisitos.'
                : 'Requisitos cumplidos: ' + cuenta.cumplidos + ' de ' + cuenta.total + '.';
        }, 700);
    }

    // Mientras la confirmación sea el principio de la contraseña no se dice
    // nada: avisar de «no coinciden» a la primera tecla sería ruido.
    function evaluarCoincidencia(aviso) {
        var confirmacion = document.getElementById(aviso.getAttribute('data-coincidencia-de'));
        var original = document.getElementById(aviso.getAttribute('data-coincidencia-con'));
        if (!confirmacion || !original) return;
        var c = confirmacion.value, o = original.value;
        var estado = '';
        if (c.length > 0) {
            if (c === o) estado = 'ok';
            else if (o.indexOf(c) !== 0 || c.length >= o.length) estado = 'error';
        }
        aviso.classList.toggle('acceso-coincidencia-ok', estado === 'ok');
        aviso.classList.toggle('acceso-coincidencia-error', estado === 'error');
        aviso.textContent = estado === 'ok' ? 'Las dos contraseñas coinciden.'
            : estado === 'error' ? 'Las dos contraseñas no coinciden.' : '';
    }

    function coincidenciasDe(idCampo) {
        return document.querySelectorAll('[data-coincidencia-de="' + idCampo + '"], [data-coincidencia-con="' + idCampo + '"]');
    }

    document.addEventListener('input', function (e) {
        var campo = e.target;
        if (!campo || !campo.id) return;
        var lista = document.querySelector('[data-requisitos-de="' + campo.id + '"]');
        if (lista) anunciar(campo.id, evaluarRequisitos(lista, campo.value));
        var avisos = coincidenciasDe(campo.id);
        for (var i = 0; i < avisos.length; i++) evaluarCoincidencia(avisos[i]);
    });

    // Estado de partida (el navegador puede rellenar el campo solo, o volver
    // de un envío fallido con valor). Sin anunciar: nadie ha escrito aún.
    function evaluarTodo() {
        var listas = document.querySelectorAll('[data-requisitos-de]');
        for (var i = 0; i < listas.length; i++) {
            var campo = document.getElementById(listas[i].getAttribute('data-requisitos-de'));
            if (campo) evaluarRequisitos(listas[i], campo.value);
        }
        var avisos = document.querySelectorAll('[data-coincidencia-de]');
        for (var j = 0; j < avisos.length; j++) evaluarCoincidencia(avisos[j]);
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', evaluarTodo);
    else evaluarTodo();

    if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
        window.Blazor.addEventListener('enhancedload', evaluarTodo);
    }
})();
