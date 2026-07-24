# Arquitectura — Motor de IA Documental multi-proveedor (Gemini + Mistral OCR)

**Estado**: Propuesta de arquitectura, decisiones cerradas con el usuario el 2026-07-24. **Fase 1 (clasificación local) completa** — ver § 3 y `ROADMAP.md`; siguiente paso es la Fase 2 de § 5 (`IDocumentAIProvider` sobre Anthropic para pruebas puntuales). Responde a la petición del usuario de enrutar cada documento al proveedor de IA más barato sin sacrificar precisión, con Gemini 2.5 Flash y Mistral OCR como proveedores futuros. Sigue la disciplina de `CLAUDE.md`: Dominio → Arquitectura → Plataforma → Implementación — este documento cubre las tres primeras.

**Decisiones del usuario (2026-07-24)**: (1) el chat "Pregúntale a Hydra" sigue sobre Anthropic sin tocarse — es intercambiable en el futuro, pero no como parte de este trabajo; (2) modelo de credenciales: clave global por ahora (recomendación del asistente, no objetada — ver § 4.1); (3) el coste por página no es un criterio de enrutado en v1, solo un dato de auditoría (ver § 4.2, simplificación aceptada); (4) la localización de páginas relevantes en documentos grandes queda fuera de esta entrega, como fase separada.

## 0. Punto de partida real (no aspiracional)

Antes de proponer nada, esto es lo que ya existe en el repositorio, verificado en código:

- **Un único proveedor de IA, sin abstracción**, llamado por HTTP directo a la API de Anthropic desde tres sitios: `AnthropicAsistenteIaService` (chat), `AnthropicExtraccionTrabajadoresIaService` (listado de personal en documentos de Empresa), y `AnthropicExtraccionMetadatosDocumentoIaService` (Fase 38, recién construido: tipo/fechas/firma/confianza de un Documento de Trabajador). Los tres comparten el mismo patrón: `AnthropicOptions` (ApiKey opcional, "inerte por defecto"), prompt que exige JSON estricto, parseo con red de seguridad por si el modelo envuelve la respuesta en markdown.
- **`docs/PLATFORM.md` § 4 ya dejó escrita la condición de disparo** para generalizar esto: *"el día que aparezca un segundo caso de uso de IA con un proveedor distinto... se generaliza a un `IAIProvider`"*. Esta petición **es** ese disparador — no es una abstracción especulativa, es la que el propio documento de plataforma anticipó.
- **`ARQUITECTURA-INTEGRACIONES.md` ya resolvió el mismo problema de forma general** (proveedores de integración CAE: Dokify/6Coordina/CTAIMA) con un patrón concreto: capacidades como flags, Factory por `(código, versión)`, orquestador que decide contra capacidades — nunca contra nombres de proveedor. La propuesta de abajo es **el mismo patrón aplicado a IA documental**, no uno nuevo — mismo criterio de "Consistencia de patrones" de `PROJECT.md`.
- **No existe hoy ningún componente de clasificación de documento** (digital vs. escaneado vs. mixto), ni de localización de páginas relevantes, ni de cache por hash, ni de tabla de costes/auditoría de IA. `PDFsharp` (ya en el proyecto) sirve para generar/combinar PDFs, no para extraer texto — no cubre la clasificación página a página que pide el Caso 4.

## 1. Dominio: qué representa esto para el negocio

Un documento no tiene un proveedor de IA "correcto" fijo — tiene una **clasificación** (tipo de contenido, calidad, tamaño) que determina qué proveedor(es) tiene sentido usar, y esa decisión es reproducible y auditable. Tres conceptos de dominio nuevos:

| Concepto | Qué es |
|---|---|
| **Clasificación de documento** | Digital (texto seleccionable) / Escaneado (imagen) / Mixto (ambos) / Imagen suelta — se determina **localmente**, antes de llamar a cualquier proveedor, por eso no es una entidad de negocio sino un resultado de análisis (ver § 4.1). |
| **Extracción IA** | El resultado estructurado de procesar un documento: tipo detectado, campos extraídos, confianza, y los metadatos de auditoría (proveedor usado, tiempo, coste estimado, páginas procesadas, incidencias). Generaliza lo que `RevisionIaDocumento`/`MetadatosDocumentoExtraidosDto` ya modelan para el caso Trabajador (Fase 38) — ver § 5 sobre cómo esto no descarta ese trabajo, lo envuelve. |
| **Proveedor de IA Documental** | Igual que `ProveedorIntegracion`: un catálogo de qué proveedores existen y qué **capacidades** declara cada uno (no qué proveedor es). |

