// Pruebas de src/CaeManager.Web/wwwroot/js/reto-prueba-trabajo-worker.js con el crypto.subtle de Node, sin dependencias.
// Las corre el job deploy-script-tests de ci.yml. El worker es un guion clásico (sin export), así que se evalúa su texto
// en una función y se recogen sus declaraciones; sin «self», no se engancha al evento message.
//
// El vector fijo es el mismo que fija RetoPruebaTrabajoTests en C#: si el worker y ServicioRetoPruebaTrabajo dejaran de
// calcular lo mismo (separador, codificación, orden de bits), uno de los dos lados se pondría en rojo.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const ruta = fileURLToPath(new URL('../../../src/CaeManager.Web/wwwroot/js/reto-prueba-trabajo-worker.js', import.meta.url));
const { resolverReto, bitsCeroIniciales } = new Function(readFileSync(ruta, 'utf8') + '\nreturn { resolverReto, bitsCeroIniciales };')();

const VECTOR_RETO = 'vector-alta-a2';
const VECTOR_BITS = 12;
const VECTOR_NONCE = '2413';

async function ceros(reto, nonce) {
    const resumen = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(reto + ':' + nonce));
    return bitsCeroIniciales(new Uint8Array(resumen));
}

test('el vector compartido con C# da el mismo primer nonce', async () => {
    assert.equal(await resolverReto(VECTOR_RETO, VECTOR_BITS, crypto.subtle), VECTOR_NONCE);
});

test('la solución tiene los bits pedidos y ningún nonce anterior los tiene', async () => {
    const nonce = Number(await resolverReto('otro-reto', 8, crypto.subtle));
    assert.ok(await ceros('otro-reto', nonce) >= 8);
    for (let n = 0; n < nonce; n++) assert.ok(await ceros('otro-reto', n) < 8, `el nonce ${n} ya valía`);
});

test('cuenta los bits cero iniciales por octeto y dentro del primero no nulo', () => {
    assert.equal(bitsCeroIniciales(new Uint8Array([0x80, 0])), 0);
    assert.equal(bitsCeroIniciales(new Uint8Array([0x01, 0xff])), 7);
    assert.equal(bitsCeroIniciales(new Uint8Array([0x00, 0x00, 0x10])), 19);
    assert.equal(bitsCeroIniciales(new Uint8Array([0, 0])), 16);
});

test('un reto vacío o una dificultad fuera de rango se rechazan sin buscar', async () => {
    await assert.rejects(resolverReto('', 8, crypto.subtle));
    await assert.rejects(resolverReto('x', 33, crypto.subtle));
    await assert.rejects(resolverReto('x', Number.NaN, crypto.subtle));
});
