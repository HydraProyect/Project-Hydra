using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.AutorizacionesLectura.Queries.ObtenerAutorizacionesLectura;

public enum EstadoAutorizacionLectura
{
    /// <summary>Sin revocar y dentro de su vigencia hoy.</summary>
    Vigente,

    /// <summary>Sin revocar; su vigencia empieza más adelante.</summary>
    Programada,

    /// <summary>Sin revocar; su vigencia ya terminó.</summary>
    Caducada,

    Revocada,
}

/// <param name="NumeroTrabajadores">Trabajadores asignados a la autorización.</param>
public record AutorizacionLecturaDto(
    Guid Id,
    Guid TenantDestinatarioId,
    Guid ContratoAccesoId,
    bool IncluyeDocumentosEmpresa,
    DateOnly VigenciaDesde,
    DateOnly? VigenciaHasta,
    DateTime ConcedidaEnUtc,
    DateTime? RevocadaEnUtc,
    int NumeroTrabajadores,
    EstadoAutorizacionLectura Estado,
    Guid Version);

/// <summary>
/// Las autorizaciones de lectura que el Tenant activo, como Tenant de la Empresa contratista, ha concedido. Solo sus
/// propias filas: el filtro de Tenant y RLS no dejan ver las que otros Tenants concedieron, tampoco al Tenant
/// destinatario. Solo Administrador, el mismo criterio que concederlas (<see cref="AutorizacionLecturaAutoridad"/>);
/// para el resto, lista vacía. Las más recientes primero.
/// </summary>
public record ObtenerAutorizacionesLecturaQuery : IRequest<IReadOnlyList<AutorizacionLecturaDto>>;

public class ObtenerAutorizacionesLecturaQueryHandler(
    IAutorizacionesLecturaQueryContext contexto, ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerAutorizacionesLecturaQuery, IReadOnlyList<AutorizacionLecturaDto>>
{
    public async Task<IReadOnlyList<AutorizacionLecturaDto>> Handle(
        ObtenerAutorizacionesLecturaQuery request, CancellationToken cancellationToken)
    {
        if (!await AutorizacionLecturaAutoridad.EsAdministradorAsync(currentUserService))
            return [];

        var autorizaciones = await contexto.AutorizacionesLecturaDocumental
            .OrderByDescending(a => a.ConcedidaEnUtc).ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);
        if (autorizaciones.Count == 0)
            return [];

        var ids = autorizaciones.Select(a => a.Id).ToList();
        var asignaciones = await contexto.AutorizacionesLecturaTrabajadores
            .Where(t => ids.Contains(t.AutorizacionId))
            .Select(t => t.AutorizacionId)
            .ToListAsync(cancellationToken);
        var trabajadoresPorAutorizacion = asignaciones.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());

        var hoy = DiaDeNegocio.Hoy();

        return autorizaciones
            .Select(a => new AutorizacionLecturaDto(
                a.Id, a.TenantDestinatarioId, a.ContratoAccesoId, a.IncluyeDocumentosEmpresa, a.VigenciaDesde, a.VigenciaHasta,
                a.ConcedidaEnUtc, a.RevocadaEnUtc, trabajadoresPorAutorizacion.GetValueOrDefault(a.Id),
                a.EstaRevocada ? EstadoAutorizacionLectura.Revocada
                : a.VigenteEn(hoy) ? EstadoAutorizacionLectura.Vigente
                : hoy < a.VigenciaDesde ? EstadoAutorizacionLectura.Programada
                : EstadoAutorizacionLectura.Caducada,
                a.Version))
            .ToList();
    }
}
