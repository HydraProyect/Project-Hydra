'use strict';

// Pruebas de la conciliación de alertas: node --test .github/vigilancia/*.test.js
// Las corre el job deploy-script-tests de ci.yml.

const test = require('node:test');
const assert = require('node:assert/strict');
const v = require('./alertas.js');

const informe = (vulns, target = 'imagen (debian 12)') => ({
  SchemaVersion: 2,
  ArtifactName: 'x',
  Results: [{ Target: target, Vulnerabilities: vulns }],
});

const vuln = (id, sev, extra = {}) => ({
  VulnerabilityID: id,
  Severity: sev,
  PkgName: 'openssl',
  InstalledVersion: '3.0.1',
  FixedVersion: '3.0.2',
  Title: `Título de ${id}`,
  PrimaryURL: `https://avd.aquasec.com/nvd/${id.toLowerCase()}`,
  ...extra,
});

test('el marcador identifica capa y clave y no confunde capas', () => {
  const cuerpo = `texto\n${v.marcador('imagenes', 'CVE-2026-0001')}\nmás`;
  assert.equal(v.claveDe(cuerpo, 'imagenes'), 'CVE-2026-0001');
  assert.equal(v.claveDe(cuerpo, 'kev'), null);
  assert.equal(v.claveDe('sin marcador', 'imagenes'), null);
  assert.throws(() => v.marcador('Imagenes', 'CVE-1'));
  assert.throws(() => v.marcador('imagenes', 'CVE 1 -->'));
});

test('un informe sin SchemaVersion no se lee como «sin hallazgos»', () => {
  assert.throws(() => v.agruparTrivy([{ imagen: 'caddy', informe: {} }]), /no es un informe JSON de Trivy/);
  assert.throws(() => v.agruparTrivy([{ imagen: 'caddy', informe: null }]));
  assert.throws(() => v.agruparTrivy([{ imagen: 'caddy', informe: { SchemaVersion: 2, Results: 'x' } }]));
  // Un informe válido sin Results (imagen sin paquetes) sí es «sin hallazgos».
  assert.equal(v.agruparTrivy([{ imagen: 'caddy', informe: { SchemaVersion: 2 } }]).size, 0);
});

test('agrupa por CVE entre imágenes, toma la severidad mayor y descarta lo no alertable', () => {
  const porCve = v.agruparTrivy([
    { imagen: 'caddy', informe: informe([vuln('CVE-2026-0001', 'HIGH'), vuln('CVE-2026-0009', 'MEDIUM')]) },
    {
      imagen: 'aspnet',
      informe: informe([
        vuln('CVE-2026-0001', 'CRITICAL', { PkgName: 'libssl3' }),
        vuln('CVE-2026-0002', 'HIGH', { FixedVersion: '' }),
      ]),
    },
  ]);
  assert.deepEqual([...porCve.keys()], ['CVE-2026-0001']);
  const cve = porCve.get('CVE-2026-0001');
  assert.equal(cve.severidad, 'CRITICAL');
  assert.deepEqual(cve.afectados.map((a) => a.imagen), ['aspnet', 'caddy']);
});

test('el cuerpo es determinista, escapa texto ajeno y la CRITICAL se marca', () => {
  const porCve = v.agruparTrivy([
    { imagen: 'seq', informe: informe([vuln('CVE-2026-0003', 'CRITICAL', { Title: 'a | b <script>', PrimaryURL: 'javascript:alert(1)' })]) },
  ]);
  const a1 = v.alertaDeImagenes(porCve.get('CVE-2026-0003'));
  const a2 = v.alertaDeImagenes(porCve.get('CVE-2026-0003'));
  assert.equal(a1.cuerpo, a2.cuerpo);
  assert.equal(a1.critica, true);
  assert.equal(v.claveDe(a1.cuerpo, 'imagenes'), 'CVE-2026-0003');
  assert.ok(!a1.cuerpo.includes('<script>'));
  assert.ok(!a1.cuerpo.includes('javascript:'));
  assert.ok(a1.cuerpo.includes('a &#124; b &lt;script&gt;'));
  assert.match(a1.titulo, /^\[Vigilancia imágenes\] CVE-2026-0003 \(CRITICAL\) en seq$/);
});

const alerta = (clave, cuerpo = `${v.marcador('imagenes', clave)}\ncuerpo`) => ({ clave, titulo: `t ${clave}`, cuerpo, critica: false });
const issue = (number, state, clave, extra = {}) => ({
  number,
  state,
  title: `t ${clave}`,
  body: `${v.marcador('imagenes', clave)}\ncuerpo`,
  ...extra,
});

