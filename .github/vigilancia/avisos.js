'use strict';

// Capa B de la vigilancia de vulnerabilidades (vigilancia-avisos.yml): cruza el
// catálogo CISA KEV (vulnerabilidades explotadas de verdad) y los avisos de OSV (que
// incluyen los de GitHub, GHSA) con el inventario de .github/vigilancia/inventario.json.
// A diferencia de la capa A (Trivy sobre las imágenes), alerta aunque no haya parche:
// lo que importa aquí es saber cuanto antes que algo que corre en el VPS se está
// explotando o tiene un aviso crítico.
//
// Funciones puras: el workflow descarga, lee ficheros y llama a la API de GitHub; aquí
// solo se decide. Pruebas: node --test .github/vigilancia/*.test.js

const { claveSegura, escaparCelda, urlSegura, marcador } = require('./alertas.js');

const MIN_ENTRADAS_KEV = 1000;

// Política de conciliación por capa (alertas.planificar). KEV es un suceso: no se
// cierra ni se reabre sola, se cierra a mano tras el triaje. OSV es un estado: se
// cierra cuando deja de afectar y se reabre si vuelve (salvo cerrada como «no
// planificada»). El workflow la consume; las pruebas la fijan.
const POLITICA_CAPAS = Object.freeze({
  kev: Object.freeze({ completo: false, reabrirCerradas: false }),
  osv: Object.freeze({ completo: true, reabrirCerradas: true }),
});

function regex(patron, donde) {
  try {
    return new RegExp(patron, 'i');
  } catch (e) {
    throw new Error(`Expresión no válida en ${donde}: ${patron}`);
  }
}

// Lee la versión de cada componente del fichero que la fija. Un patrón que ya no casa
// (alguien cambió el formato del compose o del Dockerfile) es un error, no un «sin
// versión»: si no, la consulta a OSV dejaría de hacerse en silencio. Ese error se
// devuelve en `errorVersion` del componente y no se lanza: KEV no necesita la versión y
// debe seguir avisando; el workflow pone el job en rojo y no cierra nada de OSV.
function resolverVersiones(inventario, leerFichero) {
  if (!inventario || !Array.isArray(inventario.componentes) || inventario.componentes.length === 0) {
    throw new Error('El inventario no tiene componentes.');
  }
  const ids = new Set();
  return inventario.componentes.map((c) => {
    if (!/^[a-z0-9-]+$/.test(String(c.id))) throw new Error(`Componente con id no válido: ${c.id}`);
    if (ids.has(c.id)) throw new Error(`Componente repetido: ${c.id}`);
    ids.add(c.id);
    if (!Array.isArray(c.kev) || c.kev.length === 0) throw new Error(`${c.id} no tiene criterios KEV.`);
    const kev = c.kev.map((k, i) => ({
      vendor: regex(k.vendor, `${c.id}.kev[${i}].vendor`),
      producto: regex(k.producto, `${c.id}.kev[${i}].producto`),
    }));
    let version = null;
    let errorVersion = null;
    if (c.version) {
      const patron = regex(c.version.patron, `${c.id}.version.patron`);
      let texto = null;
      try {
        texto = leerFichero(c.version.fichero);
      } catch (e) {
        errorVersion = `No se puede leer ${c.version.fichero} para la versión de ${c.id}: ${e.message}`;
      }
      if (texto !== null) {
        const m = patron.exec(texto);
        if (!m || !m[1]) errorVersion = `No se encuentra la versión de ${c.id} en ${c.version.fichero}.`;
        else version = m[1];
      }
    }
    const osv = (c.osv || []).map((p) => ({ ecosystem: String(p.ecosystem), name: String(p.name) }));
    if (osv.length > 0 && !c.version) throw new Error(`${c.id} tiene paquetes OSV pero no versión.`);
    return { id: c.id, nombre: c.nombre || c.id, version, errorVersion, kev, osv };
  });
}

function validarKev(catalogo) {
  if (!catalogo || !Array.isArray(catalogo.vulnerabilities) || catalogo.vulnerabilities.length < MIN_ENTRADAS_KEV) {
    const n = catalogo && Array.isArray(catalogo.vulnerabilities) ? catalogo.vulnerabilities.length : 'ninguna';
    throw new Error(`El catálogo KEV no tiene la forma esperada (entradas: ${n}).`);
  }
  return catalogo;
}

// Entradas KEV añadidas en la ventana que casan con algún componente. Fuera de la
// ventana no se alerta: el catálogo es histórico y abrir cientos de issues de CVE de
// hace años (ya corregidos en las versiones que corren) no despierta a nadie.
function cruzarKev(catalogo, componentes, ahora, ventanaDias) {
  validarKev(catalogo);
  const desde = ahora - ventanaDias * 86400000;
  const cruces = [];
  const vistas = new Set();
  for (const e of catalogo.vulnerabilities) {
    const fecha = Date.parse(e.dateAdded);
    if (!Number.isFinite(fecha) || fecha < desde) continue;
    const afectados = componentes.filter((c) =>
      c.kev.some((k) => k.vendor.test(String(e.vendorProject || '')) && k.producto.test(String(e.product || ''))),
    );
    if (afectados.length === 0) continue;
    // Un catálogo con el mismo CVE dos veces (o dos ids que sanean a la misma clave)
    // no debe tumbar la capa entera en planificar: se queda la primera.
    const clave = claveSegura(String(e.cveID || '').trim());
    if (vistas.has(clave)) continue;
    vistas.add(clave);
    cruces.push({ entrada: e, componentes: afectados });
  }
  return cruces.sort((a, b) => String(a.entrada.cveID).localeCompare(String(b.entrada.cveID)));
}

