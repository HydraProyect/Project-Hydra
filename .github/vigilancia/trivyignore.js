'use strict';

// Guarda de caducidad del `.trivyignore` de la raíz (mejora 1.6 de la evaluación del
// harness, 2026-11-05). Trivy lee ese fichero en el gate de PR (ci.yml, job
// docker-build), en el escaneo de la imagen firmada (deploy.yml) y en la vigilancia
// diaria (vigilancia-imagenes.yml). Una excepción sin fecha se queda para siempre; por
// eso cada entrada debe llevar:
//
//   # motivo: <por qué se acepta el riesgo>
//   CVE-AAAA-NNNNN exp:AAAA-MM-DD
//
// - `exp:` es la caducidad NATIVA de Trivy: pasada la fecha, Trivy deja de ignorar la
//   entrada y el gate vuelve a fallar solo;
// - el comentario `# motivo:` va en la línea inmediatamente anterior y no puede ir vacío;
// - esta guarda falla si alguna entrada no tiene `exp:` o motivo, si ya ha caducado
//   (la entrada caducada no protege nada y engaña a quien lee el fichero) o si caduca
//   a más de MAX_DIAS días: una excepción se revisa, no se renueva a cien años.
//
// Uso: node .github/vigilancia/trivyignore.js [ruta]   (por defecto, .trivyignore)
// Pruebas: node --test .github/vigilancia/*.test.js (las corre ci.yml).

const MAX_DIAS = 90;
const ENTRADA = /^(\S+)\s+exp:(\d{4}-\d{2}-\d{2})\s*$/;
const ID_VALIDO = /^[A-Za-z0-9][A-Za-z0-9._:-]*$/;
const MOTIVO = /^#\s*motivo:\s*(\S.*)$/i;

function fechaValida(texto) {
  const d = new Date(`${texto}T00:00:00Z`);
  return !Number.isNaN(d.getTime()) && d.toISOString().slice(0, 10) === texto ? d : null;
}

// hoy: 'AAAA-MM-DD' (UTC). Devuelve { entradas: [{ id, exp, motivo, linea }], errores: [texto] }.
function validar(texto, hoy) {
  const diaHoy = fechaValida(hoy);
  if (!diaHoy) throw new Error(`Fecha de hoy no válida: ${hoy}`);
  const limite = new Date(diaHoy.getTime() + MAX_DIAS * 86400000).toISOString().slice(0, 10);
  const lineas = String(texto).split(/\r?\n/);
  const entradas = [];
  const errores = [];

  lineas.forEach((bruta, i) => {
    const linea = bruta.trim();
    const n = i + 1;
    if (linea === '' || linea.startsWith('#')) return;
    const m = ENTRADA.exec(linea);
    if (!m) {
      errores.push(`línea ${n}: «${linea}» no tiene la forma «<id> exp:AAAA-MM-DD».`);
      return;
    }
    const [, id, exp] = m;
    if (!ID_VALIDO.test(id)) errores.push(`línea ${n}: identificador no válido «${id}».`);
    if (!fechaValida(exp)) {
      errores.push(`línea ${n}: ${id} lleva una fecha de caducidad que no existe (${exp}).`);
      return;
    }
    const anterior = i > 0 ? lineas[i - 1].trim() : '';
    const motivo = MOTIVO.exec(anterior);
    if (!motivo) errores.push(`línea ${n}: ${id} no va precedida de «# motivo: <por qué se acepta>».`);
    if (exp < hoy) errores.push(`línea ${n}: ${id} caducó el ${exp}: corregir la vulnerabilidad o renovar la excepción con un motivo nuevo.`);
    if (exp > limite) errores.push(`línea ${n}: ${id} caduca el ${exp}, a más de ${MAX_DIAS} días (límite ${limite}).`);
    entradas.push({ id, exp, motivo: motivo ? motivo[1].trim() : '', linea: n });
  });

  return { entradas, errores };
}

function principal(argv, { leer, hoy, log, error }) {
  const ruta = argv[0] || '.trivyignore';
  let texto;
  try {
    texto = leer(ruta);
  } catch (e) {
    if (e.code === 'ENOENT') {
      error(`No existe ${ruta}: sin el fichero la guarda no puede comprobar nada.`);
      return 1;
    }
    throw e;
  }
  const { entradas, errores } = validar(texto, hoy);
  for (const e of errores) error(`${ruta}: ${e}`);
  if (errores.length > 0) return 1;
  log(`${ruta}: ${entradas.length} excepción(es), todas con motivo y vigentes hasta como mucho ${MAX_DIAS} días.`);
  for (const e of entradas) log(`  ${e.id} caduca el ${e.exp}: ${e.motivo}`);
  return 0;
}

if (require.main === module) {
  const fs = require('fs');
  process.exitCode = principal(process.argv.slice(2), {
    leer: (r) => fs.readFileSync(r, 'utf8'),
    hoy: new Date().toISOString().slice(0, 10),
    log: (m) => console.log(m),
    error: (m) => console.log(`::error::${m}`),
  });
}

module.exports = { MAX_DIAS, validar, principal };
