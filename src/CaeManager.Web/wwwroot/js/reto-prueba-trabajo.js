// Arranca el reto de prueba de trabajo de cada RetoPruebaTrabajoFormulario.razor de la página.
//
// Guion global (App.razor) y no un módulo por página, por lo mismo que acceso-agua.js: la navegación mejorada de
// Blazor parchea el DOM sin reejecutar los <script> de la página, así que se monta al cargar y otra vez en cada
// «enhancedload». Un contenedor se vuelve a arrancar solo si trae un reto distinto del que ya está resolviendo (tras un
// envío rechazado, la página vuelve con otro). En cada montaje se detienen los retos cuyo contenedor ya no está en el
// documento: sin eso, salir del formulario por navegación mejorada dejaría su Web Worker calculando en segundo plano.
//
// Sin interacción de la persona: el Web Worker empieza al pintarse el formulario. Un envío se retiene mientras falte
// la solución o no haya pasado el tiempo mínimo de la trampa de tiempo (data-espera-minima-ms, contado desde que este
// guion arranca, que es después del pintado del servidor), y se repite solo, con el mismo botón, en cuanto se cumplen
// las dos cosas. El estado se escribe en la región role="status" del componente, que el lector de pantalla anuncia sin
// mover el foco.
//
// El reto caduca en el servidor. data-vigencia-ms es ya la vigencia que cuenta este guion, la del servidor menos el
// margen que cubre la latencia entre emitir y pintar (OpcionesAntiabusoAlta.MargenCaducidadGuion). Pasado ese plazo, el
// guion detiene el cálculo, deja de retener y avisa de que hay que recargar la página. El plazo se comprueba con el
// reloj en cada paso (solución, repetición, envío) y no solo con su temporizador: tras una pestaña congelada, los
// temporizadores vencidos pueden dispararse en cualquier orden, y una repetición no puede mandar un reto caducado.
//
// La retención escucha submit en fase de captura sobre window, que corre antes que la captura sobre document de
// acceso-doble-envio.js: un envío retenido no llega a marcarse como «enviando» ni a deshabilitar el botón, y la
// repetición sí pasa por esa guarda como un envío normal. Si el reto falla (sin Worker, sin crypto.subtle) o caduca, la
// retención se retira y el envío sale sin solución válida: el servidor lo rechaza con su respuesta, en vez de dejar el
// formulario mudo.
(function () {
    'use strict';

    var SELECTOR = '[data-reto-prueba-trabajo]';
    var estados = new WeakMap();
    var activos = [];

    function ahora() {
        return (typeof performance !== 'undefined' && performance.now) ? performance.now() : Date.now();
    }

    function arrancar(contenedor) {
        var reto = contenedor.getAttribute('data-reto') || '';
        var previo = estados.get(contenedor);
        if (previo && previo.reto === reto) return;
        if (previo) retirar(previo);

        var campo = contenedor.querySelector('[data-reto-solucion]');
        var aviso = contenedor.querySelector('[data-reto-estado]');
        if (!campo || !aviso) return;

        var bits = parseInt(contenedor.getAttribute('data-bits'), 10);
        var espera = parseInt(contenedor.getAttribute('data-espera-minima-ms'), 10);
        var vigencia = parseInt(contenedor.getAttribute('data-vigencia-ms'), 10);
        var listoDesde = ahora() + (espera > 0 ? espera : 0);
        var caducaEn = vigencia > 0 ? ahora() + vigencia : Infinity;
        // performance.now() puede pararse mientras el equipo está suspendido (Firefox en Linux y macOS); el reloj de
        // pared sigue corriendo, así que el plazo vence con el primero de los dos que lo alcance.
        var caducaEnPared = vigencia > 0 ? Date.now() + vigencia : Infinity;
        var trabajador = null;
        var temporizador = null;
        var temporizadorCaducidad = null;
        campo.value = '';

        var estado = {
            contenedor: contenedor,
            reto: reto,
            resuelto: false,
            fallido: false,
            caducado: false,
            retenido: null,
            puedeSalir: function () {
                comprobarPlazo();
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
                if (temporizadorCaducidad) clearTimeout(temporizadorCaducidad);
                temporizadorCaducidad = null;
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
            if (comprobarPlazo()) return;
            var retenido = estado.retenido;
            var formulario = contenedor.closest('form');
            estado.retenido = null;
            if (!retenido || !formulario) return;
            if (retenido.boton) formulario.requestSubmit(retenido.boton);
            else formulario.requestSubmit();
        }

        function terminar(nonce) {
            if (comprobarPlazo()) return;
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

        function caducar() {
            // Resuelto o no, el reto ya no vale: se para el cálculo y la repetición, y se deja salir el envío.
            estado.detener();
            estado.caducado = true;
            estado.fallido = true;
            avisar('data-texto-caducado');
        }

        // Devuelve true si el reto ya ha caducado, y lo da por caducado la primera vez que lo ve.
        function comprobarPlazo() {
            if (estado.caducado) return true;
            if (ahora() < caducaEn && Date.now() < caducaEnPared) return false;
            caducar();
            return true;
        }

        estados.set(contenedor, estado);
        activos.push(estado);

        if (!reto || !(bits >= 0) || typeof Worker !== 'function') {
            terminar(null);
            return;
        }

        if (vigencia > 0) temporizadorCaducidad = setTimeout(comprobarPlazo, vigencia);

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

    function retirar(estado) {
        estado.detener();
        var i = activos.indexOf(estado);
        if (i >= 0) activos.splice(i, 1);
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
        // Primero se retiran los retos de contenedores que la navegación mejorada ha sacado del documento.
        for (var j = activos.length - 1; j >= 0; j--) {
            if (activos[j].contenedor.isConnected === false) retirar(activos[j]);
        }
        var contenedores = document.querySelectorAll(SELECTOR);
        for (var i = 0; i < contenedores.length; i++) arrancar(contenedores[i]);
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', montar);
    else montar();

    if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
        window.Blazor.addEventListener('enhancedload', montar);
    }
})();
