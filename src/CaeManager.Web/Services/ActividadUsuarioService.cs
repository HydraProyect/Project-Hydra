using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;

namespace CaeManager.Web.Services;

/// <summary>
/// Modelo de "visto" del resumen de ausencia (Project-Hydra-Negocio/tecnico/docs/blueprints/OPERATIONAL-HOME.md § 6,
/// DDL-068): registra la última interacción autenticada del usuario en cualquier pantalla
/// (no solo el Home) y resuelve si vuelve tras una ausencia real.
///
/// Se resuelve una única vez por circuito y se cachea (mismo patrón que
/// <see cref="TrazaSoporteService"/>): "ausente" solo tiene sentido la primera vez que se
/// evalúa tras reconectar, no en cada navegación dentro de la misma sesión ya abierta.
/// </summary>
public class ActividadUsuarioService(
    ICurrentUserService currentUserService,
    IPreferenciasCuentaPropia preferencias,
    ILogger<ActividadUsuarioService> logger)
{
    private static readonly TimeSpan UmbralAusencia = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ThrottleEscritura = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Se cachea la TAREA de la resolución, no un booleano de «ya resuelto».
    /// Con la bandera, la segunda llamada que entraba mientras la primera
    /// seguía esperando a la base se encontraba la bandera puesta y devolvía
    /// los campos todavía vacíos —(false, null)—, concluyendo que no hubo
    /// ausencia. Y la carrera no es rara: MainLayout e Inicio comparten este
    /// servicio con ámbito de circuito y los dos lo invocan en la misma carga.
    /// Una tarea cancelada o fallida no se conserva: la siguiente llamada
    /// vuelve a intentarlo.
    /// </summary>
    private Task<(bool Ausente, DateTime? DesdeParaResumen)>? _resolucion;

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
    /// <remarks>
    /// <c>virtual</c> para poder sustituirlo en los tests de componente de
    /// Inicio: la implementación real necesita una cuenta que escribir, y montar
    /// ese aparato para decidir si el resumen «qué llegó sin ver» aparece haría que
    /// el andamio tapara lo que el caso mide.
    /// </remarks>
    public virtual Task<(bool Ausente, DateTime? DesdeParaResumen)> RegistrarYEvaluarAsync(bool interactivo, CancellationToken cancellationToken = default)
    {
        // Una resolución ya hecha —o en vuelo— vale para cualquier llamada,
        // incluida la del prerenderizado: el orden de estas dos guardas es el
        // de siempre.
        if (_resolucion is { IsCanceled: false, IsFaulted: false }) return _resolucion;
        if (!interactivo) return Task.FromResult<(bool, DateTime?)>((false, null));

        return _resolucion = ResolverAsync(cancellationToken);
    }

    /// <summary>
    /// Un conflicto que sobrevive al reintento del puerto no invalida la ausencia ya
    /// leída: solo se queda sin registrar esta actividad, y otra escritura acaba de tocar
    /// la cuenta. Se deja constancia en vez de descartarlo; cualquier otro fallo de la
    /// escritura sigue subiendo hasta quien llama.
    /// </summary>
    private async Task<(bool Ausente, DateTime? DesdeParaResumen)> ResolverAsync(CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return (false, null);

        var cuenta = await preferencias.ObtenerAsync(usuarioId.Value, cancellationToken);
        if (cuenta is null) return (false, null);

        var anterior = cuenta.UltimaActividadUtc;
        var ahora = DateTime.UtcNow;
        var (ausente, debeEscribir) = Evaluar(anterior, ahora);

        if (debeEscribir)
        {
            var escritura = await preferencias.RegistrarActividadAsync(usuarioId.Value, ahora, cancellationToken);
            if (escritura != ResultadoEscrituraCuenta.Guardada)
            {
                logger.LogWarning(
                    "No se pudo registrar la actividad de {UsuarioId}: {Resultado}", usuarioId, escritura);
            }
        }

        return (ausente, anterior);
    }

    /// <summary>
    /// Extraído como método puro y estático (mismo patrón que
    /// <c>ObtenerBandejaGestorQueryHandler.Fusionar</c>) para poder probar el umbral de
    /// ausencia y el throttle de escritura sin tocar la base.
    /// </summary>
    public static (bool Ausente, bool DebeEscribir) Evaluar(DateTime? anterior, DateTime ahora)
    {
        // Sin actividad previa no hay "ausencia" que resumir — es la primera vez que este
        // usuario interactúa con la plataforma (Project-Hydra-Negocio/tecnico/docs/blueprints/OPERATIONAL-HOME.md § 6).
        var ausente = anterior is not null && ahora - anterior.Value > UmbralAusencia;
        var debeEscribir = anterior is null || ahora - anterior.Value > ThrottleEscritura;
        return (ausente, debeEscribir);
    }
}