## 2. Arquitectura: el mismo patrón que `IIntegrationProvider`, aplicado a IA

### 2.1 Capacidades, no proveedores

```csharp
// CaeManager.Domain.DocumentosIa.CapacidadesProveedorIa
[Flags]
public enum CapacidadesProveedorIa
{
    Ninguna              = 0,
    OcrImagenAEscaneado  = 1 << 0,  // Mistral OCR: imagen/escaneado → texto plano
    ExtraccionEstructurada = 1 << 1,  // Gemini 2.5 Flash: texto → JSON estructurado con confianza
    ClasificacionBarata  = 1 << 2,  // Nivel 1: solo tipo de documento, sin extracción completa
    ComparacionDocumentos = 1 << 3,  // futuro: póliza N vs. póliza N+1 (Issue #19, sin construir)
}
```

El **router** (§ 3) consulta capacidades antes de llamar a nadie — nunca `if (proveedor == "gemini")`. Añadir un tercer proveedor mañana (Azure OpenAI, Bedrock) es una fila de catálogo + un adaptador nuevo en `Infrastructure`, cero cambios en `Application`/`Domain`/`Presentation` — exactamente la garantía que ya da `ARQUITECTURA-INTEGRACIONES.md` § 1 para conectores CAE.

### 2.2 Contratos (`Application/DocumentosIa/Common/`)

```csharp
public interface IDocumentAIProvider
{
    string Codigo { get; }                       // "gemini-2-5-flash", "mistral-ocr"
    CapacidadesProveedorIa Capacidades { get; }

    Task<Result<string>> ExtraerTextoAsync(byte[] contenidoPagina, CancellationToken ct);           // OCR
    Task<Result<ExtraccionEstructuradaDto>> ExtraerEstructuradoAsync(string texto, string tipoEsperado, CancellationToken ct); // estructuración
}

// Auditoría, no enrutado — ver § 4.2. Cada proveedor calcula su propio
// coste con sus propias unidades (Gemini por tokens, Mistral OCR por
// página); no hay un campo de interfaz porque no se compara entre
// proveedores todavía.
public record CosteEstimadoDto(decimal Importe, string Moneda, string Unidad);

public record ExtraccionEstructuradaDto(
    string? TipoDetectado, IReadOnlyDictionary<string, string?> Campos,
    int ConfianzaGeneral, string? NotasValidacion);
```

**Decidido con el usuario**: el chat "Pregúntale a Hydra" (`IAsistenteIaService`/`AnthropicAsistenteIaService`) **no se toca** — sigue sobre Anthropic, fuera de este router. `IExtraccionMetadatosDocumentoIaService` (Fase 38, construido esta misma sesión, todavía sin usuarios reales dependiendo de él) sí es candidato directo a que su implementación pase de Anthropic a `DocumentAIRouterService` (Gemini + Mistral) — es exactamente el flujo que motivó esta propuesta. `IExtraccionTrabajadoresIaService` (Fase 36, detección de altas/bajas de personal, en uso) se deja **fuera de esta fase a propósito**: migrarlo también es una extensión natural más adelante, no una decisión que haya que tomar ahora para poder construir el router.

### 2.3 El Router (`DocumentAIRouterService`, Application)

Implementa exactamente los 4 casos que pediste, como reglas explícitas — no heurísticas ocultas ni until until "el modelo decide":

```
Documento → Clasificar (§ 4.1, local, sin IA)
  Digital        → ExtraccionEstructurada directa (Gemini) — Caso 1
  Escaneado      → OCR (Mistral) → ExtraccionEstructurada (Gemini) — Caso 2
  Imagen suelta  → OCR (Mistral) → ExtraccionEstructurada (Gemini) — Caso 3
  Mixto          → por página: Digital→texto directo / Escaneado→OCR → unificar → ExtraccionEstructurada — Caso 4
```

