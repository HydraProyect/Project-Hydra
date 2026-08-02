# BENCHMARK_PRECIOS_CAE — Costes de mercado de plataformas y servicios CAE

**Tipo**: Operativo (insumo de `COMPETITOR_ANALYSIS.md` y `PRICING.md`)
**Estado**: Draft — primera compilación con fuentes públicas (2026-07-30). Ninguna cifra está confirmada como decisión de negocio.
**Propósito**: Reunir en un solo lugar los precios verificables del mercado CAE español (plataformas inbound, servicios de externalización, rangos orientativos) para que `PRICING.md` y `COMPETITOR_ANALYSIS.md` trabajen sobre datos con fuente, no sobre supuestos. Distingue explícitamente dato verificado, dato secundario y estimación.

## Qué pertenece aquí

- Precios públicos verificados de plataformas CAE y servicios de externalización, con fuente y fecha de consulta.
- Rangos orientativos de mercado y su nivel de fiabilidad ([V]/[S]/[E]).
- Método de obtención de datos no públicos (licitaciones, solicitudes directas) y su estado.

## Qué NO pertenece aquí

- El análisis competitivo que consume estos datos (posicionamiento, ventajas, debilidades) → `COMPETITOR_ANALYSIS.md`.
- Las tarifas propias de Hydra → `PRICING.md`.
- Las plantillas de solicitud de información usadas para obtener datos no públicos → `PLANTILLAS_SOLICITUD_PRECIOS.md`.

## Método y niveles de fiabilidad

Cada cifra lleva una etiqueta de fiabilidad:

| Nivel | Significa |
|---|---|
| **[V]** Verificado | Publicado por el propio proveedor en su web o tienda oficial, consultado el 2026-07-30. |
| **[S]** Secundario | Publicado por un tercero (comparadoras, guías de competidores). Puede estar desactualizado o sesgado — en particular, las guías publicadas por un competidor (ej. la guía de Twind) tienen incentivo a encuadrar los precios ajenos de forma desfavorable. |
| **[E]** Estimación | Rango orientativo sin fuente primaria. Solo sirve para dimensionar, nunca para decidir pricing. |

Referencia interna ya disponible y no incluida aquí: el precio real que Geseme paga a Twind (dato de primera mano, banda consultora — es el ancla más valiosa y vive fuera de este documento por confidencialidad de fuente).

## 1. Plataformas inbound — precios al contratista (quien sube documentación)

Este es el lado del mercado que más publica precios, porque es autoservicio. **No es la banda de Hydra** (nuestro ICP es la consultora/titular), pero define el coste que soportan las contratas de nuestros clientes y el argumento de fricción del modelo actual.

Nalanda mantiene **dos páginas públicas de tarifas simultáneas** para el lado proveedor/contratista (verificadas ambas el 2026-08-02): la de "servicios premium para contratistas" (FREE/STARTER/ADVANCED) y la de "tarifas y packs" (packs Básico→Platino + niveles de servicio). No está claro cuál sustituye a cuál; se registran ambas.

| Plataforma | Oferta | Precio | Fiabilidad | Fuente |
|---|---|---|---|---|
| **Nalanda** (Once For All) | FREE (3 licencias, 3 expedientes, sin soporte tel.) | 0 €/año | [V] | nalandaglobal.com — tarifas contratistas |
| **Nalanda** | STARTER (5 licencias, 3 expedientes, soporte) | 875 €/año | [V] | ídem |
| **Nalanda** | ADVANCED (licencias y expedientes ilimitados) | 2.000 €/año | [V] | ídem |
| **Nalanda** | Add-on revisión de urgencias | +525 €/año (Starter) / +1.800 €/año (Advanced) | [V] | ídem |
| **Nalanda** | PACK BÁSICO (1 visibilidad de contratista, 5 trabajadores/máquinas validados/año) | 420 €/año (precio "de lista" mostrado: 945 €) | [V] | nalandaglobal.com — tarifas y packs |
| **Nalanda** | PACK PLATA (hasta 3 contratistas, 15 elementos/año) | 640 €/año (lista: 1.655 €) | [V] | ídem |
| **Nalanda** | PACK ORO (hasta 5 contratistas, 50 elementos/año) | 1.265 €/año (lista: 2.790 €) | [V] | ídem |
| **Nalanda** | PACK PLATINO (hasta 10 contratistas, 100 elementos/año) | 2.455 €/año (lista: 4.990 €) | [V] | ídem |
| **Nalanda** | AUTÓNOMO BÁSICO (autónomo + 1 máquina) | 225 €/año (lista: 710 €) | [V] | ídem |
| **Nalanda** | Niveles de servicio: Min / Plus / Pro / Max | 0 € / 199 € / 500 € / 780 €/año | [V] | ídem |
| **Nalanda** | Cuota de alta, renovación y reenganche | 250 € cada una | [V] | ídem |
| **Nalanda** | Ampliaciones: visibilidad adicional 190 €/año; pack 5 trabajadores +85 €/año; urgencias 60/160/240 €; nóminas x15 trabajadores 180 €/año | ver detalle | [V] | ídem |
| **Dokify** (Once For All) | Licencia Temporal (máx. 3 elementos, 5 días de carga) | 34,99 € | [V] | dokify.net/cae/tarifas |
| **Dokify** | Licencia Premium contrata | desde 102,90 €/año + 15,44 €/elemento (empleado o máquina)/año | [V] | ídem (calculadora: 1 elemento = 118,34 €/año) |
| **CTAIMA** | Licencia usuario adicional avanzado (la 1ª es gratuita) | 150 €/lic/año (145 € a partir de 5) | [V] | store.ctaima.com |
| **CTAIMA** | Control de presencia de trabajadores (suscripción anual) | 100 €/año (precio tachado 200 €) | [V] | ídem |
| **CTAIMA** | Plugin integración Forms + 500 submits | 750 € | [V] | ídem |
| **Metacontratas** | Planes subcontratista Basic / One / Prime | importes no publicados | [S] | ComparaSoft |

