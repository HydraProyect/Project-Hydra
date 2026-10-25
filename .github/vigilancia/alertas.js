'use strict';

// Conciliación de las alertas de vigilancia de vulnerabilidades con issues de GitHub.
//
// La usan los workflows de vigilancia (vigilancia-imagenes.yml). Cada capa de
// vigilancia entrega una lista de alertas { clave, titulo, cuerpo, critica } y esta
// conciliación deja en el repositorio exactamente una issue abierta por alerta:
//
//   - la identidad de una issue es un marcador HTML invisible en su cuerpo,
//     `<!-- vigilancia:<capa>:<clave> -->`, no su título (que cambia con las imágenes
//     afectadas); solo cuentan las issues con la etiqueta `Seguridad: alerta`, que un
//     usuario sin permiso de triaje no puede poner;
//   - si ya hay una abierta con ese marcador se actualiza (título y cuerpo) solo
//     cuando cambian; si solo hay cerradas, se reabre la más reciente; si no hay
//     ninguna, se crea, con un tope por ejecución para no inundar el repositorio;
//   - una issue abierta cuya alerta ya no aparece se cierra sola, PERO solo cuando el
//     escaneo ha sido completo: un escáner que no ha podido mirar una imagen no es un
//     «ya no está», y entonces no se cierra nada.
//
// El módulo no lee ficheros ni el entorno: recibe los datos ya leídos y el cliente
// de Octokit, así sus reglas se prueban con `node --test .github/vigilancia/*.test.js`.

const ETIQUETA = 'Seguridad: alerta';
const COLOR_ETIQUETA = 'b60205';
const DESCRIPCION_ETIQUETA =
  'Alerta automática de vigilancia de vulnerabilidades (la abren, actualizan y cierran los workflows vigilancia-*)';
const ORDEN_SEVERIDAD = { CRITICAL: 4, HIGH: 3, MEDIUM: 2, LOW: 1, UNKNOWN: 0 };
const SEVERIDADES_ALERTA = new Set(['CRITICAL', 'HIGH']);

function validarCapa(capa) {
  if (!/^[a-z][a-z0-9-]*$/.test(String(capa))) {
    throw new Error(`Capa de vigilancia no válida: ${capa}`);
  }
  return capa;
}

function normalizarClave(clave) {
  const limpia = String(clave).trim();
  if (!/^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(limpia)) {
    throw new Error(`Clave de alerta no válida: ${clave}`);
  }
  return limpia;
}

function marcador(capa, clave) {
  return `<!-- vigilancia:${validarCapa(capa)}:${normalizarClave(clave)} -->`;
}

function claveDe(cuerpo, capa) {
  validarCapa(capa);
  const m = new RegExp(`<!-- vigilancia:${capa}:([A-Za-z0-9][A-Za-z0-9._-]*) -->`).exec(String(cuerpo || ''));
  return m ? m[1] : null;
}

// Texto de terceros (títulos de CVE, nombres de paquete) dentro de una tabla Markdown:
// sin HTML, sin barras que rompan la tabla y sin saltos de línea.
function escaparCelda(texto, max = 160) {
  let s = String(texto == null ? '' : texto)
    .replace(/[\r\n\t]+/g, ' ')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/\|/g, '&#124;')
    .replace(/`/g, "'")
    .trim();
  if (s.length > max) s = `${s.slice(0, max - 1)}…`;
  return s;
}

function urlSegura(url) {
  const s = String(url || '');
  return /^https:\/\/[^\s<>()"'`]+$/.test(s) ? s : '';
}

function mayorSeveridad(a, b) {
  return (ORDEN_SEVERIDAD[b] || 0) > (ORDEN_SEVERIDAD[a] || 0) ? b : a;
}

