// Carga progresiva de 5 minutos contra STAGING — REC-196/P33, decisión D2 del
// propietario (2026-09-19): «carga progresiva de 5 min en staging, de
// madrugada, abortando ante degradación sostenida o errores en producción».
// La lanza a mano el workflow carga-staging.yml; el aborto por lo que pasa en
// PRODUCCIÓN lo decide scripts/carga-staging-vigia.sh, no este fichero.
//
// LO QUE ESTE FICHERO NO MIDE, dicho aquí para que nadie lo lea de más: k6
// contra rutas anónimas (/salud y el primer render de /cuenta/iniciar-sesion,
// como scripts/carga-basica.js) NO abre circuitos de Blazor Server (no
// establece la conexión SignalR ni hace login). Empuja HTTP, el prerenderizado
// SSR, el pool de Kestrel y la JIT/GC de la app de staging; no el coste de
// memoria de un circuito interactivo (ADR-008: ~1,8 MB por circuito), ni
// LibreOffice, ni las consultas de un usuario autenticado. Esa fase es
// CargaCircuitos --attach-url/--container contra staging y necesita credenciales
// del Tenant de demo de staging (fase 2, aún no preparada).
//
// Forma de la rampa (5 min exactos, suma de las etapas; VUS_MAX es el 100 %):
//   30 s   0 → 10 %     calentamiento (JIT, cachés)
//   60 s  10 → 25 %
//   60 s  25 → 50 %
//   60 s  50 → 100 %
//   60 s  se mantiene en 100 %   (la meseta que interesa para el pico)
//   30 s 100 → 0        bajada
// Cada VU pide las dos rutas y espera 1 s: con VUS_MAX = 20, del orden de 40
// peticiones/s en la meseta.
import http from "k6/http";
import { check, sleep } from "k6";

const VUS_MAX = Math.max(1, parseInt(__ENV.VUS_MAX || "20", 10));
const BASE_URL = (__ENV.BASE_URL || "https://staging.talveg.es").replace(/\/$/, "");

// Salvaguarda: este fichero solo debe apuntar a staging. Si alguien pasa la URL
// de producción por error, no arranca (el vigía comprueba lo mismo en shell).
if (!BASE_URL.includes("staging.")) {
  throw new Error(`BASE_URL debe ser staging, recibí: ${BASE_URL}`);
}

const pct = (p) => Math.max(1, Math.round((VUS_MAX * p) / 100));

export const options = {
  scenarios: {
    carga_staging: {
      executor: "ramping-vus",
      startVUs: 0,
      stages: [
        { duration: "30s", target: pct(10) },
        { duration: "60s", target: pct(25) },
        { duration: "60s", target: pct(50) },
        { duration: "60s", target: pct(100) },
        { duration: "60s", target: pct(100) },
        { duration: "30s", target: 0 },
      ],
      gracefulRampDown: "5s",
      gracefulStop: "5s",
    },
  },
  thresholds: {
    // Si STAGING se degrada de verdad, no se sigue empujando: k6 corta solo.
    // (Producción se vigila aparte, desde el vigía.)
    http_req_failed: [{ threshold: "rate<0.10", abortOnFail: true, delayAbortEval: "20s" }],
    http_req_duration: [{ threshold: "p(95)<5000", abortOnFail: true, delayAbortEval: "30s" }],
  },
};

export default function () {
  const salud = http.get(`${BASE_URL}/salud`, { timeout: "10s" });
  check(salud, { "/salud responde 200": (r) => r.status === 200 });

  const login = http.get(`${BASE_URL}/cuenta/iniciar-sesion`, { timeout: "10s" });
  check(login, { "/cuenta/iniciar-sesion responde 200": (r) => r.status === 200 });

  sleep(1);
}
