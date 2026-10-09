using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Operaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Operaciones.ApoyoCartera;

/// <summary>
/// El aviso de que un apoyo de cartera empezó o terminó (ADR-011 § 2.7, enmienda 2026-10-08,
/// punto 5: el control del Coordinador CAE sobre quién entra en un Tenant propietario pasa de
/// previo a <b>posterior</b>, y este aviso es ese control). Una sola definición de a quién se
/// avisa, para la aceptación y para las tres formas de terminar.
///
/// <para>
/// <b>A quién</b>: al Coordinador CAE de quien propuso el apoyo y al del Gestor CAE de apoyo
/// (<c>CoordinadorUsuarioId</c>, leído de las cuentas en el momento del aviso), si hoy son
/// cuentas activas con ese rol en el Operador CAE. Quien propuso, si él mismo es un Coordinador
/// CAE —el Coordinador CAE principal también da acceso—, cuenta como su propio Coordinador CAE.
/// Si a alguno de los dos le falta, se avisa además a <b>toda la Dirección CAE</b> activa del
/// Operador CAE. Nunca a quien hizo la acción: ya lo sabe.
/// </para>
///
/// <para>
/// Es una <see cref="NotificacionUsuario"/> sellada con el Tenant de origen del Operador CAE,
/// igual que las de incorporación a cartera: se escribe dentro del ámbito de ese Tenant
/// (<see cref="ContextoOperadorCae.EnOrigen"/>) y <b>solo se ve en la campana con ese Tenant
/// activo</b>. <b>Nunca hace fallar el comando</b>: el apoyo ya está concedido o retirado
/// cuando se llama, y un aviso que no sale se registra y se descarta.
/// </para>
/// </summary>
public static class AvisoDeApoyoDeCartera
{
    private const string CoordinadorCae = "CoordinadorCae";
    private const string DireccionCae = "DireccionCae";

