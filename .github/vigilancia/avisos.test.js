'use strict';

// Pruebas del cruce KEV/OSV: node --test .github/vigilancia/*.test.js

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const a = require('./avisos.js');
const v = require('./alertas.js');

const RAIZ = path.join(__dirname, '..', '..');
const leerDelRepo = (f) => fs.readFileSync(path.join(RAIZ, f), 'utf8');
const AHORA = Date.parse('2026-10-25T12:00:00Z');

const componentes = () =>
  a.resolverVersiones(
    {
      componentes: [
        { id: 'kernel', nombre: 'Kernel', kev: [{ vendor: '^Linux$', producto: 'Kernel' }] },
        { id: 'openssl', nombre: 'OpenSSL', kev: [{ vendor: 'OpenSSL', producto: 'OpenSSL' }] },
        {
          id: 'caddy',
          nombre: 'Caddy',
          version: { fichero: 'compose', patron: 'caddy:([0-9.]+)-alpine@' },
          kev: [{ vendor: 'Caddy', producto: 'Caddy' }],
          osv: [{ ecosystem: 'Go', name: 'github.com/caddyserver/caddy/v2' }],
        },
      ],
    },
    () => 'image: caddy:2.11.4-alpine@sha256:abc',
  );

const relleno = Array.from({ length: a.MIN_ENTRADAS_KEV }, (_, i) => ({
  cveID: `CVE-2010-${i}`,
  vendorProject: 'Otro',
  product: 'Otro',
  dateAdded: '2010-01-01',
}));
const kev = (...entradas) => ({ vulnerabilities: [...relleno, ...entradas] });
const entrada = (cveID, vendorProject, product, dateAdded = '2026-10-20') => ({
  cveID,
  vendorProject,
  product,
  dateAdded,
  vulnerabilityName: `${product} vuln`,
  shortDescription: 'Descripción @alguien [x](https://e.vil)',
  dueDate: '2026-11-10',
  knownRansomwareCampaignUse: 'Unknown',
  requiredAction: 'Apply mitigations',
});

test('el inventario real resuelve todas sus versiones desde los ficheros del repositorio', () => {
  const inventario = JSON.parse(leerDelRepo('.github/vigilancia/inventario.json'));
  const comps = a.resolverVersiones(inventario, leerDelRepo);
  const conVersion = comps.filter((c) => c.version);
  assert.ok(conVersion.length >= 4, 'aspnetcore, postgresql, caddy y seq tienen versión');
  for (const c of conVersion) assert.match(c.version, /^[0-9]+(\.[0-9]+)+$/, c.id);
  assert.ok(comps.some((c) => c.id === 'kernel'));
});

test('un patrón de versión que deja de casar es un error, no un «sin versión», y no tumba KEV', () => {
  const [x, y] = a.resolverVersiones(
    {
      componentes: [
        { id: 'x', kev: [{ vendor: 'a', producto: 'b' }], version: { fichero: 'f', patron: 'x:([0-9]+)' } },
        { id: 'y', kev: [{ vendor: 'a', producto: 'b' }], version: { fichero: 'falta', patron: 'y:([0-9]+)' } },
      ],
    },
    (f) => {
      if (f === 'falta') throw new Error('ENOENT');
      return 'nada';
    },
  );
  assert.equal(x.version, null);
  assert.match(x.errorVersion, /No se encuentra la versión de x en f/);
  assert.match(y.errorVersion, /No se puede leer falta .*ENOENT/);
  // El componente sigue en la lista con sus criterios KEV.
  assert.equal(x.kev.length, 1);
  assert.throws(() => a.resolverVersiones({ componentes: [] }, () => ''), /no tiene componentes/);
  assert.throws(() => a.resolverVersiones({ componentes: [{ id: 'x', kev: [] }] }, () => ''), /criterios KEV/);
  assert.throws(
    () => a.resolverVersiones({ componentes: [{ id: 'x', kev: [{ vendor: 'a', producto: 'b' }], osv: [{ ecosystem: 'Go', name: 'p' }] }] }, () => ''),
    /OSV pero no versión/,
  );
});

