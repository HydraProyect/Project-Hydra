// Web Worker del reto de prueba de trabajo del alta autónoma (ServicioRetoPruebaTrabajo en el servidor).
//
// Busca el primer nonce decimal n = 0, 1, 2… tal que SHA-256(UTF-8(reto + ":" + n)) empiece por al menos «bits»
// bits cero, y lo devuelve como texto. Es el mismo cálculo que ServicioRetoPruebaTrabajo.BitsCeroIniciales.
// Usa crypto.subtle (sin dependencias ni terceros) y pide los hashes por lotes, porque cada digest es una promesa y
// esperar una a una deja la CPU ociosa; dentro del lote se recorre en orden, así que el resultado es el primer nonce
// válido y no depende del tamaño del lote. Corre fuera del hilo principal: la página sigue respondiendo.
//
// Mensaje de entrada: { reto: string, bits: number }. Respuesta: { nonce: string } o { error: true }.
'use strict';

var LOTE = 512;
var BITS_MAXIMOS = 32;

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

if (typeof self !== 'undefined' && typeof self.addEventListener === 'function') {
    self.addEventListener('message', function (evento) {
        var datos = evento.data || {};
        resolverReto(datos.reto, Number(datos.bits), self.crypto.subtle).then(
            function (nonce) { self.postMessage({ nonce: nonce }); },
            function () { self.postMessage({ error: true }); });
    });
}
