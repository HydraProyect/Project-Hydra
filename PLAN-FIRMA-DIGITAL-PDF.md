# Verificación de firmas digitales en PDF — análisis de carga y plan de implementación

Estado: **implementado en su mayor parte** (épica "Documentación mensual auto-validada", 2026-08-06): motor de verificación (`VerificadorFirmaPdfService`, BCL .NET + almacén FNMT fijado), entidades `FirmaDigitalDocumento`/`VerificacionDocumentoOficial`, `TipoDocumento.PerfilDocumentoOficial`, pipeline en la cola de análisis (`ValidacionDocumentoOficialService` + parsers deterministas por perfil), reencolado al renovar, bandeja de revisión extendida a documentos de Empresa y pestaña "Validación" del Documento. **Pendiente**: calibración de parsers y cadena real con PDFs de muestra (fuera del repo), confirmación RGPD para persistir firmante persona física, retirar `TieneFirma` del prompt IA (épica 2), y verificación oficial CEA/CSV (épica 3, requiere autorización RED). Las secciones siguientes son el análisis de decisión original, en el orden que pide `CLAUDE.md`: dominio → arquitectura → plataforma → implementación.

Responde a dos preguntas: **cuánto pesa** para la plataforma y **cuál es la mejor forma de implementarlo**.

---

## 1. Dominio: qué significa aquí "firma verificada"

Hoy la plataforma ya dice algo sobre firmas: `RevisionIaDocumento.TieneFirmaDetectada`, que la IA rellena mirando el PDF (`VerificacionIaDocumentoService`, línea 92). Es una **conjetura visual**: cuesta una llamada a un LLM y no prueba nada — un PDF con una imagen de una rúbrica escaneada le parece firmado, y un certificado de la TGSS sellado criptográficamente sin marca visible le parece que no.

La verificación criptográfica prueba tres cosas que la IA no puede saber:

| | Qué prueba |
|---|---|
| **Integridad** | El PDF no se ha modificado desde que se firmó. Un TC2 con el importe retocado en un editor deja de validar. |
| **Autoría** | Quién firmó, con nombre y NIF/CIF dentro del certificado (`serialNumber`/`organizationIdentifier`). |
| **Momento** | Cuándo, si lleva sello de tiempo cualificado (RFC 3161). No la fecha que alguien escribió en el documento. |

Lo que **no** prueba: que el contenido sea cierto, que el documento sea del tipo declarado, ni que las fechas de vigencia sean las correctas. Para eso sigue haciendo falta la lectura IA. Son complementarias, no sustitutivas — salvo en `TieneFirma`, donde la criptografía sustituye a la conjetura (ver § 5, fase 5).

**Casos reales del CAE que sí llegan firmados**: certificado de estar al corriente con la TGSS y con la AEAT, ITA, RNT/RLC descargados de la Seguridad Social, certificados de formación de servicios de prevención ajenos, aptos médicos de algunos SPA. Son PDFs con sello de órgano o certificado de representante.

**Consecuencia adicional para la extracción**: los PDFs de la Administración con firma válida llevan capa de texto digital. Para esos, la extracción no necesita OCR ni LLM — `PdfSharpExtractorTextoDigitalService` (ya existe) la saca determinista y gratis. La firma válida garantiza que *el documento* no cambió desde que se selló; nunca convierte en "verdad absoluta" una extracción hecha por IA — pero sí permite sustituirla por lectura directa del texto en los tipos que lo tienen.

**Segundo mecanismo de autenticidad, distinto de la firma: CSV/CEA.** Los documentos de la Seguridad Social llevan además un Código Electrónico de Autenticidad (CEA) — y los de otras Administraciones, un CSV — verificable contra el servicio del organismo emisor (SVID en el caso de la TGSS, huella de RNT/RLC en su trámite propio). Es complementario a la firma, no redundante: prueba que *el organismo emitió* ese documento, no solo que no se ha modificado. Queda fuera de este plan como fase comprometida (ver § 7): la vía seria son los servicios web para autorizados RED con sello electrónico de empresa (trámite administrativo, no código), y el scraping de la sede es frágil y de encaje legal dudoso como proceso masivo.

