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
const { resolverReto, bitsCeroIniciales, mensajeValido, atenderMensaje } = new Function(
    readFileSync(ruta, 'utf8') + '\nreturn { resolverReto, bitsCeroIniciales, mensajeValido, atenderMensaje };')();

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

test('el manejador atiende la forma exacta que envía la página y responde con el nonce', async () => {
    let limite;
    const respuesta = await Promise.race([
        new Promise((responder) => {
            assert.equal(atenderMensaje({ data: { reto: VECTOR_RETO, bits: VECTOR_BITS } }, responder, crypto.subtle), true);
        }),
        new Promise((_, fallar) => { limite = setTimeout(() => fallar(new Error('el worker no respondió en 30 s')), 30_000); }),
    ]);
    clearTimeout(limite);
    assert.deepEqual(respuesta, { nonce: VECTOR_NONCE });
});

test('un mensaje con otra forma se ignora sin responder ni buscar', async () => {
    const respuestas = [];
    let llamadasDigest = 0;
    const sutilEspia = { digest: () => { llamadasDigest++; throw new Error('no debía buscar'); } };
    const largo = 'A'.repeat(1025);
    const malos = [
        undefined, null, 'abc', 42, [], ['abc', 8],
        {}, { reto: 'abc' }, { bits: 8 }, { reto: 'abc', bits: 8, extra: true },
        { reto: '', bits: 8 }, { reto: 'a:b', bits: 8 }, { reto: 'a b', bits: 8 }, { reto: 'a=', bits: 8 }, { reto: largo, bits: 8 },
        { reto: 123, bits: 8 }, { reto: 'abc', bits: '8' }, { reto: 'abc', bits: 8.5 }, { reto: 'abc', bits: -1 },
        { reto: 'abc', bits: 33 }, { reto: 'abc', bits: Number.NaN }, { reto: 'abc', bits: Infinity },
    ];
    for (const datos of malos) {
        assert.equal(mensajeValido(datos), false, `aceptó ${JSON.stringify(datos)}`);
        assert.equal(atenderMensaje({ data: datos }, (r) => respuestas.push(r), sutilEspia), false);
    }
    assert.equal(atenderMensaje(undefined, (r) => respuestas.push(r), sutilEspia), false);
    await new Promise((listo) => setTimeout(listo, 20));
    assert.deepEqual(respuestas, [], 'a un mensaje que no encaja no se le responde');
    assert.equal(llamadasDigest, 0, 'ni se empieza a buscar');

    // Defensa en profundidad del validador: postMessage entrega objetos planos (la clonación estructurada no conserva
    // prototipos), así que esto no lo puede enviar la página; se prueba solo el validador.
    const conPrototipo = Object.create({ heredado: 1 });
    conPrototipo.reto = 'abc';
    conPrototipo.bits = 8;
    assert.equal(mensajeValido(conPrototipo), false);
    assert.equal(mensajeValido({ reto: 'A'.repeat(1024), bits: 32 }), true, 'control: los límites incluidos valen');
    assert.equal(mensajeValido({ reto: 'Ab0_-', bits: 0 }), true, 'control: todo el alfabeto base64url vale');
});

// Engancha el worker de verdad a un «self» falso para probar el manejador de message tal cual, origen incluido.
function engancharWorker() {
    const enviados = [];
    const falso = {
        location: { origin: 'https://app.ejemplo.test' },
        crypto: { subtle: crypto.subtle },
        postMessage: (m) => enviados.push(m),
        addEventListener: (tipo, manejador) => { if (tipo === 'message') falso.manejador = manejador; },
    };
    new Function('self', readFileSync(ruta, 'utf8'))(falso);
    return { manejador: falso.manejador, enviados };
}

async function esperarRespuesta(enviados, ms) {
    for (let t = 0; t < ms && enviados.length === 0; t += 10) await new Promise((listo) => setTimeout(listo, 10));
}

test('el manejador ignora un mensaje con un origen ajeno y atiende el vacío del worker dedicado y el propio', async () => {
    const mensaje = { reto: VECTOR_RETO, bits: VECTOR_BITS };

    const ajeno = engancharWorker();
    ajeno.manejador({ origin: 'https://otro.ejemplo.test', data: mensaje });
    await new Promise((listo) => setTimeout(listo, 200));
    assert.deepEqual(ajeno.enviados, [], 'a un origen ajeno no se le responde');

    for (const origen of ['', 'https://app.ejemplo.test']) {
        const propio = engancharWorker();
        propio.manejador({ origin: origen, data: mensaje });
        await esperarRespuesta(propio.enviados, 30_000);
        assert.deepEqual(propio.enviados, [{ nonce: VECTOR_NONCE }], `control: con origen «${origen}» responde`);
    }
});