using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Notificaciones;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Usuarios.Commands.CambiarActivacionUsuario;

/// <summary>
/// El aviso de que, al desactivar a un Gestor CAE sin elegir a quién reasignar su cartera, uno o
/// más Tenants beneficiarios se quedaron sin Gestor CAE (decisión de Chris del 2026-11-14, «Baja de
/// un Usuario frente a baja del Tenant»). Es un puerto para que los tests del Command no dependan
/// de la persistencia de las notificaciones.
/// </summary>
public interface IAvisoDeCarteraSinGestorCae
{
    /// <summary>
    /// Avisa a los Administradores activos de <paramref name="operadorTenantId"/>. Se llama con la
    /// transacción de la desactivación ya confirmada y <b>nunca hace fallar el comando</b>: la cuenta
    /// ya está desactivada y su cartera retirada; un aviso que no sale se registra y se descarta.
    /// </summary>
    Task AvisarAsync(
        Guid operadorTenantId, string nombreDesactivado, IReadOnlyList<TenantEnCarteraDeGestor> sinGestorCae,
        CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="NotificacionUsuario"/> en la campana, sellada con el Tenant del Operador CAE (el
/// Tenant propietario de la cuenta desactivada), igual que los avisos de apoyo de cartera: se
/// escribe con ese Tenant como <see cref="AmbitoTenantExplicito"/> y solo se ve con él activo.
/// Nunca correo.
///
/// <para>
/// <b>A quién</b>: a todos los Administradores activos de ese Tenant, también a quien desactivó si
/// lo es: el aviso es la tarea pendiente de dar un Gestor CAE nuevo a esas empresas, no la noticia
/// de la desactivación, y con un único Administrador no habría nadie más a quien dejársela.
/// </para>
/// </summary>
public sealed class AvisoDeCarteraSinGestorCae(
    IDirectorioUsuariosService directorioUsuarios,
    INotificacionUsuarioRepository notificaciones,
    IUnitOfWork unitOfWork,
    ICatalogoIncorporacionCartera catalogo,
    ILogger<AvisoDeCarteraSinGestorCae> logger) : IAvisoDeCarteraSinGestorCae
{
    private const string Administrador = "Administrador";

    public const string Titulo = "Empresas sin Gestor CAE";

    public const string RutaUsuarios = "/usuarios";

    public async Task AvisarAsync(
        Guid operadorTenantId, string nombreDesactivado, IReadOnlyList<TenantEnCarteraDeGestor> sinGestorCae,
        CancellationToken cancellationToken)
    {
        if (sinGestorCae.Count == 0)
            return;

        try
        {
            using (AmbitoTenantExplicito.Establecer(operadorTenantId))
            {
                var administradores = await directorioUsuarios.ObtenerCuentasActivasConRolAsync(
                    operadorTenantId, Administrador, cancellationToken);
                if (administradores.Count == 0)
                {
                    logger.LogWarning(
                        "{Cuantas} empresas quedaron sin Gestor CAE en el Tenant {TenantId} y no hay ningún Administrador activo a quien avisar.",
                        sinGestorCae.Count, operadorTenantId);
                    return;
                }

                var mensaje = Mensaje(nombreDesactivado, sinGestorCae);
                foreach (var administradorId in administradores)
                    notificaciones.Agregar(new NotificacionUsuario(
                        administradorId, Titulo, mensaje, RutaUsuarios, "Asignar un Gestor CAE"));

                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            catalogo.DescartarPendientes();
            logger.LogWarning(ex,
                "Se desactivó a un Gestor CAE y {Cuantas} empresas quedaron sin Gestor CAE en el Tenant {TenantId}, pero no se pudo avisar.",
                sinGestorCae.Count, operadorTenantId);
        }
    }

    public static string Mensaje(string nombreDesactivado, IReadOnlyList<TenantEnCarteraDeGestor> sinGestorCae)
    {
        var empresas = string.Join(", ", sinGestorCae.Select(t => $"«{t.Nombre}»"));
        return sinGestorCae.Count == 1
            ? $"Se desactivó a «{nombreDesactivado}» sin reasignar su cartera: {empresas} se ha quedado sin Gestor CAE. Asígnale uno."
            : $"Se desactivó a «{nombreDesactivado}» sin reasignar su cartera: {empresas} se han quedado sin Gestor CAE. Asígnales uno.";
    }
}
