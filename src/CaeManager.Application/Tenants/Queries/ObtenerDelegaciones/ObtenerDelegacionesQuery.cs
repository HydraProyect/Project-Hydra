using CaeManager.Application.Common;
using CaeManager.Domain.Tenants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Queries.ObtenerDelegaciones;

/// <summary>
/// Delegaciones en las que participa el tenant de origen del usuario, en
/// cualquiera de los dos papeles: las que ha concedido (es el Cliente
/// Delegante) y las que ha recibido (es la Consultora). Alimenta la pantalla
/// de administración de delegaciones (hallazgo N-4 de INFORME-AUDITORIA-2.md).
///
/// Tenant de <b>origen</b>, no <c>ITenantActual</c>, por el mismo motivo que
/// los comandos de revocación: administrar delegaciones se hace desde la casa
/// de uno, nunca desde un Delegated Workspace ajeno que se esté operando.
///
/// Devuelve también las revocadas — el histórico de quién operó sobre qué es
/// justamente lo que conserva "desactivar, nunca borrar" (ADR-004 § 5.5), y
/// sin verlas no se puede reactivar una.
/// </summary>
public record ObtenerDelegacionesQuery : IRequest<IReadOnlyList<DelegacionDto>>;

public record OperadorDelegadoDto(Guid AsignacionId, Guid UsuarioId, string Rol);

public record DelegacionDto(
    Guid Id,
    Guid TenantConsultoraId,
    string ConsultoraNombre,
    Guid TenantClienteId,
    string ClienteNombre,
    bool Activa,
    bool SomosLaConsultora,
    DateTime CreadoEnUtc,
    IReadOnlyList<OperadorDelegadoDto> Operadores,
    PropositoDelegacion Proposito,
    string? MotivoActivacion,
    DateTime? ExpiraEnUtc)
{
    /// <summary>
    /// Una delegación de soporte apagada no está "revocada": está
    /// aprovisionada y sin abrir, que es su estado normal en reposo.
    /// Distinguirlo importa porque el botón que corresponde es otro.
    /// </summary>
    public bool EsSoporte => Proposito == PropositoDelegacion.Soporte;

    public bool VentanaCaducada => ExpiraEnUtc is { } expira && expira <= DateTime.UtcNow;

    public bool AccesoVigente => Activa && !VentanaCaducada;
}

public class ObtenerDelegacionesQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerDelegacionesQuery, IReadOnlyList<DelegacionDto>>
{
    public async Task<IReadOnlyList<DelegacionDto>> Handle(
        ObtenerDelegacionesQuery request, CancellationToken cancellationToken)
    {
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (tenantOrigenId is null) return [];

        var delegaciones = await (
            from delegacion in dbContext.DelegacionesTenant
            join consultora in dbContext.Tenants on delegacion.TenantConsultoraId equals consultora.Id
            join cliente in dbContext.Tenants on delegacion.TenantClienteId equals cliente.Id
            where delegacion.TenantConsultoraId == tenantOrigenId.Value || delegacion.TenantClienteId == tenantOrigenId.Value
            orderby delegacion.Activa descending, cliente.Nombre
            select new
            {
                delegacion.Id,
                delegacion.TenantConsultoraId,
                ConsultoraNombre = consultora.Nombre,
                delegacion.TenantClienteId,
                ClienteNombre = cliente.Nombre,
                delegacion.Activa,
                delegacion.CreadoEnUtc,
                delegacion.Proposito,
                delegacion.MotivoActivacion,
                delegacion.ExpiraEnUtc
            })
            .ToListAsync(cancellationToken);

        if (delegaciones.Count == 0) return [];

        var delegacionIds = delegaciones.Select(d => d.Id).ToList();

        var operadores = await dbContext.AsignacionesOperadorDelegado
            .Where(a => delegacionIds.Contains(a.DelegacionTenantId))
            .Select(a => new { a.DelegacionTenantId, a.Id, a.UsuarioId, a.Rol })
            .ToListAsync(cancellationToken);

        var operadoresPorDelegacion = operadores
            .GroupBy(a => a.DelegacionTenantId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<OperadorDelegadoDto>)g
                    .Select(a => new OperadorDelegadoDto(a.Id, a.UsuarioId, a.Rol))
                    .ToList());

        return delegaciones.Select(d => new DelegacionDto(
            d.Id, d.TenantConsultoraId, d.ConsultoraNombre, d.TenantClienteId, d.ClienteNombre,
            d.Activa, d.TenantConsultoraId == tenantOrigenId.Value, d.CreadoEnUtc,
            operadoresPorDelegacion.GetValueOrDefault(d.Id, []),
            d.Proposito, d.MotivoActivacion, d.ExpiraEnUtc)).ToList();
    }
}
