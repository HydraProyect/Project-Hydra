using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Operaciones.IncorporacionCartera.Queries;

/// <summary>
/// La bandeja de incorporaciones a cartera, según quién la mira dentro de su
/// Operador CAE:
/// <list type="bullet">
/// <item>un Coordinador CAE ve las pendientes de todo su Operador CAE (que
/// puede aceptar o rechazar, salvo las suyas) y las incorporaciones vigentes
/// (que puede revocar);</item>
/// <item>un Gestor CAE ve las suyas, en cualquier estado, y puede revocar las
/// aceptadas.</item>
/// </list>
/// Nadie más la ve. Las pendientes son la fuente del aviso emergente de los
/// Coordinadores CAE: resolver una la quita de la bandeja y del aviso de
/// todos a la vez, porque no hay una notificación por destinatario que borrar.
/// </summary>
/// <param name="SoloPendientes">Para el aviso emergente: solo la lista de pendientes, sin las demás.</param>
public record ObtenerSolicitudesIncorporacionCarteraQuery(bool SoloPendientes = false)
    : IRequest<Result<BandejaIncorporacionCarteraDto>>;

public record BandejaIncorporacionCarteraDto(
    bool EsCoordinadorCae,
    IReadOnlyList<SolicitudIncorporacionCarteraDto> Pendientes,
    IReadOnlyList<SolicitudIncorporacionCarteraDto> Incorporaciones,
    IReadOnlyList<SolicitudIncorporacionCarteraDto> Propias);

/// <param name="NombreEmpresa">Nombre del Tenant propietario, rotulado «Empresa» en pantalla (contrato § 14).</param>
public record SolicitudIncorporacionCarteraDto(
    Guid Id,
    Guid TenantPropietarioId,
    string NombreEmpresa,
    Guid SolicitanteUsuarioId,
    string NombreSolicitante,
    string Mensaje,
    EstadoSolicitudIncorporacionCartera Estado,
    DateTime CreadaEnUtc,
    DateTime? ResueltaEnUtc,
    string? NombreResolutor,
    bool PuedeResolver,
    bool PuedeRevocar);

public class ObtenerSolicitudesIncorporacionCarteraQueryHandler(
    ICurrentUserService currentUserService,
    ICatalogoIncorporacionCartera catalogo,
    ISolicitudIncorporacionCarteraRepository repositorio,
    IDirectorioUsuariosService directorioUsuarios,
    ITenantsQueryContext tenants)
    : IRequestHandler<ObtenerSolicitudesIncorporacionCarteraQuery, Result<BandejaIncorporacionCarteraDto>>
{
    public async Task<Result<BandejaIncorporacionCarteraDto>> Handle(
        ObtenerSolicitudesIncorporacionCarteraQuery request, CancellationToken cancellationToken)
    {
        var contexto = await ContextoOperadorCae.ResolverAsync(currentUserService, directorioUsuarios, cancellationToken);
        if (contexto.EsFallido) return Result.Fallo<BandejaIncorporacionCarteraDto>(contexto.Error);
        var ctx = contexto.Valor;

        if (!ctx.ParticipaEnIncorporacionCartera)
            return Result.Fallo<BandejaIncorporacionCarteraDto>(ErroresSolicitudCartera.SinPermiso);

        using (ctx.EnOrigen())
        {
            IReadOnlyList<SolicitudIncorporacionCartera> pendientes = [];
            IReadOnlyList<SolicitudIncorporacionCartera> incorporaciones = [];
            IReadOnlyList<SolicitudIncorporacionCartera> propias = [];

            if (ctx.EsCoordinadorCae)
            {
                pendientes = await repositorio.ListarPendientesAsync(ctx.OperadorTenantId, cancellationToken);
                if (!request.SoloPendientes)
                    incorporaciones = await SoloConCarteraVigenteAsync(
                        await repositorio.ListarAceptadasAsync(ctx.OperadorTenantId, cancellationToken), cancellationToken);
            }
            else if (!request.SoloPendientes)
            {
                propias = await repositorio.ListarDelSolicitanteAsync(ctx.OperadorTenantId, ctx.UsuarioId, cancellationToken);
            }

            var todas = pendientes.Concat(incorporaciones).Concat(propias).ToList();
            var nombresEmpresa = await NombresEmpresaAsync(todas, cancellationToken);
            var nombresUsuario = await directorioUsuarios.ObtenerNombresVisiblesAsync(
                todas.Select(s => s.SolicitanteUsuarioId)
                    .Concat(todas.Where(s => s.ResueltaPorUsuarioId is not null).Select(s => s.ResueltaPorUsuarioId!.Value))
                    .Distinct().ToList(),
                cancellationToken);

            SolicitudIncorporacionCarteraDto ADto(SolicitudIncorporacionCartera s)
            {
                var esLaSuya = s.SolicitanteUsuarioId == ctx.UsuarioId;
                var aceptada = s.Estado == EstadoSolicitudIncorporacionCartera.Aceptada;

                return new SolicitudIncorporacionCarteraDto(
                    s.Id,
                    s.PropietarioTenantId,
                    nombresEmpresa.GetValueOrDefault(s.PropietarioTenantId, string.Empty),
                    s.SolicitanteUsuarioId,
                    nombresUsuario.GetValueOrDefault(s.SolicitanteUsuarioId, string.Empty),
                    s.Mensaje,
                    s.Estado,
                    s.CreadaEnUtc,
                    s.ResueltaEnUtc,
                    s.ResueltaPorUsuarioId is { } r ? nombresUsuario.GetValueOrDefault(r) : null,
                    PuedeResolver: ctx.EsCoordinadorCae && !esLaSuya && s.Estado == EstadoSolicitudIncorporacionCartera.Pendiente,
                    PuedeRevocar: aceptada && (ctx.EsCoordinadorCae || esLaSuya));
            }

            return Result.Exito(new BandejaIncorporacionCarteraDto(
                ctx.EsCoordinadorCae,
                pendientes.Select(ADto).ToList(),
                incorporaciones.Select(ADto).ToList(),
                propias.Select(ADto).ToList()));
        }
    }

    /// <summary>
    /// Una aceptada cuya cartera ya se cerró por otra vía (el Tenant
    /// propietario retiró al operador, la operación terminó) no es una
    /// incorporación vigente: no hay nada que revocar.
    /// </summary>
    private async Task<IReadOnlyList<SolicitudIncorporacionCartera>> SoloConCarteraVigenteAsync(
        IReadOnlyList<SolicitudIncorporacionCartera> aceptadas, CancellationToken cancellationToken)
    {
        var vigentes = await catalogo.FiltrarCarterasVigentesAsync(
            aceptadas.Where(s => s.AsignacionCarteraId is not null).Select(s => s.AsignacionCarteraId!.Value).ToList(),
            cancellationToken);

        return aceptadas.Where(s => s.AsignacionCarteraId is { } id && vigentes.Contains(id)).ToList();
    }

    private async Task<Dictionary<Guid, string>> NombresEmpresaAsync(
        IReadOnlyCollection<SolicitudIncorporacionCartera> solicitudes, CancellationToken cancellationToken)
    {
        var ids = solicitudes.Select(s => s.PropietarioTenantId).Distinct().ToList();
        if (ids.Count == 0) return [];

        return await tenants.Tenants
            .Where(t => ids.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);
    }
}