function alertaKev({ entrada: e, componentes }) {
  const id = String(e.cveID || '').trim();
  const clave = claveSegura(id);
  const nombres = componentes.map((c) => c.nombre);
  const cuerpo = [
    marcador('kev', clave),
    `## ${escaparCelda(id, 100)} en el catálogo CISA KEV (explotada activamente)`,
    '',
    `**${escaparCelda(e.vendorProject)} ${escaparCelda(e.product)}**: ${escaparCelda(e.vulnerabilityName, 300)}`,
    '',
    escaparCelda(e.shortDescription, 1500),
    '',
    '| Campo | Valor |',
    '|---|---|',
    `| Añadida al catálogo | ${escaparCelda(e.dateAdded)} |`,
    `| Plazo CISA (agencias federales) | ${escaparCelda(e.dueDate)} |`,
    `| Uso conocido en ransomware | ${escaparCelda(e.knownRansomwareCampaignUse)} |`,
    `| Acción requerida | ${escaparCelda(e.requiredAction, 500)} |`,
    `| Componentes del inventario que casan | ${nombres.map((n) => escaparCelda(n)).join('; ')} |`,
    '',
    `Aviso: https://nvd.nist.gov/vuln/detail/${encodeURIComponent(id)}`,
    '',
    '### Qué hacer',
    '',
    '- Comprobar si la versión que corre (VPS, imágenes) está afectada: el cruce es por fabricante y producto, no por versión.',
    '- Si lo está: parchear o mitigar ya, aunque no haya versión corregida (es lo que KEV significa).',
    '- Cerrar la issue a mano tras el triaje. Esta capa no la cierra ni la reabre sola: una entrada KEV no desaparece del catálogo.',
  ].join('\n');
  return {
    clave,
    titulo: `[Vigilancia KEV] ${id.replace(/[\r\n]+/g, ' ')} explotada: ${nombres.join(', ')}`.slice(0, 250),
    cuerpo,
    critica: true,
  };
}

// Crítico según el propio aviso: severidad CRITICAL de la base de GitHub (GHSA) o del
// ecosistema (Ubuntu, Debian), o una puntuación numérica de 9 o más. Un vector CVSS sin
// puntuación no se calcula aquí: esos avisos los cubre la severidad de la base.
function esCritico(vuln) {
  const ds = vuln && vuln.database_specific;
  if (ds && /^critical$/i.test(String(ds.severity || ''))) return true;
  for (const s of (vuln && vuln.severity) || []) {
    const score = String(s.score || '');
    if (/^critical$/i.test(score)) return true;
    if (/^[0-9]+(\.[0-9]+)?$/.test(score) && Number(score) >= 9) return true;
  }
  for (const a of (vuln && vuln.affected) || []) {
    const sev = a.ecosystem_specific && a.ecosystem_specific.severity;
    if (/^critical$/i.test(String(sev || ''))) return true;
  }
  return false;
}

// respuestas: [{ componente, paquete, vulns }] (vulns: la lista de /v1/query de OSV
// para la versión del componente). Agrupa por id de aviso y deja solo los críticos.
function cruzarOsv(respuestas) {
  const porId = new Map();
  for (const { componente, paquete, vulns } of respuestas) {
    if (!Array.isArray(vulns)) throw new Error(`Respuesta de OSV sin lista de avisos para ${paquete.name}.`);
    for (const v of vulns) {
      if (!v || !v.id || v.withdrawn || !esCritico(v)) continue;
      const clave = claveSegura(v.id);
      if (!porId.has(clave)) porId.set(clave, { clave, vuln: v, afectados: [] });
      porId.get(clave).afectados.push({ componente: componente.nombre, paquete: `${paquete.ecosystem}/${paquete.name}`, version: componente.version });
    }
  }
  return [...porId.values()].sort((a, b) => a.clave.localeCompare(b.clave));
}

function alertaOsv({ clave, vuln: v, afectados }) {
  const alias = (v.aliases || []).filter((a) => /^CVE-/.test(a));
  const filas = afectados.map((a) => `| ${escaparCelda(a.componente)} | ${escaparCelda(a.paquete)} | ${escaparCelda(a.version)} |`);
  const referencia = urlSegura(((v.references || []).find((r) => r.type === 'ADVISORY') || {}).url);
  const cuerpo = [
    marcador('osv', clave),
    `## ${escaparCelda(v.id, 100)}${alias.length ? ` (${alias.map((a) => escaparCelda(a, 40)).join(', ')})` : ''}: aviso crítico que afecta a la versión en uso`,
    '',
    escaparCelda(v.summary || v.details || '', 500),
    '',
    '| Componente | Paquete | Versión en uso |',
    '|---|---|---|',
    ...filas,
    '',
    `Aviso: https://osv.dev/vulnerability/${encodeURIComponent(v.id)}${referencia ? ` · ${referencia}` : ''}`,
    '',
    'Detectado por `vigilancia-avisos.yml` consultando OSV con la versión que fija el repositorio, haya o no corrección publicada.',
    '',
    'Esta issue se cierra sola cuando el aviso deja de afectar a las versiones del inventario (tras una consulta completa). Cerrada a mano como «no planificada» (riesgo aceptado), no se reabre.',
  ].join('\n');
  return {
    clave,
    titulo: `[Vigilancia OSV] ${String(v.id).replace(/[\r\n]+/g, ' ')} crítico en ${[...new Set(afectados.map((a) => a.componente))].join(', ')}`.slice(0, 250),
    cuerpo,
    critica: true,
  };
}

module.exports = { MIN_ENTRADAS_KEV, POLITICA_CAPAS, resolverVersiones, validarKev, cruzarKev, alertaKev, esCritico, cruzarOsv, alertaOsv };