Lectura de negocio: el modelo dominante monetiza al contratista con cuota de alta + variable por trabajador/máquina **y por "visibilidad"** (cada cliente-titular que puede ver tu documentación es una unidad de cobro: ~180 €/año por visibilidad en la estructura de packs de Nalanda, 190 € como ampliación suelta). El dolor multi-cliente está monetizado unidad a unidad. Los add-ons de urgencia son caros (la "urgencia" de Nalanda cuesta hasta el 90% de la licencia base en el modelo STARTER/ADVANCED). Nótese también la mecánica de "precio de lista tachado" con descuentos aparentes del ~50% en todos los packs — teatro de anclaje, no descuento real. Las quejas públicas más repetidas (Trustpilot, GoWork, comparadoras) son renovaciones automáticas opacas, cobro por elementos dados de baja y subidas de cuota sin preaviso — munición directa de posicionamiento para Hydra.

## 1-bis. Konvergia — la capa de interoperabilidad (no es una plataforma, es una red)

Qué es, verificado en la página oficial de Nalanda (2026-08-02): una red de plataformas CAE asociadas en la que un documento subido a cualquiera de ellas se replica automáticamente en las demás (bajo control del usuario: enviar, recibir o ambos). Cada plataforma sigue validando con sus propios criterios — Konvergia mueve el documento, no la validación. Existe además un panel de estado unificado en konvergia.com **con tarifa propia** (importe no publicado en la página de Nalanda — pendiente de verificar en konvergia.com).

Matices relevantes frente a la descripción informal "Nalanda replica a las demás":

1. **La replicación es bidireccional y multi-origen**: se puede subir en cualquier plataforma asociada, no solo en Nalanda.
2. **En Nalanda, Konvergia está detrás del nivel de servicio más caro**: activar Konvergia exige el Pack de Nivel Max (780 €/año). La interoperabilidad se vende como premium, no como básico.
3. **Membresía verificada** (AECER, 2026-08-02): Dokify, Tesicnor, Construred, Ecogestor, UCAE, SGRed, 6conecta, Metacontratas y Nalanda. **CTAIMA/Twind NO es miembro** — mantiene su propia API REST documentada. Konvergia está liderada por Juan Medina como CEO — la misma persona que figura como CEO de Dokify (Once For All), lo que sitúa la gobernanza de la red en la órbita del grupo competidor principal.
4. **No existe API pública de Konvergia** (verificado 2026-08-02: sin portal de desarrolladores, sin documentación técnica pública, sin modelo de consumo por uso). El acceso es por dos vías: como usuario final a través de una plataforma miembro, o como **software que se adhiere a la red como socio** — AECER describe Konvergia como "hub de integración de todos aquellos softwares que decidan unirse". Es decir: la vía de entrada para un tercero como Hydra es una negociación de adhesión (business development), no un contrato de API.