**Invariante propuesta**: el resultado de la verificación **nunca aprueba ni rechaza un Documento por sí solo**. Deja constancia, igual que `RevisionIaDocumento` (ver su comentario de clase: "nunca corrige nada por sí sola"). Un Gestor CAE decide.

**Y el resultado es una terna, no un booleano.** La mayoría de los documentos del CAE son escaneos o fotos sin ninguna firma digital, y eso no los hace inválidos:

- `SinFirma` — no hay diccionario de firma. Estado normal, sin aviso.
- `Valida` — firma presente, íntegra y (según fase) de confianza.
- `Invalida` — hay firma pero no cuadra: documento alterado tras firmar, certificado caducado en el momento de firmar, revocado, o cadena no confiable. **Esto sí es un aviso fuerte**: es el único caso en que la plataforma puede afirmar que alguien manipuló un documento.

**Y sobre la terna, un nivel de confianza documental** que resume cuánto puede fiarse un Gestor CAE (o una regla automática) de lo que dice el documento — niveles discretos con nombre, no un score numérico inventado:

| Nivel | Condición |
|---|---|
| `VerificadoOficialmente` | Firma válida **y** CSV/CEA contrastado con el organismo emisor (futuro, ver § 7) |
| `FirmaValida` | Firma íntegra y cadena de confianza correcta — suficiente para afirmar "no manipulado desde la emisión" |
| `FirmaValidaSinRevocacion` | Íntegra, pero la revocación no se pudo comprobar (degradación de § 4) |
| `SoloLectura` | Sin firma; los datos salen de la extracción IA/OCR, con su confianza propia (`RevisionIaDocumento.ConfianzaGeneral`) |
| `Manipulado` | Firma inválida — el único nivel que acusa |

Este nivel es lo que permite, tipo a tipo, decidir cuánta revisión humana hace falta (ver decisión § 6.1): el valor de negocio real está en que la documentación mensual recurrente (RNT, RLC, corriente TGSS/AEAT) — firmada siempre y con texto digital — pueda validarse de punta a punta sin que nadie la mire.

---

## 2. Arquitectura: dónde vive

No hace falta infraestructura nueva. La cola durable ya existe y encaja exactamente:

- `TrabajoAnalisisDocumento` (`src/CaeManager.Domain/DocumentosIa/`) — cola en PostgreSQL, en la misma transacción que crea el Documento, con reintentos (3) y recuperación de trabajos estancados.
- `ProcesadorAnalisisDocumentoHostedService` (`src/CaeManager.Infrastructure/DocumentosIa/`) — sondeo cada 5 s, elección de líder entre réplicas (`pg_try_advisory_lock`), un tenant a la vez con `AmbitoTenantExplicito`, aviso por campana al terminar.

Añadir un valor a `TipoAnalisisDocumento` (`VerificacionFirmaDigital`) y un `case` en `EjecutarAnalisisAsync` hereda todo eso gratis: durabilidad, reintentos, aislamiento por tenant, multi-réplica y notificación.

**Resultado como agregado propio**: `FirmaDigitalDocumento : EntidadConTenant`, 0..N por Documento (un PDF puede llevar varias firmas apiladas por incremental updates — típico en cadenas empresa → SPA → mutua).

**Frontera**: `IVerificadorFirmaPdfService` en Application, implementación en Infrastructure. Esto es lo que permite cambiar de motor sin tocar dominio ni UI si algún día hace falta (§ 3, opción A) — mismo criterio de "capacidades, no proveedores" de `ARQUITECTURA-INTEGRACIONES.md`.

---

## 3. Plataforma: qué motor de validación

### Opción A — DSS de la Comisión Europea (Java), como servicio aparte

El validador de referencia eIDAS. Gestiona solo el LOTL y las TSL nacionales, emite informes ETSI EN 319 102-1 (simple y detailed report), cubre PAdES B-B/B-T/B-LT/B-LTA además de XAdES y CAdES.