test('un catálogo KEV vacío o recortado no se lee como «sin entradas»', () => {
  assert.throws(() => a.validarKev({}), /forma esperada/);
  assert.throws(() => a.validarKev({ vulnerabilities: [entrada('CVE-1', 'Linux', 'Kernel')] }), /forma esperada/);
  assert.throws(() => a.cruzarKev({ vulnerabilities: [] }, componentes(), AHORA, 30));
});

test('cruza por fabricante y producto dentro de la ventana', () => {
  const cruces = a.cruzarKev(
    kev(
      entrada('CVE-2026-1001', 'Linux', 'Kernel'),
      entrada('CVE-2026-1002', 'Linux Foundation', 'Kernel'),
      entrada('CVE-2026-1003', 'OpenSSL', 'OpenSSL', '2026-08-01'),
      entrada('CVE-2026-1004', 'openssl', 'openssl', '2026-09-26'),
    ),
    componentes(),
    AHORA,
    30,
  );
  assert.deepEqual(cruces.map((c) => c.entrada.cveID), ['CVE-2026-1001', 'CVE-2026-1004']);
  assert.deepEqual(cruces[1].componentes.map((c) => c.id), ['openssl']);
});

test('la alerta KEV deduplica por CVE, escapa el texto ajeno y es crítica', () => {
  const [cruce] = a.cruzarKev(kev(entrada('CVE-2026-1001', 'Linux', 'Kernel')), componentes(), AHORA, 30);
  const al = a.alertaKev(cruce);
  assert.equal(v.claveDe(al.cuerpo, 'kev'), 'CVE-2026-1001');
  assert.equal(al.critica, true);
  assert.ok(!al.cuerpo.includes('@alguien'));
  assert.ok(!al.cuerpo.includes('[x]'));
  assert.match(al.titulo, /^\[Vigilancia KEV\] CVE-2026-1001 explotada: Kernel$/);
});

test('los patrones KEV del inventario real casan lo que corre en el VPS y no subcadenas ni productos ajenos', () => {
  const comps = a.resolverVersiones(JSON.parse(leerDelRepo('.github/vigilancia/inventario.json')), leerDelRepo);
  const casa = (vendorProject, product) =>
    a.cruzarKev(kev(entrada('CVE-2026-9999', vendorProject, product)), comps, AHORA, 30).flatMap((c) => c.componentes.map((x) => x.id));
  assert.deepEqual(casa('Linux', 'Kernel'), ['kernel']);
  assert.deepEqual(casa('OpenSSL', 'OpenSSL'), ['openssl']);
  assert.deepEqual(casa('Sudo', 'Sudo'), ['sudo']);
  assert.deepEqual(casa('Microsoft', '.NET Core and Visual Studio'), ['aspnetcore']);
  assert.deepEqual(casa('Microsoft', 'ASP.NET Core'), ['aspnetcore']);
  assert.deepEqual(casa('Docker', 'Docker Engine'), ['docker']);
  assert.deepEqual(casa('Canonical', 'Ubuntu Linux'), ['ubuntu']);
  assert.deepEqual(casa('haxx', 'libcurl'), ['curl']);
  assert.deepEqual(casa('Microsoft', '.NET Framework'), []);
  assert.deepEqual(casa('Microsoft', '.NET Framework, SharePoint, Visual Studio'), []);
  assert.deepEqual(casa('Microsoft', 'Windows'), []);
  assert.deepEqual(casa('Docker', 'Docker Desktop'), []);
  assert.deepEqual(casa('Securly', 'Securly Filter'), []);
  assert.deepEqual(casa('Pseudo Corp', 'Pseudosudo'), []);
});

test('un CVE repetido en el catálogo da una sola alerta y no tumba la capa', () => {
  const cruces = a.cruzarKev(kev(entrada('CVE-2026-1001', 'Linux', 'Kernel'), entrada('CVE-2026-1001', 'Linux', 'Kernel')), componentes(), AHORA, 30);
  assert.equal(cruces.length, 1);
  assert.doesNotThrow(() =>
    v.planificar({ capa: 'kev', existentes: [], alertas: cruces.map(a.alertaKev), maxNuevas: 10, ...a.POLITICA_CAPAS.kev }),
  );
});

