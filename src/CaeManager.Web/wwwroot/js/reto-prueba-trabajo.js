// Arranca el reto de prueba de trabajo de cada RetoPruebaTrabajoFormulario.razor de la página.
//
// Guion global (App.razor) y no un módulo por página, por lo mismo que acceso-agua.js: la navegación mejorada de
// Blazor parchea el DOM sin reejecutar los <script> de la página, así que se monta al cargar y otra vez en cada
// «enhancedload». Un contenedor se vuelve a arrancar solo si trae un reto distinto del que ya está resolviendo (tras un
// envío rechazado, la página vuelve con otro).
//
// Sin interacción de la persona: el Web Worker empieza al pintarse el formulario. Un envío se retiene mientras falte
// la solución o no haya pasado el tiempo mínimo de la trampa de tiempo (data-espera-minima-ms, contado desde que este
// guion arranca, que es después del pintado del servidor), y se repite solo, con el mismo botón, en cuanto se cumplen
// las dos cosas. El estado se escribe en la región role="status" del componente, que el lector de pantalla anuncia sin
// mover el foco.
//
// La retención escucha submit en fase de captura sobre window, que corre antes que la captura sobre document de
// acceso-doble-envio.js: un envío retenido no llega a marcarse como «enviando» ni a deshabilitar el botón, y la
// repetición sí pasa por esa guarda como un envío normal. Si el reto falla (sin Worker, sin crypto.subtle), la
// retención se retira y el envío sale sin solución: el servidor lo rechaza con su respuesta, en vez de dejar el
// formulario mudo.
(function () {
    'use strict';

    var SELECTOR = '[data-reto-prueba-trabajo]';
    var estados = new WeakMap();

    function ahora() {
        return (typeof performance !== 'undefined' && performance.now) ? performance.now() : Date.now();
    }

    function arrancar(contenedor) {
        var reto = contenedor.getAttribute('data-reto') || '';
        var previo = estados.get(contenedor);
        if (previo && previo.reto === reto) return;
        if (previo) previo.detener();

        var campo = contenedor.querySelector('[data-reto-solucion]');
        var aviso = contenedor.querySelector('[data-reto-estado]');
        if (!campo || !aviso) return;

        var bits = parseInt(contenedor.getAttribute('data-bits'), 10);
        var espera = parseInt(contenedor.getAttribute('data-espera-minima-ms'), 10);
        var listoDesde = ahora() + (espera > 0 ? espera : 0);
        var trabajador = null;
        var temporizador = null;
        campo.value = '';

        var estado = {
            reto: reto,
            resuelto: false,
            fallido: false,
            retenido: null,
            puedeSalir: function () {
                return estado.fallido || (estado.resuelto && ahora() >= listoDesde);
            },
            retener: function (boton) {
                estado.retenido = { boton: boton || null };
                if (!estado.resuelto) avisar('data-texto-esperando');
                programarRepeticion();
            },
            detener: function () {
                if (trabajador) trabajador.terminate();
                trabajador = null;
                if (temporizador) clearTimeout(temporizador);
                temporizador = null;
                estado.retenido = null;
            }
        };

        function avisar(clave) {
            var texto = contenedor.getAttribute(clave);
            if (texto) aviso.textContent = texto;
        }

        function programarRepeticion() {
            if (!estado.retenido || !estado.resuelto || temporizador) return;
            temporizador = setTimeout(function () {
                temporizador = null;
                repetir();
            }, Math.max(0, listoDesde - ahora()));
        }

        function repetir() {
            var retenido = estado.retenido;
            var formulario = contenedor.closest('form');
            estado.retenido = null;
            if (!retenido || !formulario) return;
            if (retenido.boton) formulario.requestSubmit(retenido.boton);
            else formulario.requestSubmit();
        }

        function terminar(nonce) {
            if (trabajador) trabajador.terminate();
            trabajador = null;
            if (typeof nonce !== 'string') {
                // Sin solución no hay nada que esperar: el envío retenido no se repite solo, la persona ve el error y
                // su siguiente envío sale (y el servidor lo rechaza con su respuesta).
                estado.fallido = true;
                estado.retenido = null;
                avisar('data-texto-error');
                return;
            }
            campo.value = nonce;
            estado.resuelto = true;
            avisar('data-texto-listo');
            programarRepeticion();
        }

        estados.set(contenedor, estado);

        if (!reto || !(bits >= 0) || typeof Worker !== 'function') {
            terminar(null);
            return;
        }

        try {
            trabajador = new Worker(contenedor.getAttribute('data-trabajador'));
        } catch (e) {
            terminar(null);
            return;
        }
        trabajador.onmessage = function (evento) {
            var datos = evento.data || {};
            terminar(typeof datos.nonce === 'string' ? datos.nonce : null);
        };
        trabajador.onerror = function () { terminar(null); };
        trabajador.postMessage({ reto: reto, bits: bits });
    }

    window.addEventListener('submit', function (evento) {
        var formulario = evento.target;
        if (!formulario || typeof formulario.querySelector !== 'function') return;
        var contenedor = formulario.querySelector(SELECTOR);
        var estado = contenedor && estados.get(contenedor);
        if (!estado || estado.puedeSalir()) return;

        evento.preventDefault();
        evento.stopImmediatePropagation();
        estado.retener(evento.submitter);
    }, true);

    function montar() {
        var contenedores = document.querySelectorAll(SELECTOR);
        for (var i = 0; i < contenedores.length; i++) arrancar(contenedores[i]);
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', montar);
    else montar();

    if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
        window.Blazor.addEventListener('enhancedload', montar);
    }
})();
