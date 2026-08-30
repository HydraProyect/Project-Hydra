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

public record DeteccionCamposDocumentoDto(
    Guid? TipoDocumentoId, Guid? TrabajadorId, int ConfianzaGeneral, string? AliasSugerido = null);

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

        var resultado = await router.ProcesarAsync(request.Contenido, request.NombreArchivo, TipoEsperadoDesconocido, cancellationToken: cancellationToken);
        if (resultado.EsFallido)
            return Result.Fallo<DeteccionCamposDocumentoDto>(resultado.Error);

        var extraccion = resultado.Valor;
        if (extraccion.ConfianzaGeneral < UmbralConfianzaSugerencia)
            return Result.Exito(new DeteccionCamposDocumentoDto(null, null, extraccion.ConfianzaGeneral));

        var tipoDocumentoId = await DetectarTipoDocumentoAsync(extraccion.TipoDetectado, request.Ambito, cancellationToken);
        (Guid? trabajadorId, string? aliasSugerido) = request.Ambito == AmbitoAplicacion.Trabajador
            ? await DetectarTrabajadorAsync(extraccion.Campos, cancellationToken)
            : (null, null);

        return Result.Exito(new DeteccionCamposDocumentoDto(tipoDocumentoId, trabajadorId, extraccion.ConfianzaGeneral, aliasSugerido));
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

    /// <summary>
    /// La identidad siempre se confirma por DNI, nunca por el nombre — pero
    /// si el campo "nombreTrabajador" que ya extraen los proveedores de IA
    /// (ver AnthropicDocumentAIProvider et al.) no coincide ni con el
    /// Nombre+Apellidos oficiales ni con el Alias ya guardado, se devuelve
    /// como sugerencia de alias — un clic del Gestor en vez de tener que ir
    /// a Trabajadores a añadirlo a mano (ver Trabajador.AsignarAlias).
    /// </summary>
    private async Task<(Guid? TrabajadorId, string? AliasSugerido)> DetectarTrabajadorAsync(
        IReadOnlyDictionary<string, string?> campos, CancellationToken cancellationToken)
    {
        var dniDetectado = campos.GetValueOrDefault("documentoIdentidadTrabajador");
        if (string.IsNullOrWhiteSpace(dniDetectado))
            return (null, null);

        var dniNormalizado = NormalizarDni(dniDetectado);
        var trabajadores = await trabajadoresContext.Trabajadores
            .Select(t => new { t.Id, t.Dni, t.Nombre, t.Apellidos, t.Alias })
            .ToListAsync(cancellationToken);

        var trabajador = trabajadores.FirstOrDefault(t => NormalizarDni(t.Dni) == dniNormalizado);
        if (trabajador is null)
            return (null, null);

        var nombreDetectado = campos.GetValueOrDefault("nombreTrabajador")?.Trim();
        if (string.IsNullOrWhiteSpace(nombreDetectado))
            return (trabajador.Id, null);

        var nombreOficial = $"{trabajador.Nombre} {trabajador.Apellidos}";
        var coincideConOficial = string.Equals(nombreDetectado, nombreOficial, StringComparison.OrdinalIgnoreCase);
        var coincideConAlias = trabajador.Alias is not null
            && string.Equals(nombreDetectado, trabajador.Alias, StringComparison.OrdinalIgnoreCase);

        var aliasSugerido = coincideConOficial || coincideConAlias ? null : nombreDetectado;

        return (trabajador.Id, aliasSugerido);
    }

    // string? — un trabajador anonimizado (Dni null) nunca debe casar con un
    // Dni detectado por IA: normaliza a "", que ninguna detección real produce.
    private static string NormalizarDni(string? dni) =>
        dni is null ? string.Empty : new string(dni.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