**El problema no es DSS, es el despliegue.** Hydra es hoy **un solo contenedor .NET en Railway** (`DEPLOY.md`). Meter DSS significa un segundo servicio con runtime Java: segundo Dockerfile, segundo deploy, segunda superficie de CVEs que parchear, una JVM que reserva cientos de MB de heap solo por existir, y resolver otra vez en el borde HTTP cosas que dentro del proceso ya están resueltas (secretos, red, trazas, tenant). Para una capacidad cuyo coste real de CPU son 20 ms (§ 4), es desproporcionado.

**Cuándo sí valdría la pena**: si hiciera falta emitir un *informe de validación oponible frente a terceros* (auditoría, litigio, requerimiento de la Inspección de Trabajo) o validar XAdES/CAdES sueltos, no solo PDFs.

### Opción B — .NET nativo *(recomendada)*

Todo lo necesario está en la BCL de .NET 10, sin NuGet nuevo:

| Pieza | Cómo |
|---|---|
| Verificar el CMS/PKCS#7 detached | `System.Security.Cryptography.Pkcs.SignedCms` (`SubFilter` `ETSI.CAdES.detached` o `adbe.pkcs7.detached`) |
| Sello de tiempo RFC 3161 | `System.Security.Cryptography.Pkcs.Rfc3161TimestampToken` |
| Cadena de confianza | `X509Chain` + `X509ChainPolicy.CustomTrustStore` (almacén propio con las CA de la TSL española, no el del sistema operativo) |
| Revocación | `X509RevocationMode.Online` (OCSP/CRL), con caché y timeout propios — ver § 4 |
| Localizar la firma en el PDF | `/ByteRange` y `/Contents` del diccionario de firma. **PDFsharp 6.2.4 ya está en el proyecto** (hoy se usa para crear firmas y extraer texto) |

Coste de licencia: 0 €. Coste por volumen: 0 €. Sin datos saliendo a terceros.

**Lo que hay que construir a mano** es la parte de "¿este certificado es *cualificado* según la lista de confianza europea?" — parsear el LOTL (`https://ec.europa.eu/tools/lotl/eu-lotl.xml`) y la TSL española. Pero eso es un **job semanal**, no un coste por documento: se amortiza entre todos los tenants y no aparece en la latencia de nada.

**El único riesgo técnico abierto** es si PDFsharp expone el diccionario de firma en lectura o solo en escritura. Es lo primero que hay que despejar (§ 5, fase 1). Alternativa si no: `BouncyCastle.Cryptography` (MIT) o un lector mínimo del trailer — 200 líneas, el `/ByteRange` es un array de 4 enteros en una estructura muy acotada.

> **Nota de licencia — iText no.** La recomendación habitual en tutoriales de C# (`SignatureUtil`/`PdfPKCS7` de iText 7/8) está descartada aquí: iText es **AGPL** — en un SaaS comercial cerrado obliga a licencia de pago o a liberar el código de la plataforma. Misma advertencia para cualquier NuGet "gratis" que se evalúe: comprobar licencia antes que API.

### Opción C — pyHanko (Python), como servicio aparte

`pyhanko.sign.validation` valida de verdad y es MIT, y un contenedor Python pesa bastante menos que una JVM. Pero sigue siendo un segundo runtime con el mismo coste operativo que A, y el trabajo de listas de confianza tampoco viene resuelto (hay que montarlo sobre `certvalidator`). No compensa frente a B.

### Opción D — API externa de pago *(descartada)*

Además del coste por volumen, **implica enviar el PDF completo a un tercero**. Esos PDFs contienen datos personales y de salud de trabajadores (aptos médicos). Eso convierte al proveedor en **subencargado del tratamiento**: entrada nueva en `RGPD-TRATAMIENTO-DATOS.md`, anexo nuevo en el DPA de cada tenant, y notificación a los clientes. Es precisamente lo que la vía autoalojada evita, y por eso pesa más que cualquier ventaja técnica.

### Recomendación

