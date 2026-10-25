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

// Identificadores de aviso de terceros (RHSA-2024:1234, GHSA-…) a clave de marcador: lo que
// no sea [A-Za-z0-9._-] pasa a «_». Un identificador raro no debe tumbar todas las alertas.
function claveSegura(id) {
  const clave = String(id == null ? '' : id)
    .trim()
    .replace(/[^A-Za-z0-9._-]/g, '_')
    .replace(/^[^A-Za-z0-9]+/, '');
  if (!clave) throw new Error(`Identificador de aviso vacío o ilegible: ${id}`);
  return clave.slice(0, 100);
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
// sin HTML, sin barras que rompan la tabla, sin saltos de línea, sin menciones (@paquete
// notificaría a un usuario) y sin corchetes con los que armar un enlace Markdown. Se recorta
// antes de escapar para no partir una entidad.
function escaparCelda(texto, max = 160) {
  let s = String(texto == null ? '' : texto).replace(/[\r\n\t]+/g, ' ').trim();
  if (s.length > max) s = `${s.slice(0, max - 1)}…`;
  return s
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/\|/g, '&#124;')
    .replace(/@/g, '&#64;')
    .replace(/\[/g, '&#91;')
    .replace(/\]/g, '&#93;')
    .replace(/`/g, "'");
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
        const id = String(v.VulnerabilityID || '').trim();
        const clave = claveSegura(id);
        if (!porCve.has(clave)) {
          porCve.set(clave, { clave, id, severidad, titulo: v.Title || '', url: urlSegura(v.PrimaryURL), afectados: new Map() });
        }
        const cve = porCve.get(clave);
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

// Orden de prioridad (decide qué entra bajo el tope de issues nuevas): CRITICAL primero,
// después por identificador.
function ordenarCves(cves) {
  return [...cves].sort((a, b) =>
    a.severidad === b.severidad ? a.clave.localeCompare(b.clave) : (ORDEN_SEVERIDAD[b.severidad] || 0) - (ORDEN_SEVERIDAD[a.severidad] || 0),
  );
}

// objetivos: [{ nombre, estado }] con estado ok | fallo | ausente. El escaneo es completo
// solo si todos se han escaneado: un fallo o un artefacto ausente impiden cerrar issues.
function resumirObjetivos(objetivos) {
  const fallidos = objetivos.filter((o) => o.estado === 'fallo');
  const ausentes = objetivos.filter((o) => o.estado === 'ausente');
  const desconocidos = objetivos.filter((o) => !['ok', 'fallo', 'ausente'].includes(o.estado));
  return {
    fallidos: [...fallidos, ...desconocidos],
    ausentes,
    completo: objetivos.length > 0 && fallidos.length === 0 && ausentes.length === 0 && desconocidos.length === 0,
  };
}

const MAX_FILAS = 150;

// Cuerpo determinista (sin fechas): solo cambia cuando cambia lo afectado, así la
// issue no se reescribe cada día. Con un tope de filas: GitHub rechaza cuerpos de más
// de 65 536 caracteres y el job quedaría en rojo cada día.
function alertaDeImagenes(cve) {
  const imagenes = [...new Set(cve.afectados.map((a) => a.imagen))].sort();
  const sobrantes = Math.max(0, cve.afectados.length - MAX_FILAS);
  const filas = cve.afectados.slice(0, MAX_FILAS).map(
    (a) =>
      `| ${escaparCelda(a.imagen)} | ${escaparCelda(a.paquete)} | ${escaparCelda(a.instalada)} | ${escaparCelda(a.corregida)} | ${escaparCelda(a.objetivo)} |`,
  );
  const id = escaparCelda(cve.id, 100);
  const enlace = cve.url ? `[${id}](${cve.url})` : id;
  const cuerpo = [
    marcador('imagenes', cve.clave),
    `## ${id} (${cve.severidad}) con corrección disponible`,
    '',
    `**Aviso:** ${enlace}${cve.titulo ? ` — ${escaparCelda(cve.titulo, 300)}` : ''}`,
    '',
    'Detectada por el escaneo diario de las imágenes desplegadas (`vigilancia-imagenes.yml`), con la base de CVE del día y sin reconstruir nada.',
    '',
    '| Imagen | Paquete | Versión instalada | Versión corregida | Dónde |',
    '|---|---|---|---|---|',
    ...filas,
    ...(sobrantes > 0 ? ['', `… y ${sobrantes} filas más (ver el resumen del run).`] : []),
    '',
    '### Qué hacer',
    '',
    '- Imagen de terceros (compose de producción, base del `Dockerfile`): subir el digest fijado a una versión que traiga la corrección (Dependabot de `docker` suele proponerlo).',
    '- Paquete instalado por el `Dockerfile` o dependencia NuGet en la imagen de la aplicación: reconstruir y desplegar, o subir la versión del paquete.',
    '- Si se acepta el riesgo (falso positivo, no aplicable), anotarlo en `.trivyignore` con su motivo: la issue se cerrará sola en el siguiente escaneo completo.',
    '',
    'Esta issue la mantiene el workflow: se actualiza si cambia lo afectado y se cierra sola cuando el CVE deja de aparecer. Cerrarla a mano como «completada» sin corregir nada la reabre en el siguiente escaneo; cerrarla como «no planificada» la deja cerrada.',
  ].join('\n');
  return {
    clave: cve.clave,
    titulo: `[Vigilancia imágenes] ${String(cve.id).replace(/[\r\n]+/g, ' ')} (${cve.severidad}) en ${imagenes.join(', ')}`.slice(0, 250),
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

  const plan = { crear: [], actualizar: [], reabrir: [], cerrar: [], duplicadas: [], aplazadas: [], conservadas: [], silenciadas: [] };
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
      // Cerrada a mano como «no planificada» (riesgo aceptado): no se reabre cada día.
      // Las que cierra el propio workflow van como «completada» y sí se reabren si vuelven.
      if (cerradas[0].state_reason === 'not_planned') plan.silenciadas.push({ number: cerradas[0].number, clave });
      else plan.reabrir.push({ number: cerradas[0].number, alerta });
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
    .map((i) => ({ number: i.number, state: i.state, state_reason: i.state_reason || null, title: i.title, body: i.body || '' }));
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
      // «completada», no «no planificada»: esta última la reserva planificar() para el riesgo
      // aceptado a mano, que no se reabre.
      await github.rest.issues.update({ owner, repo, issue_number: number, state: 'closed', state_reason: 'completed' });
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
  claveSegura,
  agruparTrivy,
  ordenarCves,
  resumirObjetivos,
  alertaDeImagenes,
  planificar,
  listarExistentes,
  aplicar,
};
