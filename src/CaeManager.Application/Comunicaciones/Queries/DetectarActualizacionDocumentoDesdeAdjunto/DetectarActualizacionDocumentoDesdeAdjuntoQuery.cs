using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Application.DocumentosIa;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CaeManager.Application.Comunicaciones.Queries.DetectarActualizacionDocumentoDesdeAdjunto;

/// <summary>
/// Flujo "Actualizar documentación desde conversación" (docs/COMUNICACIONES.md
/// § 12.7): mismo espíritu que DetectarCamposDocumentoQuery (solo sugiere,
/// el gestor conserva todos los campos editables — Issue #19), pero sobre
/// un adjunto de mensaje en vez de una subida manual, y sin conocer el
/// Ámbito de antemano (aquí se infiere del TipoDocumento detectado, acotado
/// a Trabajador/Empresa — los dos casos reales del mockup; Cliente/Vehículo/
/// Proyecto quedan fuera de este flujo, YAGNI). El envío del adjunto a un
/// proveedor de IA externo está detrás de <see cref="ExtraccionDocumentoAdjuntoOptions"/>,
/// apagado por defecto.
/// </summary>
public record DetectarActualizacionDocumentoDesdeAdjuntoQuery(Guid AdjuntoId)
    : IRequest<Result<DeteccionActualizacionDocumentoDto>>;

public record DeteccionActualizacionDocumentoDto(
    Guid AdjuntoId,
    Guid? TipoDocumentoId, string? TipoDocumentoNombre, AmbitoAplicacion? Ambito,
    Guid? TrabajadorId, string? TrabajadorNombre,
    Guid? EmpresaId, string? EmpresaNombre,
    DateOnly? FechaEmision, DateOnly? FechaVencimiento,
    int ConfianzaGeneral);