test('crea lo nuevo, no toca lo igual, actualiza lo cambiado y reabre lo cerrado', () => {
  const plan = v.planificar({
    capa: 'imagenes',
    existentes: [issue(10, 'open', 'CVE-A'), issue(11, 'open', 'CVE-B', { title: 'viejo' }), issue(12, 'closed', 'CVE-C'), issue(13, 'closed', 'CVE-C')],
    alertas: [alerta('CVE-A'), alerta('CVE-B'), alerta('CVE-C'), alerta('CVE-D')],
    completo: true,
    maxNuevas: 10,
  });
  assert.deepEqual(plan.crear.map((x) => x.alerta.clave), ['CVE-D']);
  assert.deepEqual(plan.actualizar.map((x) => x.number), [11]);
  assert.deepEqual(plan.reabrir.map((x) => x.number), [13]);
  assert.deepEqual(plan.cerrar, []);
});

test('cierra lo que desaparece solo si el escaneo es completo', () => {
  const base = { capa: 'imagenes', existentes: [issue(20, 'open', 'CVE-X'), issue(21, 'closed', 'CVE-Y')], alertas: [], maxNuevas: 10 };
  assert.deepEqual(v.planificar({ ...base, completo: true }).cerrar.map((x) => x.number), [20]);
  const incompleto = v.planificar({ ...base, completo: false });
  assert.deepEqual(incompleto.cerrar, []);
  assert.deepEqual(incompleto.conservadas.map((x) => x.number), [20]);
});

test('ignora issues sin marcador de la capa y cierra duplicadas abiertas', () => {
  const plan = v.planificar({
    capa: 'imagenes',
    existentes: [
      { number: 1, state: 'open', title: 'otra', body: 'sin marcador' },
      { number: 2, state: 'open', title: 'kev', body: v.marcador('kev', 'CVE-K') },
      issue(5, 'open', 'CVE-A'),
      issue(3, 'open', 'CVE-A'),
    ],
    alertas: [alerta('CVE-A')],
    completo: true,
    maxNuevas: 10,
  });
  assert.deepEqual(plan.cerrar, []);
  assert.deepEqual(plan.duplicadas, [{ number: 5, de: 3 }]);
});

test('respeta el tope de issues nuevas por ejecución', () => {
  const plan = v.planificar({
    capa: 'imagenes',
    existentes: [],
    alertas: ['CVE-1', 'CVE-2', 'CVE-3'].map((c) => alerta(c)),
    completo: true,
    maxNuevas: 2,
  });
  assert.equal(plan.crear.length, 2);
  assert.deepEqual(plan.aplazadas.map((x) => x.alerta.clave), ['CVE-3']);
});

test('en modo simular no llama a la API', async () => {
  const llamadas = [];
  const github = new Proxy({}, { get: () => new Proxy({}, { get: () => new Proxy({}, { get: (_, m) => () => llamadas.push(m) }) }) });
  const plan = v.planificar({ capa: 'imagenes', existentes: [issue(1, 'open', 'CVE-Z')], alertas: [alerta('CVE-N')], completo: true, maxNuevas: 5 });
  const errores = await v.aplicar({ github, owner: 'o', repo: 'r', plan, motivoCierre: 'm', simular: true });
  assert.deepEqual(errores, []);
  assert.deepEqual(llamadas, []);
});

test('aplicar cierra con comentario y sigue tras un fallo', async () => {
  const hechas = [];
  const github = {
    rest: {
      issues: {
        getLabel: async () => ({}),
        create: async ({ title }) => {
          if (title === 't CVE-MAL') throw Object.assign(new Error('boom'), { status: 422 });
          hechas.push(`crear ${title}`);
        },
        update: async ({ issue_number, state }) => hechas.push(`update #${issue_number} ${state || ''}`.trim()),
        createComment: async ({ issue_number }) => hechas.push(`comentar #${issue_number}`),
      },
    },
  };
  const plan = v.planificar({
    capa: 'imagenes',
    existentes: [issue(7, 'open', 'CVE-VIEJA')],
    alertas: [alerta('CVE-MAL'), alerta('CVE-BIEN')],
    completo: true,
    maxNuevas: 5,
  });
  const errores = await v.aplicar({ github, owner: 'o', repo: 'r', plan, motivoCierre: 'ya no aparece', simular: false });
  assert.equal(errores.length, 1);
  assert.match(errores[0], /crear CVE-MAL: 422/);
  assert.deepEqual(hechas, ['crear t CVE-BIEN', 'comentar #7', 'update #7 closed']);
});
