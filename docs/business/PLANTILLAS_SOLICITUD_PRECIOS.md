# PLANTILLAS_SOLICITUD_PRECIOS — Solicitudes de información de mercado CAE

**Tipo**: Operativo (herramienta de trabajo de `COMPETITOR_ANALYSIS.md` / `BENCHMARK_PRECIOS_CAE.md`)
**Estado**: Draft — plantillas listas para uso, pendientes de personalización y envío por Chris.
**Propósito**: Plantillas homogéneas para solicitar información de precios a plataformas CAE y servicios de externalización, de forma que las respuestas sean comparables entre sí y la obtención del dato sea limpia (identidad real, sin pretextos). Alimenta la sección 4 y 5 de `BENCHMARK_PRECIOS_CAE.md`.

## Qué pertenece aquí

- Reglas de uso para mantener limpio el proceso de obtención de datos (identidad real, sin usar el nombre del empleador).
- Las plantillas de correo en sí, por segmento de proveedor.
- La tabla de seguimiento de envíos y respuestas.

## Qué NO pertenece aquí

- Los datos ya obtenidos por esta vía → `BENCHMARK_PRECIOS_CAE.md` § 4 (esta plantilla es la herramienta, el benchmark es el destino del dato).
- El análisis competitivo que consume esos datos → `COMPETITOR_ANALYSIS.md`.

---

## 0. Reglas de uso — leer antes de enviar

1. **Identidad real siempre.** Te presentas como quien eres: profesional del sector PRL/CAE desarrollando una solución de software para el sector. No inventas empresas, volúmenes ficticios de una empresa que no existe, ni interés de compra que no tienes.
2. **Nunca uses el nombre de Geseme.** Ni como remitente, ni como referencia, ni como "mi empresa está evaluando". Usar el nombre o la posición de tu empleador para obtener información en beneficio de Hydra rompe la frontera limpia de IP y roza la buena fe contractual — exactamente lo que tu briefing legal existe para proteger. Si algún día Geseme evalúa plataformas y te encarga pedir cotizaciones, ese es otro sombrero y otro correo.
3. **Perfil de volumen homogéneo.** Todas las solicitudes usan el mismo escenario de referencia (bloque A) para que las cifras sean comparables entre sí y con tu propuesta a Geseme.
4. **Expectativa realista de respuesta.** La petición transparente tiene tasa de respuesta menor que un pretexto — es el coste de hacerlo limpio. Se compensa con: (a) los precios públicos ya verificados, (b) el dato Twind/Geseme de primera mano, (c) la vía de licitación pública. Trata cada respuesta como bonus, no como bloqueante del benchmark.
5. **Registro.** Cada envío y respuesta se anota en la tabla de seguimiento (sección 5) con fecha, para que el dato entre en `BENCHMARK_PRECIOS_CAE.md` con etiqueta [V] y trazabilidad.

---

## A. Bloque de escenario de referencia (pegar en toda solicitud)

> Para poder comparar opciones, os agradecería una orientación de precio para este escenario tipo:
> - Organización que coordina la CAE de **50 empresas cliente**
> - Aproximadamente **600 trabajadores** documentados en total
> - Del orden de **80–120 centros de trabajo** activos
> - Gestión documental completa (ITA/RNT/RLC, formación, vigilancia de la salud, EPIs, maquinaria)
> - Interesa conocer: cuota base, variable por empresa/trabajador/centro, coste de implantación, y si existe modelo específico para consultoras que operan en nombre de terceros.

*(Los 80–120 centros son una derivación razonable del ratio 50/600 — ajusta si tu operativa real en Geseme sugiere otro ratio, pero fija uno y úsalo en todas.)*

---

## B. Plantilla 1 — Plataformas inbound sin precio público (6conecta, e-coordina, Metacontratas, Nalanda/Dokify banda titular, CoordinaPlus como plataforma)

**Asunto:** Consulta de tarifas orientativas — perfil consultora CAE

Hola,

Soy [nombre], profesional del sector de la prevención de riesgos laborales y la coordinación de actividades empresariales. Estoy desarrollando por mi cuenta un análisis del mercado español de soluciones CAE, como parte del diseño de una solución de software para el sector.

Me interesa entender cómo se estructura vuestra oferta para organizaciones que gestionan la CAE de múltiples empresas. Sé que es una petición atípica porque no os escribo como comprador inmediato, y entiendo perfectamente si prefereis no compartir cifras — en ese caso, me sería igual de útil conocer la estructura del modelo (qué variables determinan el precio) aunque sea sin importes.

[BLOQUE A]

Gracias de antemano por vuestro tiempo.

Un saludo,
[nombre y datos de contacto personales]

**Notas de uso:**
- La honestidad sobre el propósito ("análisis de mercado", "solución de software para el sector") filtra sola: quien responda, responde sabiendo quién eres. Eso convierte cada respuesta en dato limpio y utilizable.
- La petición de "estructura sin importes" es el plan B dentro del mismo correo — muchos comerciales comparten el modelo de variables aunque no den cifras, y para `PRICING.md` la estructura vale casi tanto como el número.

