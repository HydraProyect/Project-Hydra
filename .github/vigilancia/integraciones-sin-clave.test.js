'use strict';

// Pruebas de la issue única de integraciones sin clave: node --test .github/vigilancia/*.test.js
// Las corre el job deploy-script-tests de ci.yml.

const test = require('node:test');
const assert = require('node:assert/strict');
const s = require('./integraciones-sin-clave.js');

const FECHA = '2026-11-05';
const URL = 'https://github.com/HydraProyect/Project-Hydra/actions/runs/1';
const issue = (number, state, body, extra = {}) => ({ number, state, state_reason: null, title: s.TITULO, body, ...extra });
const porSecreto = (secretos) => s.INTEGRACIONES.filter((i) => secretos.includes(i.secreto));
const cuerpoCon = (faltan, sinMedir = []) => s.cuerpo({ faltan: porSecreto(faltan), sinMedir: porSecreto(sinMedir) });

// Cliente de Octokit simulado que registra cada llamada.
function clienteSimulado({ etiquetaExiste = true, numeroNuevo = 11 } = {}) {
  const llamadas = [];
  const github = {
    paginate: async (metodo, parametros) => {
      llamadas.push(['paginate', parametros]);
      return metodo(parametros);
    },
    rest: {
      issues: {
        listForRepo: () => [],
        getLabel: async () => {
          if (etiquetaExiste) return {};
          const e = new Error('no existe');
          e.status = 404;
          throw e;
        },
        createLabel: async (a) => llamadas.push(['createLabel', a.name]),
        create: async (a) => {
          llamadas.push(['create', a.title, a.labels]);
          return { data: { number: numeroNuevo } };
        },
        update: async (a) => llamadas.push(['update', a.issue_number, a.state || null]),
        createComment: async (a) => llamadas.push(['comment', a.issue_number, a.body]),
      },
    },
  };
  return { github, llamadas };
}

test('evaluar distingue falta, presente y sin medir', () => {
  const r = s.evaluar({ anthropic: 'false', stripe: '' });
  assert.deepEqual(r.faltan.map((i) => i.secreto), ['CI_ANTHROPIC_API_KEY']);
  assert.deepEqual(r.sinMedir.map((i) => i.secreto), ['CI_STRIPE_TEST_API_KEY']);
  const todas = s.evaluar({ anthropic: 'true', stripe: 'true' });
  assert.equal(todas.faltan.length + todas.sinMedir.length, 0);
});

test('sin ninguna issue y con un secreto ausente, crea una con el marcador y el secreto', () => {
  const plan = s.planificar({ existentes: [], medidas: { anthropic: 'false', stripe: 'true' } });
  assert.equal(plan.accion, 'crear');
  assert.ok(plan.cuerpo.startsWith(s.MARCADOR));
  assert.match(plan.cuerpo, /CI_ANTHROPIC_API_KEY/);
  assert.doesNotMatch(plan.cuerpo, /CI_STRIPE_TEST_API_KEY/);
});

test('un secreto sin medir no desaparece del cuerpo: se declara sin medir', () => {
  const plan = s.planificar({ existentes: [], medidas: { anthropic: 'false', stripe: '' } });
  assert.match(plan.cuerpo, /`CI_STRIPE_TEST_API_KEY` \| sin medir/);
  assert.match(plan.cuerpo, /`CI_ANTHROPIC_API_KEY` \| falta/);
});

test('con una abierta y otro estado, la actualiza en vez de crear otra', () => {
  const existentes = [issue(7, 'open', cuerpoCon(['CI_STRIPE_TEST_API_KEY']))];
  const plan = s.planificar({ existentes, medidas: { anthropic: 'false', stripe: 'false' } });
  assert.equal(plan.accion, 'actualizar');
  assert.equal(plan.number, 7);
});

test('el cuerpo es estable: con el mismo estado no se edita la issue, sea cual sea el día', () => {
  const existentes = [issue(7, 'open', cuerpoCon(['CI_ANTHROPIC_API_KEY', 'CI_STRIPE_TEST_API_KEY']))];
  const plan = s.planificar({ existentes, medidas: { anthropic: 'false', stripe: 'false' } });
  assert.equal(plan.accion, 'nada');
  assert.doesNotMatch(plan.cuerpo, /\d{4}-\d{2}-\d{2}/);
});

