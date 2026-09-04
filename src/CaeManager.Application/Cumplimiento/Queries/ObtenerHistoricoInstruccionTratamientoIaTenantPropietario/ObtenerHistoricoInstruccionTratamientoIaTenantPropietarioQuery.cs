using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Cumplimiento;
using MediatR;

namespace CaeManager.Application.Cumplimiento.Queries.ObtenerHistoricoInstruccionTratamientoIaTenantPropietario;

/// <summary>
/// Lo que demuestra el criterio de aceptación de HO-035-02 § 15.3: qué
/// versión de DPA y de Anexo II aceptó un Tenant propietario, cuándo, y si
/// sigue vigente — histórico completo, no solo la fila actual.
///
/// Consulta de plataforma (mismo alcance que
/// <c>ObtenerEstadoComercialTenantsQuery</c>/<c>RegistrarInstruccionTratamientoIaTenantPropietarioCommand</c>):
/// no hay autoservicio del propio Tenant propietario todavía — si en el
/// futuro se decide mostrárselo también a su Administrador, es una decisión
/// de producto/legal aparte (qué se le enseña de su propio cumplimiento),
/// no una consecuencia automática de que el dato exista.
/// </summary>
public record ObtenerHistoricoInstruccionTratamientoIaTenantPropietarioQuery(Guid TenantPropietarioId)
    : IRequest<Result<IReadOnlyList<InstruccionTratamientoIaTenantPropietarioDto>>>;

public record InstruccionTratamientoIaTenantPropietarioDto(
    Guid Id,
    string VersionDpaAceptada,
    string VersionAnexoSubencargadosAceptada,
    DateTime FechaAceptacionUtc,
    OrigenInstruccionTratamientoIa OrigenInstruccion,
    Guid RegistradaPorUsuarioId,
    bool EstaVigente,
    DateTime? RevocadaEnUtc,
    string? MotivoRevocacion);

public class ObtenerHistoricoInstruccionTratamientoIaTenantPropietarioQueryHandler(
    IAutorizacionAdminPlataforma autorizacion,
    ICurrentUserService currentUserService,
    IInstruccionTratamientoIaTenantPropietarioRepository repositorio)
    : IRequestHandler<ObtenerHistoricoInstruccionTratamientoIaTenantPropietarioQuery, Result<IReadOnlyList<InstruccionTratamientoIaTenantPropietarioDto>>>
{
    public async Task<Result<IReadOnlyList<InstruccionTratamientoIaTenantPropietarioDto>>> Handle(
        ObtenerHistoricoInstruccionTratamientoIaTenantPropietarioQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<IReadOnlyList<InstruccionTratamientoIaTenantPropietarioDto>>(
                Error.Crear("InstruccionTratamientoIa.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        if (!await autorizacion.PuedeSobreTenantAsync(usuarioId.Value, request.TenantPropietarioId, cancellationToken))
            return Result.Fallo<IReadOnlyList<InstruccionTratamientoIaTenantPropietarioDto>>(Error.Crear(
                "InstruccionTratamientoIa.SinAutoridad",
                "No tienes capacidad de administración de plataforma sobre ese tenant."));

        // Misma razón que en Registrar/Revocar: la tabla lleva RLS + filtro
        // global por TenantId, así que la lectura cruzada exige el ámbito
        // explícito del tenant objetivo — leerla desde el tenant de origen
        // del administrador vería siempre cero filas.
        using (AmbitoTenantExplicito.Establecer(request.TenantPropietarioId))
        {
            var historico = await repositorio.ObtenerHistoricoAsync(request.TenantPropietarioId, cancellationToken);

            IReadOnlyList<InstruccionTratamientoIaTenantPropietarioDto> dto = historico
                .Select(i => new InstruccionTratamientoIaTenantPropietarioDto(
                    i.Id, i.VersionDpaAceptada, i.VersionAnexoSubencargadosAceptada, i.FechaAceptacionUtc,
                    i.OrigenInstruccion, i.RegistradaPorUsuarioId, i.EstaVigente, i.RevocadaEnUtc, i.MotivoRevocacion))
                .ToList();

            return Result.Exito(dto);
        }
    }
}