Implicación estratégica para Hydra (pregunta abierta, no decisión): si la documentación de las contratas fluye por Konvergia, una consultora que opere con Hydra puede exigir que Hydra lea/escriba contra esa red o contra sus plataformas miembro. Eso convierte "¿Hydra dentro o fuera de Konvergia?" en una decisión de producto y de canal, no solo técnica. Tres datos condicionan la respuesta: (a) la entrada es por adhesión negociada con una red gobernada en parte por Once For All, no por API de pago; (b) **CTAIMA/Twind — la plataforma que usa el cliente fundador objetivo — está fuera de Konvergia**, por lo que la adhesión no cubriría el caso de integración más inmediato de Hydra, que pasa por la API REST propia de CTAIMA; (c) las contratas ya pueden activar Konvergia por sí mismas desde sus cuentas en plataformas miembro, de modo que Hydra puede orquestar valor encima sin ser miembro. Esta pregunta debe entrar en `PRODUCT_STRATEGY.md` (adherirse vs. integrar plataforma a plataforma vs. orquestar sin membresía) cuando se desarrolle — no se decide aquí.

**Nota sobre la migración CTAIMA → Twind y su API (verificado 2026-08-02):**
1. Twind es la nueva plataforma de CTAIMA Group que **unifica CTAIMACAE y e-coordina** en un único entorno — dato de consolidación relevante: e-coordina deja de ser un competidor independiente y pasa a la órbita de CTAIMA (corregir en la tabla de § 2 cuando se desarrolle `COMPETITOR_ANALYSIS.md`).
2. La migración es progresiva y está en curso durante 2026; los titulares con integraciones API/SSO son contactados por CTAIMA para "realizar las configuraciones necesarias antes de la actualización" — es decir, **no hay garantía pública de compatibilidad retroactiva**: las integraciones existentes se reconfiguran, no se mantienen sin cambios.
3. La propia página de producto de Twind anuncia "API REST documentada y SSO empresarial" como capacidad de la plataforma nueva, y existe un portal de desarrolladores del grupo (developers.ctaima.com, sobre Azure API Management) con registro para obtener claves; el catálogo concreto de APIs requiere cuenta para consultarse.
4. Implicación para Hydra: cualquier integración se diseña **contra la API de Twind desde el día uno**, nunca contra CTAIMACAE legacy. El riesgo residual (evolución de la API de Twind, que es plataforma joven) se mitiga en arquitectura con una capa adaptadora por conector — decisión técnica a formalizar en su momento como ADR referenciado desde `ARQUITECTURA-INTEGRACIONES.md`, no aquí.

**Nota de escenario (hipótesis fechada 2026-08-02, no driver de diseño)**: la adquisición de Hydra por un grupo consolidador del sector (Once For All ya ha adquirido Nalanda y Dokify) es un escenario de salida plausible *como consecuencia* de ganar el segmento consultora de forma independiente. Registrado aquí a efectos de memoria; su desarrollo, si procede, corresponde a `BUSINESS_MODEL.md` como nota de escenarios de salida. Diseñar el producto para ese comprador queda explícitamente descartado como estrategia (riesgo de comprador único, distorsión de roadmap, y el escenario simétrico más probable es la copia, no la compra).

## 2. Plataformas inbound — precios al titular / empresa principal

| Plataforma | Oferta | Precio | Fiabilidad | Fuente |
|---|---|---|---|---|
| **Dokify** | Licencia Enterprise empresa principal (implantación, formación, soporte a contratas, módulo obras incl.) | desde 490 €/año | [V] | dokify.net/cae/tarifas |
| **Dokify** | Pack control de acceso (hardware+servicio) | desde 21,13 €/mes | [V] | ídem |
| **UCAE** | Gestión documental | desde 49 €/mes | [S] | ComparaSoft (única plataforma española con precio público según esa fuente) |
| **UCAE** | CAE completa | desde 80 €/mes | [S] | ídem |
| **PlayCAE** | Tarifa plana (sin límite de trabajadores) | desde 199 €/mes | [S] | playcae.com (auto-publicado con intención comercial) |
| **DocuPRL** | Plan Starter | gratis hasta 5 trabajadores | [S] | docuprl.es (auto-publicado) |
| **Nalanda** | Licencia titular | desde ~150 €/mes hasta 10 contratas activas | [S] | guía de Twind — competidor, contrastar |
| **CTAIMA / Twind** | Titular | sin precio público; presupuesto a medida por nº empresas/volumen | [V] (ausencia verificada) | ctaima.com, ComparaSoft |
| **6conecta** | Titular | sin precio público; demo + presupuesto | [V] (ausencia verificada) | 6conecta.com |
| **e-coordina** | Titular | sin precio público | [V] (ausencia verificada) | e-coordina.es |

## 3. Rangos orientativos de mercado (titular)

Publicados por Twind en su guía comparativa — **[S] con sesgo potencial**, usar solo para dimensionar:

| Banda | Rango mensual | Volumen aproximado |
|---|---|---|
| Básica PYME | 80–150 €/mes | hasta 10–15 contratas |
| Intermedia | 150–400 €/mes | 15–50 contratas |
| Avanzada | 400–1.200 €/mes | 50–200 contratas |
| Enterprise | >1.200 €/mes | >200 contratas |

