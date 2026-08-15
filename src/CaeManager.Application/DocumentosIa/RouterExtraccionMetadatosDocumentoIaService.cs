using CaeManager.Application.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.DocumentosIa;

/// <summary>
/// Adapta <see cref="IDocumentAIRouterService"/> (genérico, cualquier tipo
/// de documento) al contrato específico que ya usa
/// <c>VerificacionIaDocumentoService</c> (Fase 38, Documento de Trabajador)
/// — sustituye a <c>AnthropicExtraccionMetadatosDocumentoIaService</c> sin
/// tocar ese servicio ni sus tests: solo cambia qué implementación de
/// <see cref="IExtraccionMetadatosDocumentoIaService"/> se registra en DI.
/// El prompt de <c>AnthropicDocumentAIProvider</c> ya pide "fechaEmision"/
/// "fechaVencimiento"/"tieneFirma" como campos comunes — aquí solo se
/// parsean de forma tolerante (null si faltan o no se pueden interpretar,
/// nunca una excepción).
/// </summary>
public class RouterExtraccionMetadatosDocumentoIaService(IDocumentAIRouterService router) : IExtraccionMetadatosDocumentoIaService
{
    private const string NombreArchivoPorDefecto = "documento.pdf";

    public async Task<Result<MetadatosDocumentoExtraidosDto>> ExtraerAsync(
        byte[] contenidoPdf, string nombreTipoDocumento, Guid? documentoId = null, CancellationToken cancellationToken = default)
    {
        var resultado = await router.ProcesarAsync(contenidoPdf, NombreArchivoPorDefecto, nombreTipoDocumento, documentoId, cancellationToken);
        if (resultado.EsFallido)
            return Result.Fallo<MetadatosDocumentoExtraidosDto>(resultado.Error);

        var campos = resultado.Valor.Campos;

        return Result.Exito(new MetadatosDocumentoExtraidosDto(
            resultado.Valor.TipoDetectado,
            ParsearFecha(campos.GetValueOrDefault("fechaEmision")),
            ParsearFecha(campos.GetValueOrDefault("fechaVencimiento")),
            ParsearBooleano(campos.GetValueOrDefault("tieneFirma")),
            resultado.Valor.ConfianzaGeneral,
            resultado.Valor.NotasValidacion));
    }

    private static DateOnly? ParsearFecha(string? valor) =>
        !string.IsNullOrWhiteSpace(valor) && DateOnly.TryParse(valor, out var fecha) ? fecha : null;

    private static bool? ParsearBooleano(string? valor) =>
        !string.IsNullOrWhiteSpace(valor) && bool.TryParse(valor, out var booleano) ? booleano : null;
}
