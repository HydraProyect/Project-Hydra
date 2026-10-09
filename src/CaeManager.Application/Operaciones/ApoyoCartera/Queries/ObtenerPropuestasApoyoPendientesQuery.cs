using CaeManager.Application.Common;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Operaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Operaciones.ApoyoCartera.Queries;

/// <summary>
/// Las propuestas de apoyo pendientes de quien pregunta, dentro de su Operador CAE:
/// <list type="bullet">
/// <item><see cref="PropuestasApoyoPendientesDto.Recibidas"/>: las dirigidas a él, que puede
/// aceptar o rechazar. Son la fuente de la campana de avisos.</item>
/// <item><see cref="PropuestasApoyoPendientesDto.Enviadas"/>: las que él propuso, que puede
/// retirar.</item>
/// </list>
/// Acotada al Tenant de origen de la sesión, nunca a un dato de la petición ni al Tenant
/// activo: el destinatario las ve trabaje en el Tenant que trabaje. Vacía, sin error, si quien
/// pregunta no es una cuenta activa con rol Gestor CAE o Coordinador CAE (leído en Identity
/// sobre el Tenant de origen): la campana pregunta por todo el mundo.
/// </summary>
public record ObtenerPropuestasApoyoPendientesQuery : IRequest<PropuestasApoyoPendientesDto>;

public record PropuestasApoyoPendientesDto(
    IReadOnlyList<PropuestaApoyoDto> Recibidas, IReadOnlyList<PropuestaApoyoDto> Enviadas)
{
    public static readonly PropuestasApoyoPendientesDto Vacia = new([], []);
}

/// <param name="NombreEmpresa">Nombre del Tenant propietario, rotulado «Empresa» en pantalla.</param>
public record PropuestaApoyoDto(
    Guid Id,
    Guid TenantPropietarioId,
    string NombreEmpresa,
    Guid AsignacionOperacionId,
    Guid ProponenteUsuarioId,
    string NombreProponente,
    Guid DestinatarioUsuarioId,
    string NombreDestinatario,
    DateTime CreadaEnUtc);

public class ObtenerPropuestasApoyoPendientesQueryHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    IPropuestaApoyoCarteraRepository repositorio,
    ITenantsQueryContext tenants)
    : IRequestHandler<ObtenerPropuestasApoyoPendientesQuery, PropuestasApoyoPendientesDto>
{
    public async Task<PropuestasApoyoPendientesDto> Handle(
        ObtenerPropuestasApoyoPendientesQuery request, CancellationToken cancellationToken)
    {
        var contexto = await ContextoOperadorCae.ResolverAsync(currentUserService, directorioUsuarios, cancellationToken);
        if (contexto.EsFallido || !contexto.Valor.ParticipaEnIncorporacionCartera)
            return PropuestasApoyoPendientesDto.Vacia;
        var ctx = contexto.Valor;

        using (ctx.EnOrigen())
        {
            var recibidas = await repositorio.ListarPendientesDelDestinatarioAsync(ctx.OperadorTenantId, ctx.UsuarioId, cancellationToken);
            var enviadas = await repositorio.ListarPendientesDelProponenteAsync(ctx.OperadorTenantId, ctx.UsuarioId, cancellationToken);
            if (recibidas.Count == 0 && enviadas.Count == 0)
                return PropuestasApoyoPendientesDto.Vacia;

            var todas = recibidas.Concat(enviadas).ToList();
            var tenantIds = todas.Select(p => p.PropietarioTenantId).Distinct().ToList();
            var nombresEmpresa = await tenants.Tenants
                .Where(t => tenantIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);
            var nombresUsuario = await directorioUsuarios.ObtenerNombresVisiblesAsync(
                todas.SelectMany(p => new[] { p.ProponenteUsuarioId, p.DestinatarioUsuarioId }).Distinct().ToList(),
                cancellationToken);

            PropuestaApoyoDto ADto(PropuestaApoyoCartera p) => new(
                p.Id,
                p.PropietarioTenantId,
                nombresEmpresa.GetValueOrDefault(p.PropietarioTenantId, string.Empty),
                p.AsignacionOperacionId,
                p.ProponenteUsuarioId,
                nombresUsuario.GetValueOrDefault(p.ProponenteUsuarioId, string.Empty),
                p.DestinatarioUsuarioId,
                nombresUsuario.GetValueOrDefault(p.DestinatarioUsuarioId, string.Empty),
                p.CreadaEnUtc);

            return new PropuestasApoyoPendientesDto(recibidas.Select(ADto).ToList(), enviadas.Select(ADto).ToList());
        }
    }
}
