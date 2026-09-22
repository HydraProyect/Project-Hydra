using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using MediatR;

namespace CaeManager.Application.Operaciones.IncorporacionCartera.Queries;

/// <summary>
/// Las empresas (Tenants propietarios) que el Gestor CAE puede pedir añadir a
/// su cartera. Solo el nombre: la lista no enseña ni un dato de dentro del
/// Tenant, porque todavía no tiene acceso a él.
/// </summary>
public record ObtenerCandidatosIncorporacionCarteraQuery : IRequest<Result<IReadOnlyList<CandidatoIncorporacionCarteraDto>>>;

/// <param name="SolicitudPendienteId">La solicitud que ya envió y sigue esperando, si la hay: la pantalla la muestra como «Solicitud enviada» en vez de ofrecer pedirla otra vez.</param>
public record CandidatoIncorporacionCarteraDto(Guid TenantId, string Nombre, Guid? SolicitudPendienteId);

public class ObtenerCandidatosIncorporacionCarteraQueryHandler(
    ICurrentUserService currentUserService,
    ICatalogoIncorporacionCartera catalogo,
    ISolicitudIncorporacionCarteraRepository repositorio)
    : IRequestHandler<ObtenerCandidatosIncorporacionCarteraQuery, Result<IReadOnlyList<CandidatoIncorporacionCarteraDto>>>
{
    public async Task<Result<IReadOnlyList<CandidatoIncorporacionCarteraDto>>> Handle(
        ObtenerCandidatosIncorporacionCarteraQuery request, CancellationToken cancellationToken)
    {
        var contexto = await ContextoOperadorCae.ResolverAsync(currentUserService);
        if (contexto.EsFallido) return Result.Fallo<IReadOnlyList<CandidatoIncorporacionCarteraDto>>(contexto.Error);
        var ctx = contexto.Valor;

        if (!ctx.EsGestorCae)
            return Result.Fallo<IReadOnlyList<CandidatoIncorporacionCarteraDto>>(ErroresSolicitudCartera.SinPermiso);

        using (ctx.EnOrigen())
        {
            var candidatos = await catalogo.ObtenerCandidatosAsync(ctx.OperadorTenantId, ctx.UsuarioId, cancellationToken);
            if (candidatos.Count == 0) return Result.Exito<IReadOnlyList<CandidatoIncorporacionCarteraDto>>([]);

            var pendientes = (await repositorio.ListarDelSolicitanteAsync(ctx.OperadorTenantId, ctx.UsuarioId, cancellationToken))
                .Where(s => s.Estado == EstadoSolicitudIncorporacionCartera.Pendiente)
                .ToDictionary(s => s.AsignacionOperacionId, s => s.Id);

            IReadOnlyList<CandidatoIncorporacionCarteraDto> resultado = candidatos
                .Select(c => new CandidatoIncorporacionCarteraDto(
                    c.PropietarioTenantId, c.Nombre,
                    pendientes.TryGetValue(c.AsignacionOperacionId, out var id) ? id : null))
                .ToList();

            return Result.Exito(resultado);
        }
    }
}