test('la política fija que KEV nunca se cierra ni se reabre sola y OSV sí', () => {
  assert.deepEqual(a.POLITICA_CAPAS, {
    kev: { completo: false, reabrirCerradas: false },
    osv: { completo: true, reabrirCerradas: true },
  });
  assert.ok(Object.isFrozen(a.POLITICA_CAPAS.kev));
});

test('una alerta KEV cerrada tras el triaje no se reabre y nunca se cierra sola', () => {
  const [cruce] = a.cruzarKev(kev(entrada('CVE-2026-1001', 'Linux', 'Kernel')), componentes(), AHORA, 30);
  const al = a.alertaKev(cruce);
  const cerrada = { number: 4, state: 'closed', state_reason: 'completed', title: al.titulo, body: al.cuerpo };
  const abiertaVieja = { number: 5, state: 'open', title: 't', body: v.marcador('kev', 'CVE-2020-0001') };
  const plan = v.planificar({ capa: 'kev', existentes: [cerrada, abiertaVieja], alertas: [al], maxNuevas: 10, ...a.POLITICA_CAPAS.kev });
  assert.deepEqual(plan.reabrir, []);
  assert.deepEqual(plan.crear, []);
  assert.deepEqual(plan.silenciadas.map((x) => x.number), [4]);
  assert.deepEqual(plan.cerrar, []);
});

test('esCritico lee la severidad de GHSA, de los ecosistemas y las puntuaciones numéricas', () => {
  assert.equal(a.esCritico({ database_specific: { severity: 'CRITICAL' } }), true);
  assert.equal(a.esCritico({ database_specific: { severity: 'HIGH' } }), false);
  assert.equal(a.esCritico({ severity: [{ type: 'Ubuntu', score: 'critical' }] }), true);
  assert.equal(a.esCritico({ severity: [{ type: 'CVSS_V3', score: 'CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H' }] }), false);
  assert.equal(a.esCritico({ severity: [{ type: 'X', score: '9.8' }] }), true);
  assert.equal(a.esCritico({ affected: [{ ecosystem_specific: { severity: 'Critical' } }] }), true);
  assert.equal(a.esCritico({}), false);
});

test('el cruce OSV agrupa por aviso, descarta retirados y no críticos, y rechaza respuestas rotas', () => {
  const [caddy] = componentes().filter((c) => c.id === 'caddy');
  const paquete = caddy.osv[0];
  const vulns = [
    { id: 'GHSA-aaaa', aliases: ['CVE-2026-2001'], summary: 'Fallo @x', database_specific: { severity: 'CRITICAL' } },
    { id: 'GHSA-bbbb', database_specific: { severity: 'MODERATE' } },
    { id: 'GHSA-cccc', withdrawn: '2026-01-01', database_specific: { severity: 'CRITICAL' } },
  ];
  const cruces = a.cruzarOsv([
    { componente: caddy, paquete, vulns },
    { componente: caddy, paquete: { ecosystem: 'Go', name: 'otro' }, vulns: [vulns[0]] },
  ]);
  assert.deepEqual(cruces.map((c) => c.clave), ['GHSA-aaaa']);
  assert.equal(cruces[0].afectados.length, 2);
  const al = a.alertaOsv(cruces[0]);
  assert.equal(v.claveDe(al.cuerpo, 'osv'), 'GHSA-aaaa');
  assert.match(al.cuerpo, /CVE-2026-2001/);
  assert.match(al.cuerpo, /\| Caddy \| Go\/github\.com\/caddyserver\/caddy\/v2 \| 2\.11\.4 \|/);
  assert.ok(!al.cuerpo.includes('@x'));
  assert.throws(() => a.cruzarOsv([{ componente: caddy, paquete, vulns: undefined }]), /sin lista de avisos/);
});
