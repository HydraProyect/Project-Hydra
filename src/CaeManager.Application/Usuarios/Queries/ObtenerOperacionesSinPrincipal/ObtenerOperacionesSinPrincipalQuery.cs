using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants.Encargo;
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
/// <paramref name="OutboundCerrado"/> dice si D3 deniega asumirla: Outbound cerrado para su Tenant
/// beneficiario o para el Tenant de origen del Operador CAE externo (R1, DECISION_LOG 2026-11-20,
/// punto 5), con la misma regla que el comando
/// (<see cref="EscaladoDePrincipalDeCartera.BeneficiariosDondeSePuedeAsumirAsync"/>).
/// </summary>
public record OperacionEnAlertaDePrincipal(
    Guid AsignacionOperacionId, Guid PropietarioTenantId, string NombreTenant,
    SituacionDePrincipal Situacion, int PersonasAsignadas, string? NombrePrincipal, bool EsDeQuienConsulta,
    bool OutboundCerrado)
{
    /// <summary>Si no tiene principal vivo.</summary>
    public bool EstaSinPrincipal => Situacion != SituacionDePrincipal.CoordinadorCaePrincipal;

    /// <summary>
    /// Solo se ofrece «Asumir» donde <see cref="AsumirPrincipalDeOperacionCommand"/> lo permitiría:
    /// sin principal vivo y sin Outbound cerrado (D3, R1). La fila se lista igual.
    /// </summary>
    public bool SePuedeAsumir => EstaSinPrincipal && !OutboundCerrado;
}

/// <summary>
/// La alerta: <paramref name="LaVe"/> es <c>false</c> —y la lista, vacía— para quien no es
/// Coordinador CAE, Dirección CAE o Administrador de un Operador CAE.
/// </summary>
public record AlertaDePrincipal(bool LaVe, IReadOnlyList<OperacionEnAlertaDePrincipal> Operaciones)
{
    public static readonly AlertaDePrincipal Ninguna = new(false, []);

    /// <summary>Las operaciones sin principal vivo, se puedan asumir o no.</summary>
    public IEnumerable<OperacionEnAlertaDePrincipal> SinPrincipal => Operaciones.Where(o => o.EstaSinPrincipal);

    /// <summary>De <see cref="SinPrincipal"/>, las que se pueden asumir: las que cuenta el aviso de la campana.</summary>
    public IEnumerable<OperacionEnAlertaDePrincipal> Asumibles => Operaciones.Where(o => o.SePuedeAsumir);

    public IEnumerable<OperacionEnAlertaDePrincipal> ConCoordinadorCaePrincipal => Operaciones.Where(o => !o.EstaSinPrincipal);
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
/// Dirección CAE y Administrador del Operador CAE: decide el rol efectivo de la sesión en el
/// Tenant de origen e Identity lo confirma (<see cref="PerfilQueAsume"/>). Lo que se puede asumir lo vuelve
/// a decidir <see cref="AsumirPrincipalDeOperacionCommand"/>.
/// </para>
///
/// <para>
/// D3, R1 (DECISION_LOG 2026-11-20, punto 5): una operación sin principal con Outbound cerrado para
/// su Tenant beneficiario o para el Tenant de origen del Operador CAE externo se lista igual, pero
/// sin «Asumir» (<see cref="OperacionEnAlertaDePrincipal.SePuedeAsumir"/>): la visibilidad de la
/// acción sigue la autorización real del comando. El Tenant de origen se pregunta una vez y cada
/// Tenant beneficiario distinto otra, solo si hay operaciones sin principal.
/// </para>
///
/// <para>
/// No concede nada: listar una operación no da acceso a su Tenant. Sin cartera vigente, alcance cero.
/// </para>
/// </summary>
[AreaDelEncargo("Usuarios")]
public record ObtenerOperacionesSinPrincipalQuery : IRequest<AlertaDePrincipal>;

public class ObtenerOperacionesSinPrincipalQueryHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    ICatalogoIncorporacionCartera catalogo,
    IOutboundCerradoTenantTec outboundCerrado)
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
            // Rol efectivo de la sesión en origen, confirmado por Identity; sin rol de negocio
            // falla cerrado: Soporte TALVEG nunca es Operador CAE.
            if (!await PerfilQueAsume.LoTieneAsync(
                    actorId.Value, origen.Value, currentUserService, directorioUsuarios, cancellationToken))
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

            // D3, R1: la misma regla que el comando, solo para las operaciones sin principal.
            var abiertos = await EscaladoDePrincipalDeCartera.BeneficiariosDondeSePuedeAsumirAsync(
                outboundCerrado,
                enAlerta.Where(o => o.Situacion != SituacionDePrincipal.CoordinadorCaePrincipal)
                    .Select(o => o.Operacion.PropietarioTenantId),
                origen.Value, cancellationToken);

            return new AlertaDePrincipal(true, enAlerta
                .Select(o => new OperacionEnAlertaDePrincipal(
                    o.Operacion.AsignacionOperacionId, o.Operacion.PropietarioTenantId, o.Operacion.Nombre,
                    o.Situacion, o.Personas,
                    o.Principal is { } p ? nombres.GetValueOrDefault(p.UsuarioId) ?? string.Empty : null,
                    o.Principal?.UsuarioId == actorId,
                    OutboundCerrado: o.Situacion != SituacionDePrincipal.CoordinadorCaePrincipal
                                     && !abiertos.Contains(o.Operacion.PropietarioTenantId)))
                .OrderBy(o => o.Situacion)
                .ThenBy(o => o.NombreTenant)
                .ToList());
        }
    }
}
