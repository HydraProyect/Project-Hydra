using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Usuarios.Commands.AsumirPrincipalDeOperacion;
using MediatR;

namespace CaeManager.Application.Usuarios.Queries.ObtenerOperacionesSinPrincipal;

/// <summary>Por qué una Asignación de Operación aparece en la alerta.</summary>
public enum SituacionDePrincipal
{
    /// <summary>Nadie tiene cartera viva de Gestor CAE ni de Coordinador CAE bajo la operación.</summary>
    SinNadieAsignado,

    /// <summary>Hay carteras vivas, todas de apoyo: ninguna lleva la marca.</summary>
    ConPersonasSinPrincipal,

    /// <summary>
    /// Informativo: la marca la lleva la cartera de un Coordinador CAE (relevo automático,
    /// escalado o «Asumir»). Tiene principal; se le recuerda que puede designar a un Gestor CAE.
    /// </summary>
    CoordinadorCaePrincipal,
}

/// <summary>
/// Una Asignación de Operación externa del Operador CAE en la alerta. Del Tenant propietario
/// solo el nombre. <paramref name="PersonasAsignadas"/> cuenta las carteras vivas de Gestor CAE
/// y de Coordinador CAE; <paramref name="NombrePrincipal"/> solo viene en
/// <see cref="SituacionDePrincipal.CoordinadorCaePrincipal"/>.
/// </summary>
public record OperacionEnAlertaDePrincipal(
    Guid AsignacionOperacionId, Guid PropietarioTenantId, string NombreTenant,
    SituacionDePrincipal Situacion, int PersonasAsignadas, string? NombrePrincipal, bool EsDeQuienConsulta)
{
    /// <summary>Solo se asume lo que no tiene principal vivo.</summary>
    public bool SePuedeAsumir => Situacion != SituacionDePrincipal.CoordinadorCaePrincipal;
}

/// <summary>
/// La alerta: <paramref name="LaVe"/> es <c>false</c> —y la lista, vacía— para quien no es
/// Coordinador CAE, Dirección CAE o Administrador de un Operador CAE.
/// </summary>
public record AlertaDePrincipal(bool LaVe, IReadOnlyList<OperacionEnAlertaDePrincipal> Operaciones)
{
    public static readonly AlertaDePrincipal Ninguna = new(false, []);

    public IEnumerable<OperacionEnAlertaDePrincipal> SinPrincipal => Operaciones.Where(o => o.SePuedeAsumir);

    public IEnumerable<OperacionEnAlertaDePrincipal> ConCoordinadorCaePrincipal => Operaciones.Where(o => !o.SePuedeAsumir);
}

/// <summary>
/// Alerta «sin principal» (ADR-011 § 2.7, enmienda 2026-10-08, punto 4): las Asignaciones de
/// Operación externas, vigentes, del Operador CAE de quien consulta que no tienen principal
/// vivo —distinguiendo las que no tienen a nadie de las que solo tienen carteras de apoyo— y,
/// como aviso informativo, aquellas cuyo principal es un Coordinador CAE.
///
/// <para>
/// Es una <b>consulta sobre el catálogo del Operador CAE</b>, no una notificación: se ve desde
/// cualquier Tenant activo, porque se resuelve con el Tenant de origen. La ven Coordinador CAE,
/// Dirección CAE y Administrador del Operador CAE, con el rol leído en Identity sobre el Tenant
/// de origen, nunca en el claim (<see cref="PerfilQueAsume"/>). Lo que se puede asumir lo vuelve
/// a decidir <see cref="AsumirPrincipalDeOperacionCommand"/>.
/// </para>
///
/// <para>
/// No concede nada: listar una operación no da acceso a su Tenant. Sin cartera vigente, alcance cero.
/// </para>
/// </summary>
public record ObtenerOperacionesSinPrincipalQuery : IRequest<AlertaDePrincipal>;

public class ObtenerOperacionesSinPrincipalQueryHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    ICatalogoIncorporacionCartera catalogo)
    : IRequestHandler<ObtenerOperacionesSinPrincipalQuery, AlertaDePrincipal>
{
    public async Task<AlertaDePrincipal> Handle(
        ObtenerOperacionesSinPrincipalQuery request, CancellationToken cancellationToken)
    {
        var actorId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var origen = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (actorId is null || origen is null)
            return AlertaDePrincipal.Ninguna;

        using (AmbitoTenantExplicito.Establecer(origen.Value))
        {
            // Falla cerrado sin rol de negocio en la sesión: Soporte TALVEG nunca es Operador CAE.
            if (await currentUserService.ObtenerRolEfectivoAsync() is null)
                return AlertaDePrincipal.Ninguna;

            if (!await PerfilQueAsume.LoTieneAsync(actorId.Value, origen.Value, directorioUsuarios, cancellationToken))
                return AlertaDePrincipal.Ninguna;

            var operaciones = await catalogo.ObtenerAsignablesAsync(origen.Value, cancellationToken);
            if (operaciones.Count == 0)
                return new AlertaDePrincipal(true, []);

            var carterasPorOperacion = (await catalogo.ObtenerCarterasVivasAsync(origen.Value, null, cancellationToken))
                .ToLookup(c => c.AsignacionOperacionId);

            var enAlerta = new List<(TenantCandidatoIncorporacion Operacion, SituacionDePrincipal Situacion, int Personas, CarteraVivaDeOperacion? Principal)>();
            foreach (var operacion in operaciones)
            {
                var carteras = carterasPorOperacion[operacion.AsignacionOperacionId].ToList();
                var principal = carteras.FirstOrDefault(c => c.EsPrincipal);

                if (principal is null)
                    enAlerta.Add((operacion,
                        carteras.Count == 0 ? SituacionDePrincipal.SinNadieAsignado : SituacionDePrincipal.ConPersonasSinPrincipal,
                        carteras.Count, null));
                else if (principal.Rol == EscaladoDePrincipalDeCartera.CoordinadorCae)
                    enAlerta.Add((operacion, SituacionDePrincipal.CoordinadorCaePrincipal, carteras.Count, principal));
            }

            var principales = enAlerta.Where(o => o.Principal is not null).Select(o => o.Principal!.UsuarioId).Distinct().ToList();
            IReadOnlyDictionary<Guid, string> nombres = principales.Count == 0
                ? new Dictionary<Guid, string>()
                : await directorioUsuarios.ObtenerNombresVisiblesAsync(principales, cancellationToken);

            return new AlertaDePrincipal(true, enAlerta
                .Select(o => new OperacionEnAlertaDePrincipal(
                    o.Operacion.AsignacionOperacionId, o.Operacion.PropietarioTenantId, o.Operacion.Nombre,
                    o.Situacion, o.Personas,
                    o.Principal is { } p ? nombres.GetValueOrDefault(p.UsuarioId) ?? string.Empty : null,
                    o.Principal?.UsuarioId == actorId))
                .OrderBy(o => o.Situacion)
                .ThenBy(o => o.NombreTenant)
                .ToList());
        }
    }
}
