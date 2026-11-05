'use strict';

// Pruebas de la guarda de caducidad del .trivyignore: node --test .github/vigilancia/*.test.js
// Las corre el job deploy-script-tests de ci.yml.

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const path = require('path');
const t = require('./trivyignore.js');

const HOY = '2026-11-05';

test('una entrada con motivo y caducidad vigente pasa', () => {
  const r = t.validar('# motivo: sin parche en Debian; no se usa apt en ejecución\nCVE-2026-109518 exp:2026-12-01\n', HOY);
  assert.deepEqual(r.errores, []);
  assert.deepEqual(r.entradas.map((e) => [e.id, e.exp]), [['CVE-2026-109518', '2026-12-01']]);
});

test('caduca mañana: vigente; caduca hoy (Trivy ya no la aplica) o ayer: falla', () => {
  assert.deepEqual(t.validar('# motivo: x\nCVE-1 exp:2026-11-06', HOY).errores, []);
  for (const exp of [HOY, '2026-11-04']) {
    const r = t.validar(`# motivo: x\nCVE-2026-1 exp:${exp}`, HOY);
    assert.equal(r.errores.length, 1);
    assert.match(r.errores[0], new RegExp(`caducó el ${exp}`));
  }
});

test('una entrada sin exp: falla', () => {
  const r = t.validar('# motivo: x\nCVE-2026-1\n', HOY);
  assert.match(r.errores.join('\n'), /no tiene la forma/);
});

test('una entrada sin motivo justo encima falla, aunque haya un motivo más arriba', () => {
  assert.match(t.validar('CVE-2026-1 exp:2026-12-01', HOY).errores.join('\n'), /motivo/);
  assert.match(t.validar('# motivo: x\n\nCVE-2026-1 exp:2026-12-01', HOY).errores.join('\n'), /motivo/);
  assert.match(t.validar('# motivo:   \nCVE-2026-1 exp:2026-12-01', HOY).errores.join('\n'), /motivo/);
});

test(`una caducidad a más de ${t.MAX_DIAS} días falla`, () => {
  assert.deepEqual(t.validar('# motivo: x\nCVE-1 exp:2027-02-03', HOY).errores, []);
  assert.match(t.validar('# motivo: x\nCVE-1 exp:2027-02-04', HOY).errores.join('\n'), /a más de 90 días/);
});

test('una fecha que no existe falla', () => {
  assert.match(t.validar('# motivo: x\nCVE-1 exp:2026-02-30', HOY).errores.join('\n'), /no existe/);
});

test('un fichero solo con comentarios y líneas vacías no tiene entradas ni errores', () => {
  assert.deepEqual(t.validar('# cabecera\n\n# más\n', HOY), { entradas: [], errores: [] });
});

test('el .trivyignore real del repositorio pasa la guarda hoy', () => {
  const real = fs.readFileSync(path.join(__dirname, '..', '..', '.trivyignore'), 'utf8');
  const hoy = new Date().toISOString().slice(0, 10);
  assert.deepEqual(t.validar(real, hoy).errores, []);
});

test('principal: código 1 y error si falta el fichero o hay una entrada caducada; 0 si todo vale', () => {
  const salida = [];
  const io = (texto) => ({
    leer: () => {
      if (texto === null) {
        const e = new Error('no');
        e.code = 'ENOENT';
        throw e;
      }
      return texto;
    },
    hoy: HOY,
    log: (m) => salida.push(['log', m]),
    error: (m) => salida.push(['error', m]),
  });
  assert.equal(t.principal([], io(null)), 1);
  assert.equal(t.principal([], io('# motivo: x\nCVE-1 exp:2026-01-01')), 1);
  assert.equal(t.principal([], io('# motivo: x\nCVE-1 exp:2026-12-01')), 0);
  assert.ok(salida.some(([tipo, m]) => tipo === 'error' && /caducó/.test(m)));
});