**Opción B**, detrás de `IVerificadorFirmaPdfService`. Si algún día aparece la necesidad del informe ETSI formal, se añade una implementación alternativa en Infrastructure sin tocar dominio, cola ni UI.

---

## 4. Carga: cuánto pesa de verdad

### Medido en este contenedor (4 vCPU, `openssl speed`)

| Operación | Medida | Para un PDF de 2 MB |
|---|---|---|
| SHA-256 sobre el rango firmado | ~394 MB/s | **~5 ms** |
| Verificación RSA-2048 | 19 µs (≈53.000/s) | **~0,02 ms** |
| Parseo del PDF para localizar la firma | — | unidades de ms |

**Total de CPU por documento: 10–30 ms.** Un PDF de 10 MB, ~25 ms de hash.

### La comparación que importa

La plataforma **ya ejecuta, en esta misma cola**, algo mucho más caro por documento con `VerificacionIaActiva`: rasterizar páginas a PNG (`PdfToPngRasterizadorPaginasPdfService`) y llamar a un LLM. Eso son **segundos** de latencia y **coste monetario por token**.

La verificación de firma es **dos o tres órdenes de magnitud más barata que lo que la plataforma ya corre**. En términos de CPU es ruido.

A escala: 10 tenants × 200 documentos/mes = 2.000 verificaciones ≈ **60 segundos de CPU al mes**.

### Lo que sí pesa: I/O, no CPU

**1. Revocación (OCSP/CRL) — el riesgo real.**
Es lo único de todo esto que puede doler. Una consulta OCSP a la FNMT son 100–500 ms; una **CRL de la FNMT puede pesar decenas de MB** y se descarga entera. Sin control, un emisor lento o caído bloquea la cola de análisis completa — incluidos los trabajos de IA que van detrás, porque `ProcesarPendientesDelTenantAsync` procesa **un trabajo a la vez en bucle secuencial**.

Mitigaciones, todas obligatorias:
- Preferir **OCSP sobre CRL** siempre que el certificado publique ambos.
- **Caché de respuestas de revocación** respetando el `nextUpdate` del emisor. Son datos públicos del emisor, iguales para todos los tenants → **catálogo global sin `TenantId`**, que hay que justificar y documentar en `docs/MULTITENANCY.md` § 7 antes de crear la tabla.
- **Timeout duro (5 s)** y degradación explícita: si la revocación no se puede comprobar, el resultado es *"firma válida, revocación no comprobada"* — nunca "inválida", y nunca una excepción que consuma un intento de los 3.
- Si el PDF trae **LTV** (diccionario DSS con OCSP/CRL embebidos, típico en documentos de la Administración), usar los embebidos y **no salir a la red**.

**2. Descargar el PDF de S3.** Ya se paga hoy en la cola de IA. Si el mismo documento dispara los dos análisis, hoy cada servicio abre el archivo por su cuenta — conviene mirarlo cuando se implemente, para no descargar dos veces.

**3. Memoria.** Hay que hashear **sobre el stream**, por trozos, sin cargar el PDF en un `byte[]`. Un `MemoryStream` de 10 MB por trabajo va al Large Object Heap y no hace falta: el `/ByteRange` define dos rangos contiguos que se pueden leer secuencialmente.

**4. Refresco del LOTL/TSL.** Un XML de pocos MB, una vez por semana, compartido por todos los tenants. Despreciable.

### Consecuencia de diseño

Como la verificación de firma es barata y puede ahorrar trabajo caro, conviene que corra **antes** que la verificación IA sobre el mismo documento: un documento con firma válida ya trae emisor y fecha certificados, y en algunos tipos eso puede hacer innecesaria parte de la extracción por LLM.

---

## 5. Plan por fases

### Calibración con muestras reales (2026-08-07, PR-6 parcial)

Cinco documentos reales de gestoría (corriente TGSS, RLC, RNT y dos ITA), analizados **fuera del repo** (confidenciales — aquí solo quedan las conclusiones estructurales):