// resultados: [{ imagen, informe }] con el JSON de `trivy image --format json` ya
// parseado. Devuelve un mapa id -> { id, severidad, titulo, url, afectados[] }.
// Un informe sin SchemaVersion no es un informe de Trivy (fichero vacío, error de
// red guardado como salida) y se rechaza en vez de leerse como «sin hallazgos».
function agruparTrivy(resultados) {
  const porCve = new Map();
  for (const { imagen, informe } of resultados) {
    if (!informe || typeof informe !== 'object' || !informe.SchemaVersion) {
      throw new Error(`El informe de ${imagen} no es un informe JSON de Trivy.`);
    }
    if (informe.Results != null && !Array.isArray(informe.Results)) {
      throw new Error(`El informe de ${imagen} tiene un Results que no es una lista.`);
    }
    for (const resultado of informe.Results || []) {
      for (const v of resultado.Vulnerabilities || []) {
        const severidad = String(v.Severity || 'UNKNOWN').toUpperCase();
        // Defensa en profundidad: el escaneo ya filtra por severidad y por corrección
        // disponible (--severity, --ignore-unfixed).
        if (!SEVERIDADES_ALERTA.has(severidad) || !v.FixedVersion) continue;
        const id = normalizarClave(v.VulnerabilityID);
        if (!porCve.has(id)) {
          porCve.set(id, { id, severidad, titulo: v.Title || '', url: urlSegura(v.PrimaryURL), afectados: new Map() });
        }
        const cve = porCve.get(id);
        cve.severidad = mayorSeveridad(cve.severidad, severidad);
        if (!cve.titulo && v.Title) cve.titulo = v.Title;
        if (!cve.url) cve.url = urlSegura(v.PrimaryURL);
        const k = `${imagen}|${v.PkgName}|${v.InstalledVersion}`;
        if (!cve.afectados.has(k)) {
          cve.afectados.set(k, {
            imagen,
            paquete: v.PkgName || '',
            instalada: v.InstalledVersion || '',
            corregida: v.FixedVersion || '',
            objetivo: resultado.Target || '',
          });
        }
      }
    }
  }
  for (const cve of porCve.values()) {
    cve.afectados = [...cve.afectados.values()].sort((a, b) =>
      `${a.imagen}|${a.paquete}|${a.instalada}`.localeCompare(`${b.imagen}|${b.paquete}|${b.instalada}`),
    );
  }
  return porCve;
}

// Cuerpo determinista (sin fechas): solo cambia cuando cambia lo afectado, así la
// issue no se reescribe cada día.
function alertaDeImagenes(cve) {
  const imagenes = [...new Set(cve.afectados.map((a) => a.imagen))].sort();
  const filas = cve.afectados.map(
    (a) =>
      `| ${escaparCelda(a.imagen)} | ${escaparCelda(a.paquete)} | ${escaparCelda(a.instalada)} | ${escaparCelda(a.corregida)} | ${escaparCelda(a.objetivo)} |`,
  );
  const enlace = cve.url ? `[${cve.id}](${cve.url})` : cve.id;
  const cuerpo = [
    marcador('imagenes', cve.id),
    `## ${cve.id} (${cve.severidad}) con corrección disponible`,
    '',
    `**Aviso:** ${enlace}${cve.titulo ? ` — ${escaparCelda(cve.titulo, 300)}` : ''}`,
    '',
    'Detectada por el escaneo diario de las imágenes desplegadas (`vigilancia-imagenes.yml`), con la base de CVE del día y sin reconstruir nada.',
    '',
    '| Imagen | Paquete | Versión instalada | Versión corregida | Dónde |',
    '|---|---|---|---|---|',
    ...filas,
    '',
    '### Qué hacer',
    '',
    '- Imagen de terceros (compose de producción, base del `Dockerfile`): subir el digest fijado a una versión que traiga la corrección (Dependabot de `docker` suele proponerlo).',
    '- Paquete instalado por el `Dockerfile` o dependencia NuGet en la imagen de la aplicación: reconstruir y desplegar, o subir la versión del paquete.',
    '- Si se acepta el riesgo (falso positivo, no aplicable), anotarlo en `.trivyignore` con su motivo: la issue se cerrará sola en el siguiente escaneo completo.',
    '',
    'Esta issue la mantiene el workflow: se actualiza si cambia lo afectado y se cierra sola cuando el CVE deja de aparecer.',
  ].join('\n');
  return {
    clave: cve.id,
    titulo: `[Vigilancia imágenes] ${cve.id} (${cve.severidad}) en ${imagenes.join(', ')}`.slice(0, 250),
    cuerpo,
    critica: cve.severidad === 'CRITICAL',
  };
}

