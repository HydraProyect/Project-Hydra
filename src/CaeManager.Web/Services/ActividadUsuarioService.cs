using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Services;

/// <summary>
/// Modelo de "visto" del resumen de ausencia (docs/blueprints/OPERATIONAL-HOME.md § 6,
/// DDL-068): registra la última interacción autenticada del usuario en cualquier pantalla
/// (no solo el Home) y resuelve si vuelve tras una ausencia real.
///
/// Se resuelve una única vez por circuito y se cachea (mismo patrón que
/// <see cref="TrazaSoporteService"/>): "ausente" solo tiene sentido la primera vez que se
/// evalúa tras reconectar, no en cada navegación dentro de la misma sesión ya abierta.
/// </summary>
public class ActividadUsuarioService(
    ICurrentUserService currentUserService,
    UserManager<ApplicationUser> userManager,
    PuertaAccesoDatos puertaAccesoDatos)
{
    private static readonly TimeSpan UmbralAusencia = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ThrottleEscritura = TimeSpan.FromMinutes(1);

    private bool _resuelto;
    private bool _ausente;
    private DateTime? _ultimaActividadAnteriorUtc;

    /// <summary>
    /// Registra la actividad de ahora (con throttle de un minuto) y devuelve si el usuario
    /// estaba ausente y, si lo estaba, el valor de <see cref="ApplicationUser.UltimaActividadUtc"/>
    /// justo antes de esta interacción — el punto de corte para "qué llegó sin ver".
    /// </summary>
    /// <param name="interactivo">
    /// <c>ComponentBase.RendererInfo.IsInteractive</c> del componente que llama. El
    /// prerenderizado de <c>@rendermode InteractiveServer</c> ejecuta el árbol de componentes una
    /// vez antes de que exista el circuito interactivo real, en un DI scope distinto y
    /// desechable — sin este filtro el prerender consume la ausencia y escribe "ahora" antes de
    /// que el circuito real llegue a leerla, y el resumen nunca aparece. <c>IHttpContextAccessor</c>
    /// no sirve para distinguirlo aquí: en este hosting model no está disponible en ninguna de
    /// las dos pasadas.
    /// </param>
    public async Task<(bool Ausente, DateTime? DesdeParaResumen)> RegistrarYEvaluarAsync(bool interactivo, CancellationToken cancellationToken = default)
    {
        if (_resuelto) return (_ausente, _ultimaActividadAnteriorUtc);
        if (!interactivo) return (false, null);

        _resuelto = true;

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return (false, null);

        await puertaAccesoDatos.EjecutarAsync(async () =>
        {
            var usuario = await userManager.FindByIdAsync(usuarioId.Value.ToString());
            if (usuario is null) return;

            var anterior = usuario.UltimaActividadUtc;
            var ahora = DateTime.UtcNow;
            var (ausente, debeEscribir) = Evaluar(anterior, ahora);

            _ultimaActividadAnteriorUtc = anterior;
            _ausente = ausente;

            if (debeEscribir)
            {
                usuario.UltimaActividadUtc = ahora;
                await userManager.UpdateAsync(usuario);
            }
        }, cancellationToken);

        return (_ausente, _ultimaActividadAnteriorUtc);
    }

    /// <summary>
    /// Extraído como método puro y estático (mismo patrón que
    /// <c>ObtenerBandejaGestorQueryHandler.Fusionar</c>) para poder probar el umbral de
    /// ausencia y el throttle de escritura sin tener que construir un
    /// <see cref="UserManager{TUser}"/> real.
    /// </summary>
    public static (bool Ausente, bool DebeEscribir) Evaluar(DateTime? anterior, DateTime ahora)
    {
        // Sin actividad previa no hay "ausencia" que resumir — es la primera vez que este
        // usuario interactúa con la plataforma (docs/blueprints/OPERATIONAL-HOME.md § 6).
        var ausente = anterior is not null && ahora - anterior.Value > UmbralAusencia;
        var debeEscribir = anterior is null || ahora - anterior.Value > ThrottleEscritura;
        return (ausente, debeEscribir);
    }
}