public class DetectarActualizacionDocumentoDesdeAdjuntoQueryHandler(
    IComunicacionesQueryContext comunicacionesContext, IAlcanceDatosService alcanceDatos, IFileStorageService almacenamiento,
    IDocumentAIRouterService router, IOptions<ExtraccionDocumentoAdjuntoOptions> opciones,
    ITiposDocumentoQueryContext tiposDocumentoContext, ITrabajadoresQueryContext trabajadoresContext,
    IEmpresasQueryContext empresasContext,
    IInstruccionTratamientoIaService instruccionTratamientoIa, ITenantActual tenantActual)
    : IRequestHandler<DetectarActualizacionDocumentoDesdeAdjuntoQuery, Result<DeteccionActualizacionDocumentoDto>>
{
    private const string TipoEsperadoDesconocido =
        "documento CAE recibido por correo o WhatsApp (tipo todavía no seleccionado por el gestor, infiérelo libremente del contenido)";

    /// <summary>Mismo umbral que DetectarCamposDocumentoQuery.</summary>
    private const int UmbralConfianzaSugerencia = 70;

    public async Task<Result<DeteccionActualizacionDocumentoDto>> Handle(
        DetectarActualizacionDocumentoDesdeAdjuntoQuery request, CancellationToken cancellationToken)
    {
        var adjunto = await (
            from a in comunicacionesContext.AdjuntosMensaje
            join m in comunicacionesContext.Mensajes on a.MensajeId equals m.Id
            join c in comunicacionesContext.Conversaciones on m.ConversacionId equals c.Id
            where a.Id == request.AdjuntoId
            select new { a.Id, a.NombreArchivo, a.ArchivoUrl, c.ClienteId, c.EmpresaId, c.ConexionIntegracionId })
            .FirstOrDefaultAsync(cancellationToken);

        if (adjunto is null || !await alcanceDatos.ConversacionVisibleAsync(adjunto.ClienteId, adjunto.EmpresaId, adjunto.ConexionIntegracionId, cancellationToken))
            return Result.Fallo<DeteccionActualizacionDocumentoDto>(Error.Crear("Adjunto.NoEncontrado", "No encontramos este adjunto."));

        // Nivel 0 (DEC-33, REC-035), por delante del kill-switch de
        // despliegue de abajo — ver el mismo gate en VerificacionIaDocumentoService.
        if (tenantActual.TenantId is not { } tenantId || !await instruccionTratamientoIa.EstaHabilitadaAsync(tenantId, cancellationToken))
            return Result.Exito(SinDeteccion(adjunto.Id));

        // Apagado por defecto (ver ExtraccionDocumentoAdjuntoOptions): sin DPA
        // que cubra este tratamiento, el gestor rellena los campos a mano —
        // el resto del flujo (crear/renovar el Documento) funciona igual.
        if (!opciones.Value.Activa)
            return Result.Exito(SinDeteccion(adjunto.Id));

        await using var flujo = await almacenamiento.AbrirAsync(adjunto.ArchivoUrl, cancellationToken);
        using var memoria = new MemoryStream();
        await flujo.CopyToAsync(memoria, cancellationToken);

        var resultado = await router.ProcesarAsync(memoria.ToArray(), adjunto.NombreArchivo, TipoEsperadoDesconocido, cancellationToken: cancellationToken);
        if (resultado.EsFallido)
            return Result.Fallo<DeteccionActualizacionDocumentoDto>(resultado.Error);

        var extraccion = resultado.Valor;
        if (extraccion.ConfianzaGeneral < UmbralConfianzaSugerencia)
            return Result.Exito(SinDeteccion(adjunto.Id) with { ConfianzaGeneral = extraccion.ConfianzaGeneral });

        var tipoDocumento = await DetectarTipoDocumentoAsync(extraccion.TipoDetectado, cancellationToken);

        (Guid? trabajadorId, string? trabajadorNombre) = (null, null);
        (Guid? empresaId, string? empresaNombre) = (null, null);

        if (tipoDocumento?.Ambito == AmbitoAplicacion.Trabajador)
            (trabajadorId, trabajadorNombre) = await DetectarTrabajadorAsync(extraccion.Campos, cancellationToken);
        else if (tipoDocumento?.Ambito == AmbitoAplicacion.Empresa)
            (empresaId, empresaNombre) = await DetectarEmpresaAsync(extraccion.Campos, cancellationToken);

        var fechaEmision = ParsearFecha(extraccion.Campos.GetValueOrDefault("fechaEmision"));
        var fechaVencimiento = ParsearFecha(extraccion.Campos.GetValueOrDefault("fechaVencimiento"));

        return Result.Exito(new DeteccionActualizacionDocumentoDto(
            adjunto.Id, tipoDocumento?.Id, tipoDocumento?.Nombre, tipoDocumento?.Ambito,
            trabajadorId, trabajadorNombre, empresaId, empresaNombre,
            fechaEmision, fechaVencimiento, extraccion.ConfianzaGeneral));
    }

    private static DeteccionActualizacionDocumentoDto SinDeteccion(Guid adjuntoId) =>
        new(adjuntoId, null, null, null, null, null, null, null, null, null, 0);

    /// <summary>Solo sugiere ante una única coincidencia razonable — mismo criterio que DetectarCamposDocumentoQueryHandler.</summary>
    private async Task<TipoDocumentoDetectado?> DetectarTipoDocumentoAsync(string? tipoDetectado, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tipoDetectado))
            return null;

        var tipos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador || t.AmbitoAplicacion == AmbitoAplicacion.Empresa)
            .Select(t => new { t.Id, t.Nombre, t.AmbitoAplicacion })
            .ToListAsync(cancellationToken);

        var coincidencias = tipos
            .Where(t => t.Nombre.Contains(tipoDetectado, StringComparison.OrdinalIgnoreCase)
                || tipoDetectado.Contains(t.Nombre, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return coincidencias.Count == 1
            ? new TipoDocumentoDetectado(coincidencias[0].Id, coincidencias[0].Nombre, coincidencias[0].AmbitoAplicacion)
            : null;
    }

    /// <summary>La identidad se confirma por DNI, nunca por el nombre — mismo criterio que DetectarCamposDocumentoQueryHandler.</summary>
    private async Task<(Guid? Id, string? Nombre)> DetectarTrabajadorAsync(
        IReadOnlyDictionary<string, string?> campos, CancellationToken cancellationToken)
    {
        var dniDetectado = campos.GetValueOrDefault("documentoIdentidadTrabajador");
        if (string.IsNullOrWhiteSpace(dniDetectado))
            return (null, null);

        var dniNormalizado = NormalizarIdentificador(dniDetectado);
        var trabajadores = await trabajadoresContext.Trabajadores
            .Select(t => new { t.Id, t.Dni, t.Nombre, t.Apellidos })
            .ToListAsync(cancellationToken);

        var trabajador = trabajadores.FirstOrDefault(t => NormalizarIdentificador(t.Dni) == dniNormalizado);
        return trabajador is null ? (null, null) : (trabajador.Id, $"{trabajador.Nombre} {trabajador.Apellidos}");
    }

    /// <summary>Análogo a DetectarTrabajadorAsync pero por CIF — sin equivalente en el ámbito Trabajador porque ahí la identidad es el DNI, aquí es el CIF de la empresa emisora/afectada.</summary>
    private async Task<(Guid? Id, string? Nombre)> DetectarEmpresaAsync(
        IReadOnlyDictionary<string, string?> campos, CancellationToken cancellationToken)
    {
        var cifDetectado = campos.GetValueOrDefault("cifEmpresa");
        if (string.IsNullOrWhiteSpace(cifDetectado))
            return (null, null);

        var cifNormalizado = NormalizarIdentificador(cifDetectado);
        var empresas = await empresasContext.Empresas
            .Where(e => e.Cif != null)
            .Select(e => new { e.Id, e.Cif, e.RazonSocial })
            .ToListAsync(cancellationToken);

        var empresa = empresas.FirstOrDefault(e => NormalizarIdentificador(e.Cif!) == cifNormalizado);
        return empresa is null ? (null, null) : (empresa.Id, empresa.RazonSocial);
    }

    // string? — un trabajador anonimizado (Dni null) nunca debe casar con un
    // identificador detectado por IA: normaliza a "", que ninguna detección
    // real produce.
    private static string NormalizarIdentificador(string? valor) =>
        valor is null ? string.Empty : new string(valor.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static DateOnly? ParsearFecha(string? valor) =>
        !string.IsNullOrWhiteSpace(valor) && DateOnly.TryParse(valor, out var fecha) ? fecha : null;

    private record TipoDocumentoDetectado(Guid Id, string Nombre, AmbitoAplicacion Ambito);
}
