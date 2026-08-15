using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.DocumentosIa;

namespace CaeManager.Infrastructure.AsistenteIa;

/// <summary>
/// <see cref="IDocumentAIProvider"/> determinista, sin llamada de red — solo
/// para la suite E2E (Horizonte 1.6, "ciclo documental entero" de
/// MACRO_PLAN_2026-08-13.md § 1.6). Un test que sube un documento y espera
/// que <c>ProcesadorAnalisisDocumentoHostedService</c> lo procese no puede
/// depender de un modelo real: no determinista, de pago y, sin credenciales
/// en CI (las claves de Anthropic/Gemini/Mistral solo existen en el .env de
/// la VPS de producción — ver InfrastructureServiceCollectionExtensions),
/// siempre "inerte" en este entorno de todos modos (mismo patrón que
/// <see cref="AnthropicDocumentAIProvider"/>: sin ApiKey, cada proveedor
/// real falla controlado). Mismo criterio que <c>DatosPrueba:Activo</c>:
/// una pieza de test que vive en Infrastructure porque el registro
/// condicional necesita estar en el mismo sitio que el resto de la
/// composición de <see cref="IDocumentAIProvider"/>, apagada por defecto y
/// solo activada por WebAppFixture vía variable de entorno.
///
/// Confianza fija en 50% — por debajo del umbral de confianza baja de
/// <c>VerificacionIaDocumentoService</c> (70%) — a propósito: así el
/// documento de prueba genera una <c>RevisionIaDocumento</c> pendiente de
/// verdad en vez de aprobarse solo, que es lo que el test de ciclo
/// documental necesita confirmar/corregir en /documentos/revision-ia.
/// </summary>
public class ProveedorFalsoDocumentAI : IDocumentAIProvider
{
    public string Codigo => "falso-e2e";

    public CapacidadesProveedorIa Capacidades =>
        CapacidadesProveedorIa.OcrImagenAEscaneado | CapacidadesProveedorIa.ExtraccionEstructurada;

    public Task<Result<TextoExtraccionDto>> ExtraerTextoAsync(
        byte[] contenidoArchivo, string nombreArchivo, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Exito(new TextoExtraccionDto(
            "Texto determinista de prueba generado por ProveedorFalsoDocumentAI (E2E).", CosteEstimado: 0m)));

    public Task<Result<ExtraccionEstructuradaDto>> ExtraerEstructuradoAsync(
        string texto, string tipoEsperado, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Exito(new ExtraccionEstructuradaDto(
            TipoDetectado: tipoEsperado,
            Campos: new Dictionary<string, string?>(),
            ConfianzaGeneral: 50,
            NotasValidacion: "Extracción determinista de prueba (E2E) — confianza baja a propósito.",
            CosteEstimado: 0m)));
}