Costes adicionales citados en la misma fuente [S]: implantación 0–3.000 €, formación 0–1.500 € (a menudo incluida), migración de datos 500–2.000 €, personalización 1.000–10.000 €. Modelo tradicional por trabajador: 1–5 €/trabajador/mes [S].

## 4. Servicios de externalización CAE ("outbound") — el hueco del benchmark

Confirmado que existe un subsector de externalización (gestionar la CAE del contratista en las plataformas de sus clientes): **GesCAE** y **CoordinaPlus/Adding Plus** lo ofrecen explícitamente; ninguno publica tarifas. Este es exactamente el servicio que presta una consultora/SPA como Geseme, y es la banda de precio más relevante para el modelo de ingresos de nuestros clientes-consultora.

**Fuentes pendientes (requieren acción de Chris, no automatizable):**
1. Solicitudes de información con identidad real a GesCAE, CoordinaPlus y 2–3 SPAs, con perfil de volumen homogéneo (banda 50 clientes / 600 trabajadores) para que las cifras sean comparables — plantillas en `PLANTILLAS_SOLICITUD_PRECIOS.md`.
2. Tarifas internas o de mercado conocidas desde la operación de Geseme (dato de primera mano).

## 5. Licitación pública — pendiente con método concreto

La búsqueda genérica no devolvió adjudicaciones limpias de plataforma CAE con importe. Hay señal de que existen (ej. licitación 2026 de Logaritme AIE en TED que agrupa "Vigilancia y Seguridad y plataforma CAE"). Siguiente paso concreto: búsqueda dirigida en contrataciondelestado.es y TED con CPV de servicios de prevención/software y términos "coordinación de actividades empresariales", filtrando por estado "Adjudicada" — los pliegos adjudicados publican importe y adjudicatario, que es precio real pagado, no de lista.

## 6. Implicaciones preliminares para PRICING.md (hipótesis, no decisiones)

1. **La banda consultora no tiene precio público en ningún competidor** — todo lo que supere ~50 contratas se negocia a medida. Hydra puede diferenciarse con transparencia de precios en esa banda (como hace UCAE en la banda baja), o capturar margen con opacidad como los incumbentes. Trade-off abierto para `PRICING.md`.
2. **El resentimiento del contratista es un activo de posicionamiento**: el modelo "el contratista paga" genera las peores opiniones públicas del sector. La decisión de quién paga en el modelo Hydra (consultora paga todo vs. repercute a contratas) es tanto de pricing como de marca.
3. **Anclas cuantitativas ya utilizables**: un contratista mediano paga hoy 875–2.000 €/año (Nalanda, servicios premium) o 420–2.455 €/año + nivel de servicio 0–780 € + cuota de alta 250 € (Nalanda, packs) o ~100–500 €/año (Dokify, según elementos) por plataforma y por cliente que se la imponga. Con la estructura de packs, un contratista con 10 titulares y nivel Max supera los 3.400 €/año solo en Nalanda. El coste agregado multi-plataforma de una contrata con 4–5 titulares distintos es el dolor que las consultoras conocen de primera mano.
4. **La visibilidad como unidad de cobro es el precedente más relevante para el modelo consultora**: el mercado ya acepta pagar por cada relación cliente↔documentación (~180–190 €/año/visibilidad). Es un dato de disposición a pagar directamente trasladable a la banda de 50 clientes gestionados de Hydra — hipótesis a contrastar, no regla.
5. **Konvergia introduce una decisión estratégica nueva** (ver § 1-bis): la interoperabilidad entre plataformas ya existe como red gobernada en parte por los competidores. La postura de Hydra frente a esa red (adherirse, integrar plataforma a plataforma, o ignorar) condiciona `PRODUCT_STRATEGY.md` y `ARQUITECTURA-INTEGRACIONES.md`.

## Documentos relacionados

- `COMPETITOR_ANALYSIS.md` — destino natural de las tablas 1–3 cuando se desarrolle.
- `PRICING.md` — consumidor de las anclas de la sección 6.
- `GO_TO_MARKET.md` — consumidor de los argumentos de posicionamiento (sección 1, lectura de negocio).
- `ARQUITECTURA-INTEGRACIONES.md` — CTAIMA, Dokify y 6conecta aparecen ahí como proveedores de integración; este documento los trata como competidores (deslinde ya fijado en `COMPETITOR_ANALYSIS.md`).
- `PLANTILLAS_SOLICITUD_PRECIOS.md` — herramienta usada para obtener los datos pendientes de § 4.
