// Web Worker del reto de prueba de trabajo del alta autónoma (ServicioRetoPruebaTrabajo en el servidor).
//
// Busca el primer nonce decimal n = 0, 1, 2… tal que SHA-256(UTF-8(reto + ":" + n)) empiece por al menos «bits»
// bits cero, y lo devuelve como texto. Es el mismo cálculo que ServicioRetoPruebaTrabajo.BitsCeroIniciales.
// Usa crypto.subtle (sin dependencias ni terceros) y pide los hashes por lotes, porque cada digest es una promesa y
// esperar una a una deja la CPU ociosa; dentro del lote se recorre en orden, así que el resultado es el primer nonce
// válido y no depende del tamaño del lote. Corre fuera del hilo principal: la página sigue respondiendo.
//
// Mensaje de entrada: { reto: string, bits: number }, con la forma que exige mensajeValido; cualquier otro se ignora sin
// responder. Respuesta: { nonce: string } o { error: true }.
'use strict';

var LOTE = 512;
var BITS_MAXIMOS = 32;
var LONGITUD_MAXIMA_RETO = 1024;
var FORMA_RETO = /^[A-Za-z0-9_-]+$/;

function bitsCeroIniciales(octetos) {
    var ceros = 0;
    for (var i = 0; i < octetos.length; i++) {
        if (octetos[i] === 0) {
            ceros += 8;
            continue;
        }
        return ceros + Math.clz32(octetos[i]) - 24;
    }
    return ceros;
}

async function resolverReto(reto, bits, sutil) {
    if (typeof reto !== 'string' || reto.length === 0 || !(bits >= 0 && bits <= BITS_MAXIMOS)) {
        throw new Error('reto no válido');
    }
    var codificador = new TextEncoder();
    var prefijo = reto + ':';
    for (var base = 0; base < Number.MAX_SAFE_INTEGER; base += LOTE) {
        var pendientes = new Array(LOTE);
        for (var i = 0; i < LOTE; i++) {
            pendientes[i] = sutil.digest('SHA-256', codificador.encode(prefijo + (base + i)));
        }
        var resumenes = await Promise.all(pendientes);
        for (var j = 0; j < LOTE; j++) {
            if (bitsCeroIniciales(new Uint8Array(resumenes[j])) >= bits) {
                return String(base + j);
            }
        }
    }
    throw new Error('sin solución');
}

// Valida la forma exacta del mensaje que envía reto-prueba-trabajo.js: un objeto plano con solo «reto» (el token
// base64url del servidor, de 1 a 1024 caracteres) y «bits» (entero de 0 a BITS_MAXIMOS). A un worker dedicado solo le
// puede escribir la página que lo creó, y su event.origin llega vacío, así que comprobar el origen no aporta nada; lo que
// sí acota el manejador es no atender nada que no tenga esa forma.
function mensajeValido(datos) {
    if (datos === null || typeof datos !== 'object' || Object.getPrototypeOf(datos) !== Object.prototype) {
        return false;
    }
    var claves = Object.keys(datos);
    if (claves.length !== 2 || !Object.prototype.hasOwnProperty.call(datos, 'reto')
        || !Object.prototype.hasOwnProperty.call(datos, 'bits')) {
        return false;
    }
    return typeof datos.reto === 'string' && datos.reto.length <= LONGITUD_MAXIMA_RETO && FORMA_RETO.test(datos.reto)
        && typeof datos.bits === 'number' && Number.isInteger(datos.bits) && datos.bits >= 0 && datos.bits <= BITS_MAXIMOS;
}

// Un mensaje que no encaja se ignora sin responder: no hay error que devolver a quien no es la página.
function atenderMensaje(evento, responder, sutil) {
    var datos = evento ? evento.data : undefined;
    if (!mensajeValido(datos)) {
        return false;
    }
    resolverReto(datos.reto, datos.bits, sutil).then(
        function (nonce) { responder({ nonce: nonce }); },
        function () { responder({ error: true }); });
    return true;
}

if (typeof self !== 'undefined' && typeof self.addEventListener === 'function') {
    self.addEventListener('message', function (evento) {
        atenderMensaje(evento, function (respuesta) { self.postMessage(respuesta); }, self.crypto.subtle);
    });
}