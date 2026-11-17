using CaeManager.Application.CapacidadesTenant;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.DerechosInteresado;
using CaeManager.Domain.Tenants;
using MediatR;

namespace CaeManager.Application.DerechosInteresado.Queries;

// Lectura del registro de solicitudes de derechos del interesado del Tenant actual. La lee quien puede tramitarlo
// (capacidad «Derechos del interesado», AutoridadCapacidadesTenant): quien solo registra, por ejemplo un Gestor CAE
// de un Operador CAE externo, no lee el registro (falla cerrado: lista vacía, ficha nula). El registro es del
// Tenant entero, no de una cartera: no pasa por IAlcanceDatosService.

/// <summary>Las solicitudes del Tenant actual, de la que antes vence a la que más tarde.</summary>
[AreaDelEncargo("DerechosInteresado")]
public record ListarSolicitudesDerechoInteresadoQuery : IRequest<IReadOnlyList<SolicitudDerechoInteresadoDto>>;

/// <summary>La ficha de una solicitud del Tenant actual; nula si no existe o quien pregunta no la puede tramitar.</summary>
[AreaDelEncargo("DerechosInteresado")]
public record ObtenerSolicitudDerechoInteresadoQuery(Guid SolicitudId) : IRequest<SolicitudDerechoInteresadoDto?>;

public record SolicitudDerechoInteresadoDto(
    Guid Id,
    TipoDerechoInteresado Tipo,
    CanalEntradaSolicitudDerecho Canal,
    DateOnly FechaRecepcion,
    string NombreInteresado,
    Guid? TrabajadorId,
    string? Descripcion,
    EstadoSolicitudDerecho Estado,
    DateOnly PlazoOrdinario,
    DateOnly PlazoVencimiento,
    bool EstaProrrogada,
    string? MotivoProrroga,
    Guid RegistradaPorUsuarioId,
    DateTime RegistradaEnUtc,
    Guid? IdentidadVerificadaPorUsuarioId,
    DateTime? IdentidadVerificadaEnUtc,
    Guid? TramitacionIniciadaPorUsuarioId,
    DateTime? TramitacionIniciadaEnUtc,
    Guid? ProrrogadaPorUsuarioId,
    DateTime? ProrrogadaEnUtc,
    Guid? CerradaPorUsuarioId,
    DateTime? CerradaEnUtc,
    string? Resultado,
    string? MotivoDenegacion,
    Guid Version)
{
    public static SolicitudDerechoInteresadoDto Desde(SolicitudDerechoInteresado s) => new(
        s.Id, s.Tipo, s.Canal, s.FechaRecepcion, s.NombreInteresado, s.TrabajadorId, s.Descripcion, s.Estado,
        s.PlazoOrdinario, s.PlazoVencimiento, s.EstaProrrogada, s.MotivoProrroga,
        s.RegistradaPorUsuarioId, s.RegistradaEnUtc,
        s.IdentidadVerificadaPorUsuarioId, s.IdentidadVerificadaEnUtc,
        s.TramitacionIniciadaPorUsuarioId, s.TramitacionIniciadaEnUtc,
        s.ProrrogadaPorUsuarioId, s.ProrrogadaEnUtc,
        s.CerradaPorUsuarioId, s.CerradaEnUtc, s.Resultado, s.MotivoDenegacion, s.Version);
}

public class SolicitudesDerechoInteresadoQueryHandler(
    IAutoridadCapacidadesTenant autoridad,
    ISolicitudDerechoInteresadoRepository solicitudes)
    : IRequestHandler<ListarSolicitudesDerechoInteresadoQuery, IReadOnlyList<SolicitudDerechoInteresadoDto>>,
      IRequestHandler<ObtenerSolicitudDerechoInteresadoQuery, SolicitudDerechoInteresadoDto?>
{
    public async Task<IReadOnlyList<SolicitudDerechoInteresadoDto>> Handle(
        ListarSolicitudesDerechoInteresadoQuery request, CancellationToken cancellationToken)
    {
        if (!await autoridad.TieneCapacidadAsync(CapacidadTenant.DerechosDelInteresado, cancellationToken))
            return [];

        return (await solicitudes.ListarAsync(cancellationToken)).Select(SolicitudDerechoInteresadoDto.Desde).ToList();
    }

    public async Task<SolicitudDerechoInteresadoDto?> Handle(
        ObtenerSolicitudDerechoInteresadoQuery request, CancellationToken cancellationToken)
    {
        if (!await autoridad.TieneCapacidadAsync(CapacidadTenant.DerechosDelInteresado, cancellationToken))
            return null;

        var solicitud = await solicitudes.ObtenerPorIdAsync(request.SolicitudId, cancellationToken);
        return solicitud is null ? null : SolicitudDerechoInteresadoDto.Desde(solicitud);
    }
}
