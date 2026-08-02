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
// Sin thresholds que fallen el build a propósito (mismo criterio que la
// cobertura de código, ver ci.yml): con cero cifras previas, un umbral
// inventado no sería defendible. El resumen impreso al final es la primera
// cifra real con la que decidir uno.

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
