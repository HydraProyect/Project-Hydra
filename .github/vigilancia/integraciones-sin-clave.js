'use strict';

// Issue única «Integraciones con clave sin ejecutar» (integraciones-con-clave.yml,
// job `aviso-sin-clave`).
//
// Sin secreto, los jobs de integraciones con clave terminan en verde con un aviso:
// un rojo permanente en `main` enseñaba a ignorar los rojos (60 de 60 ejecuciones en
// rojo por la misma ausencia, mejora 1.5 de la evaluación del harness del 2026-11-05).
// La señal duradera es esta issue, y solo una:
//
//   - su identidad es un marcador HTML invisible en el cuerpo, y solo cuentan las
//     issues con la etiqueta de abajo, que un usuario sin permiso de triaje no puede
//     poner (una issue ajena con el marcador copiado no se toca);
//   - si falta algún secreto: se actualiza la abierta (la de número más bajo; las
//     demás se cierran como duplicadas), se reabre la última cerrada o se crea;
//   - una cerrada a mano como «no planificada» se respeta y no se reabre;
//   - si los dos jobs dicen que su clave llegó, se cierra la abierta. Si algún job no
//     llegó a medirlo (falló antes, se omitió), no se cierra nada: no haber mirado no
//     es haber comprobado que ya está.
//
// El módulo no lee ficheros ni el entorno: recibe los datos ya leídos y el cliente de
// Octokit, así sus reglas se prueban con `node --test .github/vigilancia/*.test.js`.

const ETIQUETA = 'CI: integraciones con clave';
const COLOR_ETIQUETA = 'fbca04';
const DESCRIPCION_ETIQUETA =
  'Aviso automático de integraciones-con-clave.yml: falta un secreto de CI y sus tests no se ejecutan';
const MARCADOR = '<!-- integraciones-con-clave:sin-clave -->';
const TITULO = 'Integraciones con clave sin ejecutar';

// Qué secreto mide cada job. Los nombres viven aquí y no en el YAML del job de aviso:
// scripts/verificar-secretos-de-ci.sh exige `environment: ci-integraciones` a todo job
// cuyo texto cite uno de estos nombres, y el job de aviso no debe declararlo.
const INTEGRACIONES = [
  { clave: 'anthropic', secreto: 'CI_ANTHROPIC_API_KEY', job: 'Regresión de prompts de IA (Anthropic)' },
  { clave: 'stripe', secreto: 'CI_STRIPE_TEST_API_KEY', job: 'Stripe en modo prueba' },
];

// medidas: { anthropic: 'true'|'false'|'', stripe: 'true'|'false'|'' } (salidas de los jobs).
function evaluar(medidas) {
  const faltan = [];
  const sinMedir = [];
  for (const i of INTEGRACIONES) {
    const valor = String(medidas[i.clave] == null ? '' : medidas[i.clave]).trim();
    if (valor === 'false') faltan.push(i);
    else if (valor !== 'true') sinMedir.push(i);
  }
  return { faltan, sinMedir };
}