test('dos abiertas: actualiza la más antigua y cierra la otra como duplicada', () => {
  const existentes = [issue(9, 'open', `${s.MARCADOR}\nvieja`), issue(4, 'open', `${s.MARCADOR}\nvieja`)];
  const plan = s.planificar({ existentes, medidas: { anthropic: 'false', stripe: 'true' } });
  assert.equal(plan.number, 4);
  assert.deepEqual(plan.duplicadas, [{ number: 9, de: 4 }]);
});

test('solo cerradas: reabre la más reciente', () => {
  const plan = s.planificar({
    existentes: [issue(3, 'closed', s.MARCADOR), issue(5, 'closed', s.MARCADOR)],
    medidas: { anthropic: 'false', stripe: 'true' },
  });
  assert.equal(plan.accion, 'reabrir');
  assert.equal(plan.number, 5);
});

test('cualquier cerrada como no planificada silencia, aunque otra más reciente sea una duplicada completada', () => {
  const plan = s.planificar({
    existentes: [issue(4, 'closed', s.MARCADOR, { state_reason: 'not_planned' }), issue(9, 'closed', s.MARCADOR, { state_reason: 'completed' })],
    medidas: { anthropic: 'false', stripe: 'true' },
  });
  assert.equal(plan.accion, 'silenciada');
  assert.equal(plan.number, 4);
});

test('una issue sin el marcador no cuenta como la propia', () => {
  const plan = s.planificar({ existentes: [issue(2, 'open', 'otra cosa')], medidas: { anthropic: 'false', stripe: 'true' } });
  assert.equal(plan.accion, 'crear');
});

test('con las dos claves presentes, cierra la abierta', () => {
  const plan = s.planificar({ existentes: [issue(7, 'open', s.MARCADOR)], medidas: { anthropic: 'true', stripe: 'true' } });
  assert.equal(plan.accion, 'cerrar');
  assert.equal(plan.number, 7);
});

test('si un job no llegó a medir su clave, no cierra nada', () => {
  const plan = s.planificar({ existentes: [issue(7, 'open', s.MARCADOR)], medidas: { anthropic: 'true', stripe: '' } });
  assert.equal(plan.accion, 'nada');
});

test('listarExistentes solo pide las issues con la etiqueta, abiertas y cerradas, y descarta las PR', async () => {
  const { github, llamadas } = clienteSimulado();
  github.rest.issues.listForRepo = () => [
    { number: 1, state: 'open', title: s.TITULO, body: s.MARCADOR },
    { number: 2, state: 'open', title: 'PR', body: s.MARCADOR, pull_request: {} },
  ];
  const existentes = await s.listarExistentes({ github, owner: 'o', repo: 'r' });
  assert.deepEqual(existentes.map((i) => i.number), [1]);
  const [, parametros] = llamadas.find((l) => l[0] === 'paginate');
  assert.equal(parametros.labels, s.ETIQUETA);
  assert.equal(parametros.state, 'all');
});

test('aplicar crea la etiqueta si falta, abre la issue con ella y anota fecha y ejecución en un comentario', async () => {
  const { github, llamadas } = clienteSimulado({ etiquetaExiste: false });
  const plan = s.planificar({ existentes: [], medidas: { anthropic: 'false', stripe: 'false' } });
  const n = await s.aplicar({ github, owner: 'o', repo: 'r', plan, fecha: FECHA, urlEjecucion: URL });
  assert.equal(n, 11);
  assert.deepEqual(llamadas.slice(0, 2), [
    ['createLabel', s.ETIQUETA],
    ['create', s.TITULO, [s.ETIQUETA]],
  ]);
  assert.equal(llamadas[2][0], 'comment');
  assert.match(llamadas[2][2], new RegExp(`${FECHA}.*${URL.replace(/[/.]/g, '\\$&')}`));
});

test('una URL de ejecución que no es https no entra en el comentario', async () => {
  const { github, llamadas } = clienteSimulado();
  const plan = s.planificar({ existentes: [], medidas: { anthropic: 'false', stripe: 'false' } });
  await s.aplicar({ github, owner: 'o', repo: 'r', plan, fecha: FECHA, urlEjecucion: 'javascript:alert(1)' });
  assert.ok(llamadas.filter((l) => l[0] === 'comment').every((l) => !l[2].includes('javascript:')));
});