---

## C. Plantilla 2 — CTAIMA (caso especial: competidor Y potencial partner de integración)

**Asunto:** Interés en integración con vuestra plataforma — información para desarrolladores

Hola,

Soy [nombre], desarrollador de una solución de gestión CAE orientada a consultoras del sector PRL. En nuestro diseño de integraciones contemplamos la conexión con las plataformas que nuestros futuros usuarios ya utilizan, y la vuestra es una de las principales del mercado.

Me gustaría conocer:
- Condiciones de acceso a vuestra API para integradores (documentación, licenciamiento, coste si lo hay).
- Precio de los niveles de acceso EXTRA y ADVANTAGE publicados en developers.ctaima.com, y si el nivel se contrata por organización o por cliente final.
- Si las APIs 1.0 publicadas hoy en el portal (Gestión Documental, Gestión de Contratos, Gestión de clientes, etc.) se mantienen compatibles en Twind, o si habrá una versión nueva y con qué calendario.
- Si el acceso API está disponible para un software de terceros que opera en nombre de clientes comunes (caso consultora que gestiona la CAE de varias empresas), o solo para el titular de cada cuenta.
- Si existe un programa de partners tecnológicos y sus condiciones.
- Orientativamente, la estructura de precios que un cliente común (perfil consultora) tendría con vosotros, para dimensionar el caso de integración.

[BLOQUE A]

Un saludo,
[nombre y datos de contacto]

**Notas de uso:**
- Este ángulo es honesto porque la integración con CTAIMA **está realmente en tu roadmap** (`ARQUITECTURA-INTEGRACIONES.md`). No es un pretexto: es tu interés real como integrador, y de paso dimensiona precios.
- Ojo al doble filo: al presentarte como desarrollador de una solución CAE te estás dando a conocer ante el competidor principal antes de tener nombre comercial. Valora si prefieres esperar a tener la marca comercial decidida (el nombre "Hydra" no debe aparecer — es codename interno). Decisión tuya de timing, no de ética.

---

## D. Plantilla 3 — Servicios de externalización CAE (GesCAE, CoordinaPlus/Adding Plus como servicio, otros SPAs)

**Asunto:** Consulta sobre estructura de tarifas de externalización CAE

Hola,

Soy [nombre], profesional del sector PRL/CAE. Estoy realizando un análisis independiente del mercado de servicios de coordinación de actividades empresariales en España, incluyendo el segmento de externalización de la gestión documental.

Me interesa entender cómo se tarifica habitualmente este servicio (por empresa gestionada, por trabajador, por plataforma destino, cuota fija...). No os escribo como cliente potencial inmediato, así que entiendo si no podéis compartir tarifas concretas — la estructura del modelo ya me sería muy útil.

[BLOQUE A]

Un saludo,
[nombre y datos de contacto]

**Notas de uso:**
- Este es el segmento donde compites indirectamente con tu propio empleador. Máxima limpieza aquí: identidad personal, propósito declarado, cero referencias a Geseme ni a clientes de Geseme.
- Fuente complementaria de mayor rendimiento para este segmento: adjudicaciones públicas de servicios de coordinación CAE (los pliegos publican el importe real por el que un SPA ganó el servicio). Método en `BENCHMARK_PRECIOS_CAE.md` § 5.

---

## E. Tabla de seguimiento de envíos

| Destinatario | Plantilla | Fecha envío | Respuesta | Dato obtenido | Etiqueta | Trasladado a benchmark |
|---|---|---|---|---|---|---|
| 6conecta | 1 | | | | | |
| e-coordina | 1 | | | | | |
| Metacontratas | 1 | | | | | |
| Nalanda (titular) | 1 | | | | | |
| Dokify (banda alta) | 1 | | | | | |
| CTAIMA | 2 | | | | | |
| GesCAE | 3 | | | | | |
| CoordinaPlus | 3 | | | | | |
| [SPA a identificar] | 3 | | | | | |

Regla de registro: una respuesta con cifra entra en `BENCHMARK_PRECIOS_CAE.md` como [V] con fecha; una respuesta solo de estructura entra como nota cualitativa; una no-respuesta a los 10 días laborables se marca y no se persigue más de una vez.

## Documentos relacionados

- `BENCHMARK_PRECIOS_CAE.md` — destino de los datos obtenidos (secciones 2 y 4).
- `COMPETITOR_ANALYSIS.md` — consumidor final del benchmark.
- `ARQUITECTURA-INTEGRACIONES.md` — base del ángulo de integración de la plantilla 2.
- `docs/business/legal/` — el briefing legal de buena fe/pluriactividad que fundamenta la regla 2 de la sección 0 vive en la consulta legal preparada en esa carpeta, no como documento propio todavía.