1. **La reimpresión es la norma en el flujo de gestoría**: ninguno llegó firmado — "imprimir a PDF" destruye la firma y, en la mayoría, también la capa de texto (páginas rasterizadas). El verificador ahora lo detecta (`AparentaReimpresion`: Producer de impresión o cero fuentes embebidas) y el motivo es accionable: *pide el original descargado de la Sede*. La auto-validación por firma exige originales — cambio de proceso con las gestorías, no de código.
2. **El extractor de texto pierde las tildes** (caracteres de sustitución): todas las anclas son ahora agnósticas al acento («.» donde iría la vocal acentuada).
3. **RNT/RLC son tabulares** (etiquetas agrupadas, valores en otra zona): el periodo se extrae por **forma del valor** (MM/yyyy con lookarounds), no por adyacencia etiqueta→valor. Extraído correctamente de las muestras reales.
4. **RNT/RLC/ITA no traen CIF en el texto** — identifican a la empresa por CCC. CIF pasa a opcional en esos parsers y, sin CIF legible, el cotejo de identidad manda a revisión (nunca auto-valida a ciegas). **Pendiente**: cotejo por CCC exige añadir CCC a `Empresa` (decisión de dominio aparte).
5. **La extracción corre también sin firma válida**: el CEA/huella/periodo extraídos se persisten igualmente — son el insumo de la verificación oficial (§ 7 / épica 3). Decisión del usuario: una reimpresión cuyo código confirme el API del organismo podrá alcanzar `VerificadoOficialmente`; hasta entonces queda en revisión.
6. **Falta calibrar el camino positivo con un original firmado** (descarga directa de la Sede, sin imprimir-a-PDF): CEA y cadena real de los sellos FNMT del corriente TGSS/AEAT.

### Segunda ronda de calibración (2026-08-07, confirmada directamente por el usuario — sin necesidad de más muestras)

- **El CIF sí está en RNT/RLC/ITA**: la TGSS lo llama "Código de Empresario" y le antepone un prefijo numérico pegado ("90" en RNT/RLC/ITA, "0" en el certificado de corriente). `RegexCifComun` ahora admite el prefijo (0-4 dígitos) y lo descarta del valor capturado — el CIF vuelve a ser obligatorio en los 5 parsers.
- **Literal exacto del resultado del certificado**: "El presente certificado tiene carácter POSITIVO" / "…NEGATIVO" (NEGATIVO = existe deuda, se rechaza siempre). Sustituye a los literales adivinados de la primera versión.
- **Fecha del certificado**: tras "Información obtenida a…" (numérica o literal).
- **Fecha de RNT/RLC**: el documento no imprime una fecha de emisión propia — es **el día 1 del mes del "Periodo de liquidación"** (periodo 06/2026 → fecha emisión 01/06/2026; la vigencia de 2 meses más allá del periodo ya la calcula `CalculadoraEstadoDocumento` a partir de `TipoDocumento.VigenciaMeses`, sin tocar el parser). Derivación nueva y genérica en `ParserDocumentoOficialBase` (`FechaEmisionEsPrimerDiaDelPeriodo`), no un caso especial de RLC/RNT.
- **Fecha del ITA**: tras el literal "Informe de Trabajadores en Alta a fecha", formato día/mes/año separado por espacios (`NormalizarFecha` ampliado para aceptarlo, además de "/" y "-").

Con esto, los 4 tipos en alcance quedan calibrados en sus anclas de CIF y fecha; sigue pendiente solo la confirmación del CEA/cadena de confianza con un original firmado de la Sede.

**Fase 1 — Spike (media sesión). Nada se decide hasta esto.**
Comprobar si PDFsharp 6.2.4 expone `/ByteRange` y `/Contents` en lectura, sobre PDFs reales firmados: un certificado de estar al corriente de la TGSS, un certificado de formación de un SPA. Salida: sí/no y si hace falta lector propio o BouncyCastle.

