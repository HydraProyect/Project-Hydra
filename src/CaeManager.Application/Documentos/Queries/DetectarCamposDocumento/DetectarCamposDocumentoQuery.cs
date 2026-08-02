using CaeManager.Application.Common;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.DocumentosIa;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CaeManager.Application.Documentos.Queries.DetectarCamposDocumento;

/// <summary>
/// Se dispara al subir el archivo en el formulario de alta de Documento,
/// antes de que el usuario haya elegido Trabajador ni TipoDocumento — a
/// diferencia de <c>VerificacionIaDocumentoService</c> (que verifica un
/// Documento ya creado contra un TipoDocumento ya conocido), aquí el tipo
/// esperado todavía no existe, así que se le pasa al router una descripción
/// genérica y se interpreta "tipoDetectado" libremente. Solo sugiere — el
/// usuario conserva ambos campos editables y puede corregir la sugerencia
/// antes de guardar (ver Issue #19, ninguna lectura IA corrige nada sola).
/// </summary>
public record DetectarCamposDocumentoQuery(byte[] Contenido, string NombreArchivo, AmbitoAplicacion Ambito)
    : IRequest<Result<DeteccionCamposDocumentoDto>>;

public record DeteccionCamposDocumentoDto(Guid? TipoDocumentoId, Guid? TrabajadorId, int ConfianzaGeneral);

public class DetectarCamposDocumentoQueryHandler(
    IDocumentAIRouterService router,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IOptions<DeteccionPreviaDocumentoOptions> opciones)
    : IRequestHandler<DetectarCamposDocumentoQuery, Result<DeteccionCamposDocumentoDto>>
{
    private const string TipoEsperadoDesconocido = "documento CAE (tipo todavía no seleccionado por el usuario, infiérelo libremente del contenido)";

    /// <summary>Por debajo de esto no vale la pena sugerir nada — mismo umbral de "confianza baja" que Issue #19/VerificacionIaDocumentoService.</summary>
    private const int UmbralConfianzaSugerencia = 70;

    public async Task<Result<DeteccionCamposDocumentoDto>> Handle(
        DetectarCamposDocumentoQuery request, CancellationToken cancellationToken)
    {
        // Apagado por defecto (ver DeteccionPreviaDocumentoOptions): sin esto,
        // el PDF completo de cualquier Documento de Trabajador —incluido un
        // reconocimiento médico— viajaba a un proveedor de IA externo antes
        // de que existiera un TipoDocumento que lo protegiera con
        // VerificacionIaActiva/LecturaIaActiva. Devolver "sin sugerencia" deja
        // el alta manual intacta, solo sin el autorrelleno.
        if (!opciones.Value.Activa)
            return Result.Exito(new DeteccionCamposDocumentoDto(null, null, 0));

        var resultado = await router.ProcesarAsync(request.Contenido, request.NombreArchivo, TipoEsperadoDesconocido, cancellationToken);
        if (resultado.EsFallido)
            return Result.Fallo<DeteccionCamposDocumentoDto>(resultado.Error);

        var extraccion = resultado.Valor;
        if (extraccion.ConfianzaGeneral < UmbralConfianzaSugerencia)
            return Result.Exito(new DeteccionCamposDocumentoDto(null, null, extraccion.ConfianzaGeneral));

        var tipoDocumentoId = await DetectarTipoDocumentoAsync(extraccion.TipoDetectado, request.Ambito, cancellationToken);
        var trabajadorId = request.Ambito == AmbitoAplicacion.Trabajador
            ? await DetectarTrabajadorAsync(extraccion.Campos, cancellationToken)
            : null;

        return Result.Exito(new DeteccionCamposDocumentoDto(tipoDocumentoId, trabajadorId, extraccion.ConfianzaGeneral));
    }

    /// <summary>Solo sugiere si hay una única coincidencia razonable — ante cualquier ambigüedad, mejor dejarlo en blanco que arriesgar una mala sugerencia.</summary>
    private async Task<Guid?> DetectarTipoDocumentoAsync(string? tipoDetectado, AmbitoAplicacion ambito, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tipoDetectado))
            return null;

        var tipos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == ambito)
            .Select(t => new { t.Id, t.Nombre })
            .ToListAsync(cancellationToken);

        var coincidencias = tipos
            .Where(t => t.Nombre.Contains(tipoDetectado, StringComparison.OrdinalIgnoreCase)
                || tipoDetectado.Contains(t.Nombre, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return coincidencias.Count == 1 ? coincidencias[0].Id : null;
    }

    private async Task<Guid?> DetectarTrabajadorAsync(IReadOnlyDictionary<string, string?> campos, CancellationToken cancellationToken)
    {
        var dniDetectado = campos.GetValueOrDefault("documentoIdentidadTrabajador");
        if (string.IsNullOrWhiteSpace(dniDetectado))
            return null;

        var dniNormalizado = NormalizarDni(dniDetectado);
        var trabajadores = await trabajadoresContext.Trabajadores
            .Select(t => new { t.Id, t.Dni })
            .ToListAsync(cancellationToken);

        return trabajadores.FirstOrDefault(t => NormalizarDni(t.Dni) == dniNormalizado)?.Id;
    }

    private static string NormalizarDni(string dni) =>
        new(dni.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
