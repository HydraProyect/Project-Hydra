using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Usuarios.Commands.AsignarCarteraGestorCae;
using MediatR;

namespace CaeManager.Application.Operaciones.ApoyoCartera.Queries;

/// <summary>
/// Un apoyo vivo, con los nombres para pintarlo. <paramref name="UltimoDia"/> es el último día
/// de negocio en que sigue vigente («Apoyo hasta»); <c>null</c> si no caduca («Apoyo»).
/// </summary>
public record ApoyoDeCarteraDto(
    Guid PropuestaId,
    Guid TenantPropietarioId,
    string NombreEmpresa,
    Guid AsignacionOperacionId,
    Guid ApoyoUsuarioId,
    string NombreApoyo,
    Guid ProponenteUsuarioId,
    string NombreProponente,
    DateOnly? UltimoDia);

/// <summary>
/// Los apoyos vivos sobre los que quien mira puede hacer algo, repartidos por lo que puede
/// hacer. Un apoyo sale en una sola lista.
/// </summary>
/// <param name="Mios">Aquellos en los que quien mira es el Gestor CAE de apoyo: puede desasignarse.</param>
/// <param name="Concedidos">Los que concedió quien mira y de cuya operación sigue siendo el principal: puede retirarlos.</param>
/// <param name="Revocables">Los demás que puede revocar por su rol y, si es Coordinador CAE, por su equipo.</param>
public record ApoyosDeCarteraDto(
    IReadOnlyList<ApoyoDeCarteraDto> Mios,
    IReadOnlyList<ApoyoDeCarteraDto> Concedidos,
    IReadOnlyList<ApoyoDeCarteraDto> Revocables)
{
    public static readonly ApoyosDeCarteraDto Vacio = new([], [], []);

    public bool EstaVacio => Mios.Count == 0 && Concedidos.Count == 0 && Revocables.Count == 0;
}

/// <summary>
/// Alimenta las acciones de fin de apoyo («Desasignarme», «Retirar», «Revocar»). Es solo lo que
/// se enseña: quién puede lo vuelven a decidir <c>DesasignarmeDeApoyoCommand</c>,
/// <c>RetirarApoyoConcedidoCommand</c> y <c>RevocarApoyoCarteraCommand</c>, con la misma regla
/// (<see cref="AutoridadSobreCarteraDeGestorCae.PuedeRevocarApoyoAsync"/> para la revocación).
///
/// <para>
/// Se resuelve en el Tenant de origen del Operador CAE, sea cual sea el Tenant activo, y el
/// rol se lee en Identity. Con <paramref name="TenantId"/>, solo los apoyos sobre ese Tenant
/// propietario.
/// </para>
/// </summary>
public record ObtenerApoyosDeCarteraQuery(Guid? TenantId = null) : IRequest<ApoyosDeCarteraDto>;

public class ObtenerApoyosDeCarteraQueryHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    IDirectorioDestinosCartera directorioDestinos,
    ICatalogoIncorporacionCartera catalogo)
    : IRequestHandler<ObtenerApoyosDeCarteraQuery, ApoyosDeCarteraDto>
{
    private const string GestorCae = "GestorCae";
    private const string CoordinadorCae = "CoordinadorCae";

    // Por orden de autoridad: una cuenta con dos roles se trata por el mayor.
    private static readonly string[] RolesQueLeen = ["Administrador", "DireccionCae", CoordinadorCae, GestorCae];

    public async Task<ApoyosDeCarteraDto> Handle(ObtenerApoyosDeCarteraQuery request, CancellationToken cancellationToken)
    {
        var actorId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var origen = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (actorId is not { } yo || origen is not { } operadorTenantId)
            return ApoyosDeCarteraDto.Vacio;

        using (AmbitoTenantExplicito.Establecer(operadorTenantId))
        {
            // Falla cerrado sin rol de negocio en la sesión: Soporte TALVEG nunca es Operador CAE.
            if (await currentUserService.ObtenerRolEfectivoAsync() is null)
                return ApoyosDeCarteraDto.Vacio;

            string? rol = null;
            foreach (var candidato in RolesQueLeen)
            {
                if (await directorioUsuarios.EsCuentaActivaConRolAsync(yo, operadorTenantId, candidato, cancellationToken))
                {
                    rol = candidato;
                    break;
                }
            }

            if (rol is null)
                return ApoyosDeCarteraDto.Vacio;

            var apoyos = (await catalogo.ObtenerApoyosVivosAsync(operadorTenantId, cancellationToken))
                .Where(a => request.TenantId is null || a.PropietarioTenantId == request.TenantId)
                .ToList();
            if (apoyos.Count == 0)
                return ApoyosDeCarteraDto.Vacio;

            // Desasignarse y retirar lo concedido son de quien gestiona CAE, como en los Commands.
            var gestionaCae = rol is GestorCae or CoordinadorCae;
            var dondeSoyPrincipal = gestionaCae
                ? (await catalogo.ObtenerCarterasVivasAsync(operadorTenantId, request.TenantId, cancellationToken))
                    .Where(c => c.EsPrincipal && c.UsuarioId == yo)
                    .Select(c => c.AsignacionOperacionId)
                    .ToHashSet()
                : [];

            var mios = new List<ApoyoVivoDeCartera>();
            var concedidos = new List<ApoyoVivoDeCartera>();
            var revocables = new List<ApoyoVivoDeCartera>();
            foreach (var apoyo in apoyos)
            {
                if (gestionaCae && apoyo.ApoyoUsuarioId == yo)
                    mios.Add(apoyo);
                else if (gestionaCae && apoyo.ProponenteUsuarioId == yo && dondeSoyPrincipal.Contains(apoyo.AsignacionOperacionId))
                    concedidos.Add(apoyo);
                else if (await AutoridadSobreCarteraDeGestorCae.PuedeRevocarApoyoAsync(
                             rol, yo, apoyo.ApoyoUsuarioId, apoyo.ProponenteUsuarioId, directorioDestinos, cancellationToken))
                    revocables.Add(apoyo);
            }

            if (mios.Count == 0 && concedidos.Count == 0 && revocables.Count == 0)
                return ApoyosDeCarteraDto.Vacio;

            var nombres = await directorioUsuarios.ObtenerNombresVisiblesAsync(
                apoyos.SelectMany(a => new[] { a.ApoyoUsuarioId, a.ProponenteUsuarioId }).Distinct().ToList(),
                cancellationToken);

            ApoyoDeCarteraDto ADto(ApoyoVivoDeCartera a) => new(
                a.PropuestaId,
                a.PropietarioTenantId,
                a.NombreTenant,
                a.AsignacionOperacionId,
                a.ApoyoUsuarioId,
                nombres.GetValueOrDefault(a.ApoyoUsuarioId) ?? string.Empty,
                a.ProponenteUsuarioId,
                nombres.GetValueOrDefault(a.ProponenteUsuarioId) ?? string.Empty,
                a.VigenciaHasta is { } hasta ? VigenciaDeApoyo.UltimoDia(hasta) : null);

            return new ApoyosDeCarteraDto(
                mios.Select(ADto).ToList(), concedidos.Select(ADto).ToList(), revocables.Select(ADto).ToList());
        }
    }
}
