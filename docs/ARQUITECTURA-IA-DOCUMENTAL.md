# Arquitectura — Motor de IA Documental multi-proveedor (Gemini + Mistral OCR)

**Estado**: Propuesta de arquitectura para revisión — **no implementado**. Responde a la petición del usuario (2026-07-24) de enrutar cada documento al proveedor de IA más barato sin sacrificar precisión, con Gemini 2.5 Flash y Mistral OCR como proveedores iniciales. Sigue la disciplina de `CLAUDE.md`: Dominio → Arquitectura → Plataforma → Implementación — este documento cubre las tres primeras; la implementación se hace después de confirmar esto.

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
    decimal CostoEstimadoPorPagina { get; }       // para que el router compare sin llamar a nadie

    Task<Result<string>> ExtraerTextoAsync(byte[] contenidoPagina, CancellationToken ct);           // OCR
    Task<Result<ExtraccionEstructuradaDto>> ExtraerEstructuradoAsync(string texto, string tipoEsperado, CancellationToken ct); // estructuración
}

public record ExtraccionEstructuradaDto(
    string? TipoDetectado, IReadOnlyDictionary<string, string?> Campos,
    int ConfianzaGeneral, string? NotasValidacion);
```

`IExtraccionMetadatosDocumentoIaService` (Fase 38) **no desaparece** — pasa a ser el contrato específico del caso "metadatos de Documento de Trabajador" (tipo/fechas/firma), y su implementación Anthropic puede seguir existiendo como un `IDocumentAIProvider` más (`Capacidades = ExtraccionEstructurada`) o quedarse como está si el usuario decide no meter Anthropic en este router — es una decisión de producto (¿tres proveedores conviven, o Gemini/Mistral sustituyen a Anthropic para este flujo?), señalada en § 6.

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
| Clasificación digital/escaneado/mixto | ⬜ Nuevo — requiere una librería de extracción de texto por página (PDFsharp no la tiene). Propuesta: **PdfPig** (MIT, pura .NET, sin dependencias nativas — a diferencia de motores OCR nativos, aquí solo hace falta leer si una página ya tiene texto embebido, no reconocerlo). Vive en `Infrastructure/DocumentosIa/`, detrás de una interfaz `IClasificadorDocumentoService` para no acoplar Application a PdfPig directamente. |
| Localización de páginas relevantes (pólizas de 200+ páginas) | ⬜ Nuevo, pero **explícitamente fuera de esta primera entrega** — pertenece al bloque "Expedientes/documentos grandes" del Issue #19, no a la selección de proveedor. Lo trato como Fase 2 de esta propuesta (§ 7). |
| Auditoría (proveedor/tiempo/coste/páginas/confianza/incidencias) | ⬜ Nuevo — extiende el patrón "documento enriquecido" del Issue #19: una tabla `AuditoriaExtraccionIa` (o columnas en `RevisionIaDocumento`/nueva tabla ligada 1:1 al Documento) con esos 6 campos, poblada por el orquestador tras cada llamada, con `TenantId` desde el día uno. |
| `IDocumentAIProvider` + Factory + capacidades | ⬜ Nuevo, pero es la generalización que `docs/PLATFORM.md` § 4 ya preveía — no una capa añadida "por si acaso". |

## 4. Puntos que necesito que confirmes antes de escribir código

1. **¿Conviven Gemini/Mistral con Anthropic, o Anthropic queda solo para el chat/detección de trabajadores?** Afecta si `AnthropicExtraccionMetadatosDocumentoIaService` (Fase 38) se registra también como `IDocumentAIProvider` o se deja aparte.
2. **Credenciales**: mismo patrón "inerte por defecto" que `AnthropicOptions` — necesito que confirmes que Gemini/Mistral se provisionan igual (API key en configuración, sin llamada real hasta que exista) antes de dar por buena la Fase de implementación, igual que se hizo con Anthropic.
3. **Coste estimado por página**: pediste "priorizando siempre el menor coste posible" — necesito una fuente de verdad para `CostoEstimadoPorPagina` de cada proveedor (¿tabla de configuración editable, o constante en código con nota de revisión periódica, mismo criterio que `DocumentValidityRules` del Issue #19?).
4. **Alcance de esta primera entrega**: propongo dejar **fuera** de esta fase (igual que Fase 38 dejó fuera Cliente/Empresa/Vehículo) la localización de páginas relevantes de documentos grandes y el Document Graph/Expedientes — son results independientes del router de proveedor y añadirían mucho alcance a la vez. ¿De acuerdo en trocearlo así, o quieres todo junto?

## 5. Siguiente paso propuesto

Con tu confirmación de § 4, la implementación se trocea en:
1. `IClasificadorDocumentoService` + PdfPig (Infrastructure) — clasificación local, sin llamar a ningún proveedor todavía.
2. `IDocumentAIProvider` + Factory + `GeminiDocumentAIProvider`/`MistralOcrDocumentAIProvider` (Infrastructure), inertes sin API key.
3. `DocumentAIRouterService` (Application) implementando los 4 casos + reintento inteligente, sustituyendo la llamada directa a `IExtraccionMetadatosDocumentoIaService` dentro de `VerificacionIaDocumentoService` por una llamada al router.
4. Cache por SHA256 + tabla de auditoría.

Cada uno con su propia verificación (build/tests/E2E) antes de pasar al siguiente, mismo criterio de fases pequeñas que el resto de `ROADMAP.md`.