function cuerpo({ faltan, fecha, urlEjecucion }) {
  const filas = faltan.map((i) => `| \`${i.secreto}\` | ${i.job} |`).join('\n');
  const url = /^https:\/\/[^\s<>()"'`]+$/.test(String(urlEjecucion || '')) ? urlEjecucion : '';
  return [
    MARCADOR,
    'Los tests de integración con clave real **no se están ejecutando**: falta al menos un secreto en el environment `ci-integraciones`.',
    '',
    '| Secreto que falta | Tests que no corren |',
    '| --- | --- |',
    filas,
    '',
    `Última comprobación: ${fecha}${url ? ` ([ejecución](${url}))` : ''}.`,
    '',
    '**Solución:** crear el secreto en Settings → Environments → `ci-integraciones` (solo el propietario). Relanzar el workflow no lo arregla.',
    '',
    'Esta issue la abre, actualiza y cierra `integraciones-con-clave.yml` (job `aviso-sin-clave`). Se cierra sola cuando las dos claves llegan; cerrada a mano como «no planificada», no se reabre.',
  ].join('\n');
}

// existentes: issues con la etiqueta (abiertas y cerradas): { number, state, state_reason, title, body }.
function planificar({ existentes, medidas, fecha, urlEjecucion }) {
  const { faltan, sinMedir } = evaluar(medidas);
  const propias = existentes.filter((i) => String(i.body || '').includes(MARCADOR));
  const abiertas = propias.filter((i) => i.state === 'open').sort((a, b) => a.number - b.number);
  const cerradas = propias.filter((i) => i.state !== 'open').sort((a, b) => b.number - a.number);
  const plan = { accion: 'nada', number: null, titulo: TITULO, cuerpo: null, duplicadas: [], faltan, sinMedir };

  if (faltan.length > 0) {
    plan.cuerpo = cuerpo({ faltan, fecha, urlEjecucion });
    if (abiertas.length > 0) {
      const [principal, ...sobrantes] = abiertas;
      plan.number = principal.number;
      plan.accion = principal.title !== TITULO || principal.body !== plan.cuerpo ? 'actualizar' : 'nada';
      plan.duplicadas = sobrantes.map((d) => ({ number: d.number, de: principal.number }));
    } else if (cerradas.length > 0) {
      plan.number = cerradas[0].number;
      plan.accion = cerradas[0].state_reason === 'not_planned' ? 'silenciada' : 'reabrir';
    } else {
      plan.accion = 'crear';
    }
    return plan;
  }

  if (sinMedir.length === 0 && abiertas.length > 0) {
    plan.accion = 'cerrar';
    plan.number = abiertas[0].number;
    plan.duplicadas = abiertas.slice(1).map((d) => ({ number: d.number, de: abiertas[0].number }));
  }
  return plan;
}

async function listarExistentes({ github, owner, repo }) {
  const issues = await github.paginate(github.rest.issues.listForRepo, {
    owner,
    repo,
    labels: ETIQUETA,
    state: 'all',
    per_page: 100,
  });
  return issues
    .filter((i) => !i.pull_request)
    .map((i) => ({ number: i.number, state: i.state, state_reason: i.state_reason || null, title: i.title, body: i.body || '' }));
}

async function asegurarEtiqueta({ github, owner, repo }) {
  try {
    await github.rest.issues.getLabel({ owner, repo, name: ETIQUETA });
  } catch (e) {
    if (e.status !== 404) throw e;
    await github.rest.issues.createLabel({ owner, repo, name: ETIQUETA, color: COLOR_ETIQUETA, description: DESCRIPCION_ETIQUETA });
  }
}

async function aplicar({ github, owner, repo, plan, fecha }) {
  const { accion, number, titulo } = plan;
  if (accion === 'crear') {
    await asegurarEtiqueta({ github, owner, repo });
    const r = await github.rest.issues.create({ owner, repo, title: titulo, body: plan.cuerpo, labels: [ETIQUETA] });
    return r && r.data ? r.data.number : null;
  }
  if (accion === 'actualizar') {
    await github.rest.issues.update({ owner, repo, issue_number: number, title: titulo, body: plan.cuerpo });
  }
  if (accion === 'reabrir') {
    await github.rest.issues.update({ owner, repo, issue_number: number, state: 'open', title: titulo, body: plan.cuerpo });
    await github.rest.issues.createComment({ owner, repo, issue_number: number, body: `Vuelve a faltar un secreto el ${fecha}: se reabre.` });
  }
  if (accion === 'cerrar') {
    await github.rest.issues.createComment({
      owner,
      repo,
      issue_number: number,
      body: `El ${fecha} las dos claves de CI llegaron a sus jobs: se cierra.`,
    });
    await github.rest.issues.update({ owner, repo, issue_number: number, state: 'closed', state_reason: 'completed' });
  }
  for (const d of plan.duplicadas) {
    await github.rest.issues.createComment({ owner, repo, issue_number: d.number, body: `Duplicada de #${d.de}.` });
    await github.rest.issues.update({ owner, repo, issue_number: d.number, state: 'closed', state_reason: 'completed' });
  }
  return number;
}

module.exports = {
  ETIQUETA,
  MARCADOR,
  TITULO,
  INTEGRACIONES,
  evaluar,
  cuerpo,
  planificar,
  listarExistentes,
  aplicar,
};