// Plan puro: qué crear, actualizar, reabrir y cerrar. `existentes` son issues con la
// etiqueta (abiertas y cerradas): { number, state, title, body }.
function planificar({ capa, existentes, alertas, completo, maxNuevas }) {
  validarCapa(capa);
  const porClave = new Map();
  for (const issue of existentes) {
    const clave = claveDe(issue.body, capa);
    if (!clave) continue;
    if (!porClave.has(clave)) porClave.set(clave, []);
    porClave.get(clave).push(issue);
  }

  const plan = { crear: [], actualizar: [], reabrir: [], cerrar: [], duplicadas: [], aplazadas: [], conservadas: [] };
  const vistas = new Set();

  for (const alerta of alertas) {
    const clave = normalizarClave(alerta.clave);
    if (vistas.has(clave)) throw new Error(`Alerta repetida en la misma ejecución: ${clave}`);
    vistas.add(clave);
    const issues = porClave.get(clave) || [];
    const abiertas = issues.filter((i) => i.state === 'open').sort((a, b) => a.number - b.number);
    const cerradas = issues.filter((i) => i.state !== 'open').sort((a, b) => b.number - a.number);
    if (abiertas.length > 0) {
      const [principal, ...sobrantes] = abiertas;
      if (principal.title !== alerta.titulo || principal.body !== alerta.cuerpo) {
        plan.actualizar.push({ number: principal.number, alerta });
      }
      for (const d of sobrantes) plan.duplicadas.push({ number: d.number, de: principal.number });
    } else if (cerradas.length > 0) {
      plan.reabrir.push({ number: cerradas[0].number, alerta });
    } else if (plan.crear.length < maxNuevas) {
      plan.crear.push({ alerta });
    } else {
      plan.aplazadas.push({ alerta });
    }
  }

  for (const [clave, issues] of porClave) {
    if (vistas.has(clave)) continue;
    for (const issue of issues.filter((i) => i.state === 'open')) {
      if (completo) plan.cerrar.push({ number: issue.number, clave });
      else plan.conservadas.push({ number: issue.number, clave });
    }
  }
  return plan;
}

async function asegurarEtiqueta({ github, owner, repo }) {
  try {
    await github.rest.issues.getLabel({ owner, repo, name: ETIQUETA });
  } catch (e) {
    if (e.status !== 404) throw e;
    await github.rest.issues.createLabel({ owner, repo, name: ETIQUETA, color: COLOR_ETIQUETA, description: DESCRIPCION_ETIQUETA });
  }
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
    .map((i) => ({ number: i.number, state: i.state, title: i.title, body: i.body || '' }));
}

// Aplica el plan. Un fallo en una issue no impide las demás; devuelve la lista de
// errores para que el workflow falle visible al final.
async function aplicar({ github, owner, repo, plan, motivoCierre, simular }) {
  const errores = [];
  const intentar = async (descripcion, accion) => {
    if (simular) return;
    try {
      await accion();
    } catch (e) {
      errores.push(`${descripcion}: ${e.status || 'sin estado'} ${e.message}`);
    }
  };
  const hayCambios = plan.crear.length + plan.reabrir.length + plan.actualizar.length > 0;
  if (hayCambios && !simular) {
    try {
      await asegurarEtiqueta({ github, owner, repo });
    } catch (e) {
      errores.push(`crear la etiqueta: ${e.status || 'sin estado'} ${e.message}`);
      return errores;
    }
  }
  for (const { alerta } of plan.crear) {
    await intentar(`crear ${alerta.clave}`, () =>
      github.rest.issues.create({ owner, repo, title: alerta.titulo, body: alerta.cuerpo, labels: [ETIQUETA] }),
    );
  }
  for (const { number, alerta } of plan.actualizar) {
    await intentar(`actualizar #${number}`, () =>
      github.rest.issues.update({ owner, repo, issue_number: number, title: alerta.titulo, body: alerta.cuerpo }),
    );
  }
  for (const { number, alerta } of plan.reabrir) {
    await intentar(`reabrir #${number}`, async () => {
      await github.rest.issues.update({ owner, repo, issue_number: number, state: 'open', title: alerta.titulo, body: alerta.cuerpo });
      await github.rest.issues.createComment({ owner, repo, issue_number: number, body: 'Vuelve a aparecer en el escaneo de hoy: se reabre.' });
    });
  }
  for (const { number } of plan.cerrar) {
    await intentar(`cerrar #${number}`, async () => {
      await github.rest.issues.createComment({ owner, repo, issue_number: number, body: motivoCierre });
      await github.rest.issues.update({ owner, repo, issue_number: number, state: 'closed', state_reason: 'completed' });
    });
  }
  for (const { number, de } of plan.duplicadas) {
    await intentar(`cerrar la duplicada #${number}`, async () => {
      await github.rest.issues.createComment({ owner, repo, issue_number: number, body: `Duplicada de #${de}, que sigue abierta con la misma alerta.` });
      await github.rest.issues.update({ owner, repo, issue_number: number, state: 'closed', state_reason: 'not_planned' });
    });
  }
  return errores;
}

module.exports = {
  ETIQUETA,
  marcador,
  claveDe,
  escaparCelda,
  urlSegura,
  agruparTrivy,
  alertaDeImagenes,
  planificar,
  listarExistentes,
  aplicar,
};
