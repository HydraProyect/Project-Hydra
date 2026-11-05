'use strict';

// Pruebas del resumen del escaneo nocturno sin filtro (trivy-nocturno-resumen.jq):
// node --test .github/vigilancia/*.test.js (las corre ci.yml, donde jq está instalado).
// Sin jq en local se omiten; en CI (variable CI) la ausencia de jq es un fallo, no un verde.

const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('path');
const { spawnSync } = require('child_process');

const PROGRAMA = path.join(__dirname, 'trivy-nocturno-resumen.jq');
const hayJq = !spawnSync('jq', ['--version']).error;
const opciones = { skip: !hayJq && !process.env.CI ? 'jq no está instalado en este equipo' : false };

function resumir(informe) {
  const r = spawnSync('jq', ['-r', '-f', PROGRAMA], { input: JSON.stringify(informe), encoding: 'utf8' });
  if (r.error) throw r.error;
  assert.equal(r.status, 0, r.stderr);
  return r.stdout;
}

const vuln = (id, sev, pkg, instalada, corregida) => ({
  VulnerabilityID: id,
  Severity: sev,
  PkgName: pkg,
  InstalledVersion: instalada,
  ...(corregida ? { FixedVersion: corregida } : {}),
});

const INFORME = {
  SchemaVersion: 2,
  Results: [
    {
      Target: 'caemanager:nocturno (debian 12)',
      Vulnerabilities: [
        vuln('CVE-2026-109518', 'HIGH', 'apt', '2.6.1'),
        vuln('CVE-2026-0001', 'CRITICAL', 'libssl3', '3.0.1', '3.0.2'),
        vuln('CVE-2026-0002', 'MEDIUM', 'zlib1g', '1.2', ''),
      ],
    },
    { Target: 'app/CaeManager.Web.deps.json', Vulnerabilities: [vuln('GHSA-x|y', 'HIGH', '@paquete<b>', '1.0', '1.1')] },
    { Target: 'sin vulnerabilidades' },
  ],
};

test('lista el CVE de HIGH sin corrección, que el gate de PR deja pasar', opciones, () => {
  const md = resumir(INFORME);
  assert.match(md, /\| CVE-2026-109518 \| HIGH \| apt \| 2\.6\.1 \| sin corrección \|/);
  assert.match(md, /\| CVE-2026-0001 \| CRITICAL \| libssl3 \| 3\.0\.1 \| 3\.0\.2 \|/);
});

test('cuenta por severidad con y sin corrección, todas las severidades', opciones, () => {
  const md = resumir(INFORME);
  assert.match(md, /\| CRITICAL \| 1 \| 0 \|/);
  assert.match(md, /\| HIGH \| 1 \| 1 \|/);
  assert.match(md, /\| MEDIUM \| 0 \| 1 \|/);
  assert.match(md, /\| UNKNOWN \| 0 \| 0 \|/);
});

test('CRITICAL antes que HIGH en la lista', opciones, () => {
  const md = resumir(INFORME);
  assert.ok(md.indexOf('CVE-2026-0001') < md.indexOf('CVE-2026-109518'));
});

test('el texto de terceros no rompe la tabla ni menciona a nadie', opciones, () => {
  const md = resumir(INFORME);
  const fila = md.split('\n').find((l) => l.includes('GHSA'));
  assert.equal((fila.match(/\|/g) || []).length, 6);
  assert.doesNotMatch(fila, /[@<>]/);
});

test('sin vulnerabilidades graves dice «Ninguna.»', opciones, () => {
  const md = resumir({ SchemaVersion: 2, Results: [{ Target: 'x', Vulnerabilities: [vuln('CVE-1', 'LOW', 'a', '1')] }] });
  assert.match(md, /Ninguna\./);
  assert.match(md, /\| LOW \| 0 \| 1 \|/);
});
