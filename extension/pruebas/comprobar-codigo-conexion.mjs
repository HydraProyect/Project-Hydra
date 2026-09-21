// Comprobación cruzada: lo que empaqueta CodigoConexionExtension.cs, ¿lo lee
// leerCodigoConexion de extension/background.js? Los dos lados están fijados
// por separado (el C# por CodigoConexionExtensionTests, el JS por esto), así
// que un cambio en cualquiera de los dos rompe uno de los dos sitios.
import { readFileSync } from "node:fs";

globalThis.chrome = {
  runtime: {
    onMessage: { addListener() {} },
    onMessageExternal: { addListener() {} },
    getManifest: () => ({ version: "0.3.0" }),
  },
};

const ruta = process.argv[2];
if (!ruta) {
  console.error("Uso: node comprobar-codigo-conexion.mjs <ruta a background.js>");
  process.exit(2);
}

const fuente = readFileSync(ruta, "utf8");
(0, eval)(fuente);

let fallos = 0;
let total = 0;
function comprobar(nombre, condicion) {
  total++;
  console.log(`${condicion ? "OK  " : "MAL "} ${nombre}`);
  if (!condicion) fallos++;
}

// El JSON exacto que CodigoConexionExtensionTests deja fijado.
const json = JSON.stringify({
  u: "https://staging.talveg.es/",
  t: "CfDJ8ejemplo-inventado.no-es-un-token-real",
  e: "2026-09-21T12:00:00.0000000Z",
});
const codigo = Buffer.from(json, "utf8").toString("base64");

const leido = leerCodigoConexion(codigo);
comprobar("lee el código que produce C#", leido !== null);
comprobar("conserva el origen", leido?.hydraUrl === "https://staging.talveg.es/");
comprobar("conserva el token intacto", leido?.t === undefined && leido?.token === "CfDJ8ejemplo-inventado.no-es-un-token-real");
comprobar("la caducidad se interpreta como UTC", Date.parse(leido?.expiraEnUtc) === Date.UTC(2026, 8, 21, 12, 0, 0));

comprobar("tolera espacios alrededor al pegar", leerCodigoConexion(`\n  ${codigo}  \n`) !== null);
comprobar("rechaza texto que no es base64", leerCodigoConexion("esto no es un código") === null);
comprobar("rechaza base64 que no es JSON", leerCodigoConexion(Buffer.from("hola", "utf8").toString("base64")) === null);
comprobar("rechaza JSON sin token", leerCodigoConexion(Buffer.from(JSON.stringify({ u: "https://x", e: "2026-09-21T12:00:00Z" }), "utf8").toString("base64")) === null);
comprobar("rechaza una caducidad que no es fecha", leerCodigoConexion(Buffer.from(JSON.stringify({ u: "https://x", t: "y", e: "mañana" }), "utf8").toString("base64")) === null);
comprobar("rechaza la cadena vacía", leerCodigoConexion("") === null);

// Control positivo del propio instrumento: si el decodificador fuera un
// "return siempre null", todas las comprobaciones de rechazo pasarían igual.
// Esta es la que lo desmiente.
comprobar("CONTROL: el decodificador no devuelve null siempre", leerCodigoConexion(codigo)?.token?.length > 0);

console.log(fallos === 0 ? "\nTODO OK" : `\n${fallos} FALLO(S)`);
console.log(`${total} comprobaciones ejecutadas.`);
process.exit(fallos === 0 ? 0 : 1);