**Fase 2 — Integridad y autoría, sin red.**
`FirmaDigitalDocumento`, `IVerificadorFirmaPdfService` + implementación .NET, `TipoAnalisisDocumento.VerificacionFirmaDigital` en la cola, migración, tests de aislamiento por tenant (patrón de los 40 ya existentes). Resultado: *"documento no modificado desde la firma; firmado por X (NIF Y) el Z"*. Sin cadena de confianza, sin revocación, sin salir a internet.
**Esto ya es la mayor parte del valor de negocio**: detecta el PDF manipulado, que es el fraude que importa en CAE.

**Fase 3 — Confianza.**
`X509Chain` con almacén propio, revocación con caché global + timeout + degradación, distinción cualificada / no cualificada.

**Fase 4 — Listas de confianza.**
Job semanal de refresco del LOTL y la TSL española, catálogo global de CA de confianza.

**Fase 5 — UI y ahorro.**
Sello en el detalle del Documento, filtro en el listado, y **quitar `TieneFirma` del prompt de la IA** una vez la firma criptográfica esté disponible: ahorra tokens en cada análisis y elimina una respuesta que el LLM no puede saber.

**Transversal**: flag `VerificacionFirmaActiva` por `TipoDocumento`, igual que el `VerificacionIaActiva` ya existente. No todos los tipos tienen sentido.

---

## 6. Decisiones pendientes del usuario

1. **¿Cuánta autonomía se le da al nivel de confianza?** Dos extremos de la misma decisión de negocio:
   - Por abajo: ¿un documento `Manipulado` puede aprobarse igualmente? Propuesta: sí, con aviso visible — coherente con "el análisis automático nunca decide solo".
   - Por arriba: ¿un documento `FirmaValida` de un tipo mensual recurrente (RNT, RLC, corriente) puede **validarse solo, sin revisión humana**? Ahí está el corte real de trabajo mensual que persigue el producto. Propuesta: opt-in por `TipoDocumento` (flag junto a `VerificacionIaActiva`), nunca global — y es una excepción consciente a la invariante de § 1, así que la decide el usuario, no este plan.

2. **RGPD — requiere confirmación antes de implementar.** El certificado de firma contiene datos personales del firmante (nombre, NIF). Guardarlos en `FirmaDigitalDocumento` es un **tratamiento nuevo**: hay que reflejarlo en `RGPD-TRATAMIENTO-DATOS.md` y encajarlo en el ciclo de retención. `CLAUDE.md` prohíbe expresamente implementar cumplimiento normativo sin confirmarlo antes.

3. **Flujo de red nuevo (no subencargado).** OCSP/CRL sale a internet hacia el emisor del certificado. **No se envía el documento** — solo el número de serie del certificado. No genera subencargado, a diferencia de la opción D, pero conviene dejarlo declarado.

---

## 7. Fuera de este plan: verificación CSV/CEA contra el organismo emisor

La firma prueba integridad; el CSV/CEA prueba **emisión** — que el organismo tiene ese documento en sus servidores. Es el escalón que sube de `FirmaValida` a `VerificadoOficialmente`, y no se compromete aquí porque el bloqueo no es técnico:

- **Vía seria**: servicios web de la TGSS para autorizados RED (SVID para CEA, trámite de huella para RNT/RLC), autenticados con sello electrónico de empresa (mTLS). Requiere un trámite administrativo previo — solicitar la autorización — que es decisión y gestión del usuario, no código. Cuando exista la autorización, encaja como conector de `ARQUITECTURA-INTEGRACIONES.md` (capacidad `VerificacionDocumental`), con su circuito de credenciales por tenant.
- **Vía scraping de la sede electrónica** (Playwright contra el SVID): descartada como mecanismo de producto — frágil ante cualquier cambio de la web, expuesta a CAPTCHA, y de encaje legal dudoso como proceso automatizado masivo contra una sede pública. Además el cotejo "byte a byte" contra el PDF re-descargado no es fiable: el organismo puede regenerar el PDF con metadatos distintos y el binario no coincide aunque el contenido sí.

Mientras tanto, el plan de § 5 ya extrae el CEA/CSV como texto (está en la capa digital del PDF) y lo **guarda** — de modo que cuando llegue la autorización RED, lo verificable esté ya almacenado y la verificación oficial sea un job que recorre lo pendiente, no una migración.