    /// <summary>
    /// Los destinatarios del aviso sobre el apoyo entre <paramref name="proponenteUsuarioId"/>
    /// y <paramref name="apoyoUsuarioId"/>, sin <paramref name="actorUsuarioId"/>. Se llama con
    /// el Tenant de origen del Operador CAE como Tenant activo.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> ResolverDestinatariosAsync(
        Guid operadorTenantId, Guid proponenteUsuarioId, Guid apoyoUsuarioId, Guid actorUsuarioId,
        IDirectorioDestinosCartera directorioDestinos, IDirectorioUsuariosService directorioUsuarios,
        CancellationToken cancellationToken)
    {
        var destinatarios = new HashSet<Guid>();
        var faltaAlguno = false;

        foreach (var persona in new[] { proponenteUsuarioId, apoyoUsuarioId })
        {
            if (await CoordinadorDeAsync(persona) is { } coordinadorId)
                destinatarios.Add(coordinadorId);
            else
                faltaAlguno = true;
        }

        if (faltaAlguno)
            destinatarios.UnionWith(
                await directorioUsuarios.ObtenerCuentasActivasConRolAsync(operadorTenantId, DireccionCae, cancellationToken));

        destinatarios.Remove(actorUsuarioId);
        return destinatarios.OrderBy(id => id).ToList();

        async Task<Guid?> CoordinadorDeAsync(Guid usuarioId)
        {
            var cuenta = await directorioDestinos.ObtenerAsync(usuarioId, cancellationToken);
            if (cuenta is null || cuenta.EsOperadorDelegado)
                return null;

            // Quien es Coordinador CAE no tiene otro por encima a quien avisar: es él.
            var candidato = cuenta.RolEfectivo == CoordinadorCae ? (Guid?)usuarioId : cuenta.CoordinadorUsuarioId;
            return candidato is { } id
                   && await directorioUsuarios.EsCuentaActivaConRolAsync(id, operadorTenantId, CoordinadorCae, cancellationToken)
                ? id
                : null;
        }
    }

    /// <summary>
    /// Avisa de que el destinatario aceptó la propuesta y ya tiene el Tenant propietario en su
    /// cartera, como apoyo. Se llama con la transacción de la aceptación ya confirmada.
    /// </summary>
    public static Task AvisarAceptadoAsync(
        PropuestaApoyoCartera propuesta, Guid actorUsuarioId, Dependencias d, CancellationToken cancellationToken) =>
        AvisarAsync(propuesta, actorUsuarioId, terminado: false, d, cancellationToken);

    /// <summary>
    /// Avisa de que el apoyo terminó por retirada. Si no lo terminó el propio Gestor CAE de
    /// apoyo, también se le avisa a él: perdió un Tenant sin haberlo pedido.
    /// </summary>
    public static Task AvisarTerminadoAsync(
        PropuestaApoyoCartera propuesta, Guid actorUsuarioId, Dependencias d, CancellationToken cancellationToken) =>
        AvisarAsync(propuesta, actorUsuarioId, terminado: true, d, cancellationToken);

    /// <summary>Lo que el aviso necesita del Command que lo envía.</summary>
    public sealed record Dependencias(
        IDirectorioDestinosCartera DirectorioDestinos,
        IDirectorioUsuariosService DirectorioUsuarios,
        ITenantsQueryContext Tenants,
        INotificacionUsuarioRepository Notificaciones,
        IUnitOfWork UnitOfWork,
        ICatalogoIncorporacionCartera Catalogo,
        ILogger Logger);

    private static async Task AvisarAsync(
        PropuestaApoyoCartera propuesta, Guid actorUsuarioId, bool terminado, Dependencias d,
        CancellationToken cancellationToken)
    {
        try
        {
            using (AmbitoTenantExplicito.Establecer(propuesta.OperadorTenantId))
            {
                var destinatarios = await ResolverDestinatariosAsync(
                    propuesta.OperadorTenantId, propuesta.ProponenteUsuarioId, propuesta.DestinatarioUsuarioId,
                    actorUsuarioId, d.DirectorioDestinos, d.DirectorioUsuarios, cancellationToken);

                var empresa = await d.Tenants.Tenants
                    .Where(t => t.Id == propuesta.PropietarioTenantId)
                    .Select(t => t.Nombre)
                    .FirstOrDefaultAsync(cancellationToken) ?? "la empresa";
                var nombres = await d.DirectorioUsuarios.ObtenerNombresVisiblesAsync(
                    [propuesta.ProponenteUsuarioId, propuesta.DestinatarioUsuarioId], cancellationToken);
                var apoyo = nombres.GetValueOrDefault(propuesta.DestinatarioUsuarioId) ?? "Un Gestor CAE";
                var proponente = nombres.GetValueOrDefault(propuesta.ProponenteUsuarioId) ?? "el principal";

                var (titulo, mensaje) = terminado
                    ? ("Acceso de apoyo terminado", $"«{apoyo}» ya no tiene acceso de apoyo a «{empresa}».")
                    : ("Nuevo acceso de apoyo", MensajeDeAceptado(apoyo, empresa, proponente, propuesta.VigenciaHastaPropuesta));

                foreach (var destinatarioId in destinatarios.Where(id => id != propuesta.DestinatarioUsuarioId))
                    d.Notificaciones.Agregar(new NotificacionUsuario(
                        destinatarioId, titulo, mensaje, RutasIncorporacionCartera.Bandeja, "Ver accesos de apoyo"));

                if (terminado && actorUsuarioId != propuesta.DestinatarioUsuarioId)
                    d.Notificaciones.Agregar(new NotificacionUsuario(
                        propuesta.DestinatarioUsuarioId,
                        "Acceso de apoyo retirado",
                        $"Te han retirado el acceso de apoyo a «{empresa}».",
                        RutasIncorporacionCartera.Bandeja,
                        "Ver mis accesos de apoyo"));

                await d.UnitOfWork.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            d.Catalogo.DescartarPendientes();
            d.Logger.LogWarning(ex,
                "El apoyo de cartera de la propuesta {PropuestaId} cambió ({Cambio}), pero no se pudo avisar.",
                propuesta.Id, terminado ? "terminado" : "aceptado");
        }
    }

    private static string MensajeDeAceptado(string apoyo, string empresa, string proponente, DateTime? vigenciaHasta) =>
        vigenciaHasta is { } hasta
            ? $"«{apoyo}» tiene acceso de apoyo a «{empresa}» hasta el {VigenciaDeApoyo.UltimoDia(hasta):dd/MM/yyyy}, a propuesta de «{proponente}»."
            : $"«{apoyo}» tiene acceso de apoyo a «{empresa}», a propuesta de «{proponente}».";
}
