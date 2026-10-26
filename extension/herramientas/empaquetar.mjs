// Genera el zip de la extensión y lo comprueba. Lo ejecuta el CI (job
// «Arneses de la extensión de navegador»); también sirve en local.
//
//   node extension/herramientas/empaquetar.mjs --origen extension --salida salida-zip
//        [--base <carpeta-de-la-extension-en-la-rama-base>]
//
// No publica nada en ninguna tienda y no necesita secretos: la clave del
// manifiesto es pública y no hay clave de firma.
//
// Sale con 1 si el paquete no pasa `validarPaquete` o, con --base, si el
// contenido del paquete cambió respecto a la base sin subir la versión.

import { mkdirSync, writeFileSync, appendFileSync, existsSync } from "node:fs";
import { join, resolve } from "node:path";
import { empaquetarCarpeta, validarPaquete, comprobarVersionContraBase, sha256 } from "./empaquetado.mjs";

// ID que fija la clave pública de extension/manifest.json. Cambiarlo rompe todo
// lo que se haya configurado con él (origen permitido en el servidor, enlaces);
// por eso cambiarlo es una decisión, no un efecto colateral.
const ID_ESTABLE = "neojcncpbpfocmpfjfefakjledbfgjlj";

function argumentos(argv) {
  const mapa = {};
  for (let i = 0; i < argv.length; i += 2) {
    if (!argv[i].startsWith("--") || argv[i + 1] === undefined) throw new Error(`argumento inválido: ${argv[i]}`);
    mapa[argv[i].slice(2)] = argv[i + 1];
  }
  return mapa;
}

function principal() {
  const args = argumentos(process.argv.slice(2));
  const origen = resolve(args.origen ?? "extension");
  const salida = resolve(args.salida ?? "salida-zip");

  const actual = empaquetarCarpeta(origen);
  const problemas = validarPaquete(actual.zip, { idEsperado: ID_ESTABLE });

  if (args.base) {
    // Una base que no existe o no es la extensión es un fallo del CI, no un permiso.
    if (!existsSync(join(resolve(args.base), "manifest.json"))) {
      console.error(`ERROR: --base ${args.base} no contiene manifest.json`);
      process.exit(1);
    }
    const base = empaquetarCarpeta(resolve(args.base));
    const p = comprobarVersionContraBase({
      zipBase: base.zip,
      versionBase: base.version,
      zipActual: actual.zip,
      versionActual: actual.version,
    });
    if (p) problemas.push(p);
  }

  if (problemas.length) {
    for (const p of problemas) console.error(`ERROR: ${p}`);
    process.exit(1);
  }

  mkdirSync(salida, { recursive: true });
  const nombre = `talveg-extension-${actual.version}.zip`;
  writeFileSync(join(salida, nombre), actual.zip);
  const huella = sha256(actual.zip);
  console.log(`${nombre} (${actual.zip.length} bytes, ${actual.ficheros.length} ficheros) sha256=${huella}`);
  for (const f of actual.ficheros) console.log(`  ${f}`);

  if (process.env.GITHUB_STEP_SUMMARY) {
    appendFileSync(
      process.env.GITHUB_STEP_SUMMARY,
      `### Paquete de la extensión\n\n\`${nombre}\` · ${actual.ficheros.length} ficheros · sha256 \`${huella}\` · ID \`${ID_ESTABLE}\`\n`,
    );
  }
}

principal();