El router es una **cadena de responsabilidad** simple (Domain no lo conoce; vive en Application, mismo nivel que `IIntegrationOrchestrator`), no una máquina de estados nueva — reutiliza el `Result<T>` ya establecido en todo el proyecto para propagar fallos de un proveedor sin tumbar el resto del pipeline.

### 2.4 Reintento inteligente

No es un mecanismo nuevo de resiliencia (`ARQUITECTURA-INTEGRACIONES.md` § 6 ya define políticas de reintento para integraciones) — es una **regla de negocio explícita** en el orquestador: si `ConfianzaGeneral < UmbralReintento` (mismo umbral 70% ya usado en Fase 38) y hay un segundo proveedor con `ExtraccionEstructurada`, reprocesar con él y quedarse con el resultado de mayor confianza. Nunca más de un reintento automático — si el segundo proveedor tampoco da confianza suficiente, va a la cola de revisión humana (`RevisionIaDocumento`, ya construida) igual que hoy.

## 3. Plataforma: qué es realmente nuevo vs. qué se reutiliza

| Pieza | Estado |
|---|---|
| Cola de revisión humana, confidence score, "nunca corrige solo" | ✅ Ya construido (Fase 38) — el router solo cambia **quién** genera el `MetadatosDocumentoExtraidosDto`, no qué se hace con él. |
| `TipoDocumento.VerificacionIaActiva`, toggle Admin | ✅ Reutilizable tal cual — el toggle activa "verificación IA", no un proveedor concreto. |
| Cache documental por SHA256 | ⬜ Nuevo, pero pequeño: hash del archivo ya se puede calcular en `IFileStorageService.GuardarAsync` o antes de llamar al router; una tabla `ExtraccionIaCache(HashSha256, ExtraccionJson, TenantId)` con índice único `(TenantId, HashSha256)` evita reprocesar. |
| Clasificación digital/escaneado/mixto | ✅ **Completa (Fase 1, 2026-07-24)** — `IClasificadorDocumentoService`/`PdfSharpClasificadorDocumentoService`. **Cambio respecto a la propuesta original**: no se usó `UglyToad.PdfPig` — el paquete disponible en el feed de NuGet de esta sesión resultó sospechoso (solo 2 versiones publicadas, la más reciente `1.7.0-custom-5` con descripción placeholder sin contenido real, mientras que paquetes de control como `Newtonsoft.Json` sí mostraban su historial real completo por el mismo feed). No se instaló. En su lugar se reutilizó `PdfSharp` (ya dependencia del proyecto, historial de versiones normal verificado) y su `ContentReader`: cada página se lee como secuencia de operadores de contenido, y "tiene texto digital" se resuelve comprobando si aparece algún operador de mostrar texto (`Tj`/`TJ`/`'`/`"`) — sin necesitar una librería de extracción de texto dedicada. **Nota para sesiones futuras: no reintentar `UglyToad.PdfPig` sin re-verificar el paquete primero** (comparar historial de versiones y metadatos contra una fuente de confianza). |
| Localización de páginas relevantes (pólizas de 200+ páginas) | ⬜ Nuevo, pero **explícitamente fuera de esta primera entrega** — pertenece al bloque "Expedientes/documentos grandes" del Issue #19, no a la selección de proveedor. Lo trato como Fase 2 de esta propuesta (§ 7). |
| Auditoría (proveedor/tiempo/coste/páginas/confianza/incidencias) | ⬜ Nuevo — extiende el patrón "documento enriquecido" del Issue #19: una tabla `AuditoriaExtraccionIa` (o columnas en `RevisionIaDocumento`/nueva tabla ligada 1:1 al Documento) con esos 6 campos, poblada por el orquestador tras cada llamada, con `TenantId` desde el día uno. **El coste es solo un campo registrado, no una entrada de decisión** (ver § 4.2) — cada proveedor lo calcula con su propia unidad de precio (constante configurable en su propio `*Options`, mismo patrón que `AnthropicOptions.MaxTokensRespuesta`), sin tabla ni comparación entre proveedores en v1. |
| `IDocumentAIProvider` + Factory + capacidades | ⬜ Nuevo, pero es la generalización que `docs/PLATFORM.md` § 4 ya preveía — no una capa añadida "por si acaso". |

