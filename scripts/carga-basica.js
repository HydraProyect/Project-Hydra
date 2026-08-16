// Prueba de carga base — P1-19 de docs/business/MATURITY_REVIEW.md: "prueba
// de carga base para tener una cifra defendible". No existía ninguna hasta
// ahora (ni k6, ni NBomber, ni nada) — greenfield.
//
// k6 (Grafana Labs, AGPLv3, ejecutable independiente) en vez de un paquete
// NuGet: se evaluó NBomber y se descartó por el mismo motivo que QuestPDF en
// ARCHITECTURE.md — su licencia exige suscripción comercial para cualquier
// uso más allá de personal (NBOMBER LICENSE AGREEMENT v3.0 § 2.1/2.3-b:
// "solely in connection with the Customer's internal operations", prohíbe
// SaaS/ASP). k6 es de verdad libre para este uso — se ejecuta como
// herramienta externa contra la app, no se distribuye ni se enlaza con ella.
//
// Deliberadamente pequeña y contra rutas anónimas (sin login): /salud (mero
// health check) y /cuenta/iniciar-sesion (primer render real de Blazor
// Server con prerenderizado SSR) — una carga con login+2FA+circuito
// interactivo es un experimento aparte y más caro de mantener; esto es la
// primera cifra defendible, no una suite de carga completa.
//
// Thresholds (auditoría de madurez 2026-08-16, P2 #14 heredado del informe
// anterior): tres runs consecutivos en CI (2026-08-15, mismo hardware de
// runner) dieron p(95) entre 1,9ms y 2,15ms y 0% de fallos sobre 910
// peticiones cada vez — la primera cifra real con la que fijar un umbral
// defendible. Los valores de abajo dejan ~100x de margen sobre lo observado
// a propósito: el objetivo es cazar una regresión real (una consulta N+1,
// un lock, un timeout) contra rutas anónimas en un runner de CI compartido,
// no reproducir un SLA de producción con tráfico real. El job sigue con
// continue-on-error a nivel de job (ver ci.yml) — un umbral roto se ve en
// rojo en el resumen, pero no bloquea el merge todavía: hace falta más
// historial bajo distintas condiciones de runner antes de dar ese paso.
import http from "k6/http";
import { check, sleep } from "k6";

export const options = {
  scenarios: {
    carga_basica: {
      executor: "ramping-vus",
      startVUs: 0,
      stages: [
        { duration: "15s", target: 10 },
        { duration: "30s", target: 10 },
        { duration: "15s", target: 0 },
      ],
    },
  },
  thresholds: {
    http_req_failed: ["rate<0.01"],
    http_req_duration: ["p(95)<200"],
    checks: ["rate>0.99"],
  },
};

const BASE_URL = __ENV.BASE_URL || "http://127.0.0.1:5000";

export default function () {
  const respuestaSalud = http.get(`${BASE_URL}/salud`);
  check(respuestaSalud, {
    "/salud responde 200": (r) => r.status === 200,
  });

  const respuestaLogin = http.get(`${BASE_URL}/cuenta/iniciar-sesion`);
  check(respuestaLogin, {
    "/cuenta/iniciar-sesion responde 200": (r) => r.status === 200,
  });

  sleep(1);
}
