'use strict';

// Pruebas de la issue única de integraciones sin clave: node --test .github/vigilancia/*.test.js
// Las corre el job deploy-script-tests de ci.yml.

const test = require('node:test');
const assert = require('node:assert/strict');
const s = require('./integraciones-sin-clave.js');

const FECHA = '2026-11-05';
const URL = 'https://github.com/HydraProyect/Project-Hydra/actions/runs/1';
const issue = (number, state, body, extra = {}) => ({ number, state, state_reason: null, title: s.TITULO, body, ...extra });
const cuerpoCon = (secretos) =>
  s.cuerpo({ faltan: s.INTEGRACIONES.filter((i) => secretos.includes(i.secreto)), fecha: FECHA, urlEjecucion: URL });

test('evaluar distingue falta, presente y sin medir', () => {
  const r = s.evaluar({ anthropic: 'false', stripe: '' });
  assert.deepEqual(r.faltan.map((i) => i.secreto), ['CI_ANTHROPIC_API_KEY']);
  assert.deepEqual(r.sinMedir.map((i) => i.secreto), ['CI_STRIPE_TEST_API_KEY']);
  const todas = s.evaluar({ anthropic: 'true', stripe: 'true' });
  assert.equal(todas.faltan.length + todas.sinMedir.length, 0);
});

test('sin ninguna issue y con un secreto ausente, crea una con el marcador y el secreto', () => {
  const plan = s.planificar({ existentes: [], medidas: { anthropic: 'false', stripe: 'true' }, fecha: FECHA, urlEjecucion: URL });
  assert.equal(plan.accion, 'crear');
  assert.ok(plan.cuerpo.startsWith(s.MARCADOR));
  assert.match(plan.cuerpo, /CI_ANTHROPIC_API_KEY/);
  assert.doesNotMatch(plan.cuerpo, /CI_STRIPE_TEST_API_KEY/);
});

test('con una abierta, la actualiza en vez de crear otra', () => {
  const existentes = [issue(7, 'open', cuerpoCon(['CI_STRIPE_TEST_API_KEY']))];
  const plan = s.planificar({ existentes, medidas: { anthropic: 'false', stripe: 'false' }, fecha: FECHA, urlEjecucion: URL });
  assert.equal(plan.accion, 'actualizar');
  assert.equal(plan.number, 7);
});

test('con la abierta ya al día, no toca nada', () => {
  const existentes = [issue(7, 'open', cuerpoCon(['CI_ANTHROPIC_API_KEY', 'CI_STRIPE_TEST_API_KEY']))];
  const plan = s.planificar({ existentes, medidas: { anthropic: 'false', stripe: 'false' }, fecha: FECHA, urlEjecucion: URL });
  assert.equal(plan.accion, 'nada');
});

test('dos abiertas: actualiza la más antigua y cierra la otra como duplicada', () => {
  const existentes = [issue(9, 'open', `${s.MARCADOR}\nvieja`), issue(4, 'open', `${s.MARCADOR}\nvieja`)];
  const plan = s.planificar({ existentes, medidas: { anthropic: 'false', stripe: 'true' }, fecha: FECHA, urlEjecucion: URL });
  assert.equal(plan.number, 4);
  assert.deepEqual(plan.duplicadas, [{ number: 9, de: 4 }]);
});

test('solo cerradas: reabre la más reciente; cerrada como no planificada, la respeta', () => {
  const medidas = { anthropic: 'false', stripe: 'true' };
  const reabre = s.planificar({ existentes: [issue(3, 'closed', s.MARCADOR), issue(5, 'closed', s.MARCADOR)], medidas, fecha: FECHA });
  assert.equal(reabre.accion, 'reabrir');
  assert.equal(reabre.number, 5);
  const respeta = s.planificar({
    existentes: [issue(5, 'closed', s.MARCADOR, { state_reason: 'not_planned' })],
    medidas,
    fecha: FECHA,
  });
  assert.equal(respeta.accion, 'silenciada');
});

test('una issue sin el marcador no cuenta como la propia', () => {
  const plan = s.planificar({ existentes: [issue(2, 'open', 'otra cosa')], medidas: { anthropic: 'false', stripe: 'true' }, fecha: FECHA });
  assert.equal(plan.accion, 'crear');
});

test('con las dos claves presentes, cierra la abierta', () => {
  const plan = s.planificar({ existentes: [issue(7, 'open', s.MARCADOR)], medidas: { anthropic: 'true', stripe: 'true' }, fecha: FECHA });
  assert.equal(plan.accion, 'cerrar');
  assert.equal(plan.number, 7);
});

test('si un job no llegó a medir su clave, no cierra nada', () => {
  const plan = s.planificar({ existentes: [issue(7, 'open', s.MARCADOR)], medidas: { anthropic: 'true', stripe: '' }, fecha: FECHA });
  assert.equal(plan.accion, 'nada');
});

test('una URL de ejecución que no es https no entra en el cuerpo', () => {
  const c = s.cuerpo({ faltan: s.INTEGRACIONES, fecha: FECHA, urlEjecucion: 'javascript:alert(1)' });
  assert.doesNotMatch(c, /javascript:/);
});

test('aplicar crea la etiqueta si falta y abre la issue con ella', async () => {
  const llamadas = [];
  const github = {
    rest: {
      issues: {
        getLabel: async () => {
          const e = new Error('no existe');
          e.status = 404;
          throw e;
        },
        createLabel: async (a) => llamadas.push(['createLabel', a.name]),
        create: async (a) => {
          llamadas.push(['create', a.title, a.labels]);
          return { data: { number: 11 } };
        },
      },
    },
  };
  const plan = s.planificar({ existentes: [], medidas: { anthropic: 'false', stripe: 'false' }, fecha: FECHA });
  const n = await s.aplicar({ github, owner: 'o', repo: 'r', plan, fecha: FECHA });
  assert.equal(n, 11);
  assert.deepEqual(llamadas, [
    ['createLabel', s.ETIQUETA],
    ['create', s.TITULO, [s.ETIQUETA]],
  ]);
});