## 4. Decisiones cerradas con el usuario (2026-07-24)

### 4.1 Credenciales: clave global por ahora, modelo de negocio ya perfilado para después

**Para desarrollo/pruebas puntuales ahora mismo**: se conecta a Anthropic (proveedor ya integrado, con `ApiKey` real disponible para probar) — no a Gemini/Mistral todavía, que no tienen clave provisionada. Esto significa que la primera implementación de `IDocumentAIProvider` es un adaptador Anthropic (reutilizando el patrón ya construido), y los adaptadores Gemini/Mistral se añaden cuando haya claves reales que probar — el router y las interfaces no cambian por esto, es exactamente el punto de la abstracción.

**Modelo de negocio ya perfilado por el usuario, para cuando se implemente Licensing** (`docs/PLATFORM.md` § 4): el paquete base de Hydra **incluye una clave de IA compartida** (paga Hydra, capacidad incluida en el plan — el modelo "global" de hoy), y existe un **add-on opcional** que el cliente contrata para que se le asigne una **clave personalizada** (por-tenant, `CredencialIntegracion`). Es decir, no es "global para siempre" ni "por-tenant desde ya" — es **ambos modelos coexistiendo**, seleccionados por si el tenant tiene el add-on activo o no. Mismo patrón de dos niveles que ya existe en el código (`TipoDocumento.LecturaIaActiva` global + `ConfiguracionIaDocumentoCliente` por-cliente, Fase 35) — cuando se implemente Licensing, la resolución de qué `ApiKey` usar por llamada probablemente sea: ¿el tenant tiene el add-on con clave propia? → usarla; si no → clave compartida del paquete base. No se construye Licensing todavía (sigue sin existir, `docs/PLATFORM.md` § 3), pero queda anotado aquí para que cuando se aborde no haya que rediseñar la resolución de credenciales de IA desde cero.

### 4.2 Coste: solo auditoría, no criterio de enrutado

Con dos proveedores y cada uno dueño de una capacidad distinta (Mistral = OCR, Gemini = estructuración), **el router nunca elige entre dos proveedores que hacen lo mismo** — los 4 casos ya determinan qué proveedor se usa, no hay comparación de precio que hacer. El coste estimado por página deja de ser un input de `IDocumentAIProvider` y pasa a ser un dato calculado solo para el registro de auditoría, con una constante configurable por proveedor (no una tabla nueva). Si en el futuro aparece un segundo proveedor de OCR o de estructuración, ahí sí hace falta comparar coste real — se aborda en ese momento, no antes (YAGNI).

### 4.3 Alcance de esta primera entrega

Confirmado: la localización de páginas relevantes en documentos grandes (pólizas de cientos de páginas) y el Document Graph/Expedientes quedan **fuera de esta fase**, como una fase separada posterior — no bloquean la construcción del router de proveedor.

## 5. Siguiente paso propuesto

Con tu confirmación de § 4, la implementación se trocea en:
1. `IClasificadorDocumentoService` + PdfPig (Infrastructure) — clasificación local, sin llamar a ningún proveedor todavía.
2. `IDocumentAIProvider` + Factory (Infrastructure) — primer adaptador real sobre Anthropic (clave ya disponible para pruebas puntuales, ver § 4.1); `GeminiDocumentAIProvider`/`MistralOcrDocumentAIProvider` se añaden cuando haya claves reales que probar, sin tocar el router.
3. `DocumentAIRouterService` (Application) implementando los 4 casos + reintento inteligente, sustituyendo la llamada directa a `IExtraccionMetadatosDocumentoIaService` dentro de `VerificacionIaDocumentoService` por una llamada al router.
4. Cache por SHA256 + tabla de auditoría.

Cada uno con su propia verificación (build/tests/E2E) antes de pasar al siguiente, mismo criterio de fases pequeñas que el resto de `ROADMAP.md`.
