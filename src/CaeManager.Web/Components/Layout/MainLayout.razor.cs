using CaeManager.Application.Common;
using System.Security.Claims;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Components.Layout;

public partial class MainLayout
{
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;
    [Inject] private ActividadUsuarioService ActividadUsuario { get; set; } = default!;
    [Inject] private EstadoDelCircuito EstadoDelCircuito { get; set; } = default!;
    [Inject] private ILogger<MainLayout> Logger { get; set; } = default!;

    /// <summary>
    /// Medido, no asumido: <c>ActividadUsuarioService.RegistrarYEvaluarAsync</c>
    /// documenta que <c>IHttpContextAccessor</c> no sirve para distinguir la
    /// pasada de prerenderizado de la del circuito real interactivo — pero esa
    /// nota es sobre <c>Inicio.razor</c>, que sí llega a ejecutarse en las dos.
    /// MainLayout no: el <c>remarks</c> de <see cref="EstadoDelCircuito"/>
    /// documenta, con un E2E real, que <c>OnParametersSetAsync</c> de
    /// MainLayout se ejecuta siempre en SSR (<c>RendererInfo.IsInteractive</c>
    /// nunca fue <c>true</c> en la medición), y en esa pasada el
    /// <c>HttpContext</c> de la petición sí está disponible y con
    /// <c>RequestAborted</c> operativo — confirmado con el mismo E2E.
    /// </summary>
    [Inject] private IHttpContextAccessor HttpContextAccessor { get; set; } = default!;

    /// <summary>
    /// Adonde va el usuario cuando el guard de abajo no se pudo evaluar y el
    /// circuito sigue vivo. <c>/Error</c> ya existe, es <c>[AllowAnonymous]</c>
    /// y usa <c>AuthLayout</c>, no este layout: no vuelve a pasar por este
    /// guard (sin bucle de redirección) y no toca la base de datos, que es
    /// justo lo que puede estar fallando (ver el comentario de cabecera de
    /// Error.razor, Sentry DOTNET-8). Con <c>forceLoad</c> porque lo que hay
    /// que retirar es el contenido ya pintado: una navegación interna
    /// conservaría el documento, y el contenido protegido seguiría a la vista.
    /// </summary>
    private const string RutaErrorDelSistema = "/Error";

    /// <summary>
    /// Antes de esta redirección, <c>Error.razor</c> solo se alcanzaba a
    /// través de <c>UseExceptionHandler</c>, que le deja
    /// <c>IExceptionHandlerPathFeature</c> para la referencia técnica y la
    /// ruta de "Reintentar". Este camino no pasa por ahí — a propósito, para
    /// no volver a tocar la base de datos que puede estar fallando — así que
    /// sin esto la página de error se quedaría sin ninguna de las dos: sería
    /// un hueco de diagnóstico nuevo, introducido por esta misma redirección.
    /// Una referencia de correlación (sin datos personales) y la ruta a la
    /// que el usuario ya estaba navegando (la misma que ya veía en la barra
    /// de direcciones) no exponen nada que el usuario no tuviera ya delante.
    /// </summary>
    private string ConDiagnostico(Guid referencia) =>
        Navigation.GetUriWithQueryParameters(RutaErrorDelSistema, new Dictionary<string, object?>
        {
            ["ref"] = referencia.ToString(),
            ["ruta"] = Navigation.ToBaseRelativePath(Navigation.Uri) is { Length: > 0 } relativa ? $"/{relativa}" : "/",
        });

    /// <summary>
    /// Forzar el cambio de contraseña en el primer login (ver
    /// ApplicationUser.DebeCambiarContrasena) no sirve de nada si basta con
    /// escribir otra URL a mano para saltárselo — por eso el guard no vive
    /// en Login.razor (que solo actúa justo después de autenticar) sino
    /// aquí, en MainLayout, el layout por defecto de cualquier página
    /// autenticada (ver Routes.razor). OnParametersSetAsync, no
    /// OnInitializedAsync, porque @Body es un parámetro en cascada de
    /// LayoutComponentBase que cambia en cada navegación — así el guard se
    /// revisa de nuevo en cada página, no solo la primera vez que se monta
    /// el Layout. Las únicas pantallas que nunca pasan por aquí son
    /// CambiarContrasena.razor y ConfigurarAutenticadorDosFactores.razor,
    /// porque usan AuthLayout en vez de este Layout — no hace falta
    /// comprobar la ruta actual a mano, ni hay bucle de redirección con el
    /// guard de 2FA de más abajo.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        try
        {
            // AuthenticationStateProvider entra en el try junto con el resto:
            // en Blazor Server también puede tocar estado atado al circuito, y
            // dejarlo fuera reabriría el mismo fallo abierto por una puerta
            // distinta — el guard de abajo nunca llegaría a decidir nada.
            var estadoAutenticacion = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            if (estadoAutenticacion.User.Identity?.IsAuthenticated != true) return;

            var idClaim = estadoAutenticacion.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(idClaim, out var id)) return;

            // Se resuelve una única vez por circuito (ActividadUsuarioService) — el resultado
            // no se usa aquí, solo se dispara para que ya esté cacheado cuando el Home lo pida.
            // Dentro del try aunque no forme parte del guard: también toca la base por la
            // puerta, así que sufre la misma carrera, y si se fuera por excepción el guard
            // de abajo no llegaría a correr — el mismo fallo abierto por otra vía.
            _ = await ActividadUsuario.RegistrarYEvaluarAsync(RendererInfo.IsInteractive);

            // Por la puerta: este guard corre en paralelo con la inicialización
            // de los demás componentes del layout y de la página, todos sobre el
            // mismo DbContext scoped (ver PuertaAccesoDatos).
            await PuertaAccesoDatos.EjecutarAsync(async () =>
            {
                var usuario = await UserManager.FindByIdAsync(id.ToString());
                if (usuario is null) return;

                if (usuario.DebeCambiarContrasena)
                {
                    Navigation.NavigateTo("/cuenta/cambiar-contrasena", forceLoad: true);
                    return;
                }

                // Mismo motivo que el guard de arriba: sin esto, un usuario
                // auto-provisionado por SSO sin rol asignado (ver
                // IdentityEndpointsExtensions) podría saltarse la sala de espera
                // escribiendo cualquier otra URL a mano.
                var roles = await UserManager.GetRolesAsync(usuario);
                if (roles.Count == 0)
                {
                    Navigation.NavigateTo("/cuenta/pendiente-de-rol", forceLoad: true);
                    return;
                }

                // 2FA obligatoria para Administrador (P1-13 de
                // docs/business/MATURITY_REVIEW.md): es el rol con más alcance
                // del sistema, y hoy activar la autenticación en dos pasos era
                // opt-in — nadie la exigía. ConfigurarAutenticadorDosFactores.razor
                // usa AuthLayout, no este Layout, así que no vuelve a pasar por
                // aquí y no hay bucle de redirección.
                if (roles.Contains(Roles.Administrador) && !await UserManager.GetTwoFactorEnabledAsync(usuario))
                    Navigation.NavigateTo("/cuenta/configurar-2fa", forceLoad: true);
            });
        }
        // Todo lo que no sea la redirección legítima del propio guard. Este
        // proyecto fija BlazorDisableThrowNavigationException=true
        // (CaeManager.Web.csproj), así que hoy NavigateTo no lanza
        // NavigationException aquí, ni en SSR estático ni en interactivo
        // (confirmado: es justo lo que esa propiedad de MSBuild existe para
        // evitar, "What's new in ASP.NET Core .NET 10"). La exclusión es
        // defensa en profundidad, no una ruta que se ejerza en producción hoy:
        // si esa opción cambiara, o algún otro camino del framework volviera a
        // lanzarla, atraparla aquí se tragaría precisamente el "cambia la
        // contraseña" o el "configura la 2FA" que el guard acaba de decidir —
        // el mismo fallo abierto que este catch existe para cerrar,
        // reintroducido por su propio arreglo.
        //
        // OperationCanceledException NO se excluye por TIPO, aunque parezca
        // tentador: una versión anterior de este incremento la excluía del
        // catch de forma general pensando solo en la petición abortada, y
        // Codex encontró el hueco (hallazgo P1, revisión sobre este mismo
        // incremento): una dependencia del guard puede lanzarla igual con el
        // circuito perfectamente vivo (p. ej. un timeout de UserManager/EF
        // Core), y ese camino saltaba el catch entero sin redirigir ni
        // registrar nada — el propio renderer de Blazor trata la tarea de
        // ciclo de vida cancelada como benigna. El criterio correcto para
        // distinguir "el otro lado se fue" de "una dependencia canceló
        // internamente con el cliente todavía ahí" no es el tipo de la
        // excepción: es HttpContext.RequestAborted, comprobado más abajo,
        // dentro del catch — solo posible porque MainLayout se ejecuta
        // siempre en SSR (ver el remarks de EstadoDelCircuito, medido con un
        // E2E real), así que HttpContext siempre está disponible aquí.
        //
        // Por tipo no se filtra nada más: el tipo no distingue un circuito
        // muerto de uno vivo (ver abajo), así que como criterio no vale, ni
        // estrecho ni ancho.
        catch (Exception ex) when (ex is not NavigationException)
        {
            // Petición realmente abortada (el cliente cerró la conexión antes
            // de que esto terminara — navegó a otro sitio, cerró la pestaña):
            // nadie va a ver una redirección a /Error, así que forzarla no
            // protege a nadie y el LogError sería puro ruido de monitorización
            // sobre tráfico normal (el motivo original por el que se revisó
            // este catch). Solo se trata así OperationCanceledException — el
            // resto de tipos con la petición abortada siguen siendo un fallo
            // real del guard, no una cancelación, y se redirigen igual.
            if (ex is OperationCanceledException && HttpContextAccessor.HttpContext is { RequestAborted.IsCancellationRequested: true })
            {
                Logger.LogInformation(ex, "El guard de seguridad de MainLayout se canceló porque la petición se abortó (cliente desconectado); no hay nadie a quien redirigir");
                return;
            }

            // Quién decide qué se hace aquí es el ESTADO DEL CIRCUITO, no el
            // tipo de la excepción. Hasta 2026-09-18 el catch aceptaba
            // ObjectDisposedException y ArgumentOutOfRangeException (PR #517) y
            // terminaba sin hacer nada, dando por supuesto que venían de la
            // carrera de desconexión descrita más abajo. Pero esos dos tipos
            // también los produce un fallo con el circuito perfectamente vivo,
            // y entonces el método terminaba sin aplicar el guard y la página
            // se mostraba igual: fallar abierto en el guard de seguridad del
            // layout (cambio de contraseña forzoso, rol pendiente, 2FA
            // obligatoria para Administrador).
            if (!EstadoDelCircuito.Cerrado)
            {
                // Circuito vivo: hay alguien al otro lado, y del guard no
                // sabemos el resultado — no si el usuario puede pasar. Lo
                // único seguro es retirar el contenido con una navegación
                // completa (ver RutaErrorDelSistema). Y se registra como error,
                // porque lo que ocultaba este fallo era justamente que la
                // excepción desaparecía en silencio.
                var referencia = Guid.NewGuid();
                Logger.LogError(ex, "El guard de seguridad de MainLayout no se pudo evaluar con el circuito vivo; se retira el contenido y se redirige a {Ruta} (referencia {Referencia}): {TipoExcepcion} — {Mensaje}",
                    RutaErrorDelSistema, referencia, ex.GetType().Name, ex.Message);
                Navigation.NavigateTo(ConDiagnostico(referencia), forceLoad: true);
                return;
            }

            // Circuito ya cerrado. El circuito de Blazor puede desconectarse (y
            // con él el CaeManagerDbContext scoped que UserManager usa por
            // debajo) mientras este guard todavía está en vuelo — reproducido
            // en producción dos veces, en el mismo sitio (Sentry DOTNET-6:
            // ObjectDisposedException sobre CaeManagerDbContext; DOTNET-3:
            // ArgumentOutOfRangeException dentro de NpgsqlDataReader — misma
            // carrera, forma distinta según en qué punto exacto del socket la
            // sorprenda la desconexión). No es un PuertaAccesoDatos.EjecutarAsync
            // — la puerta no se dispone (ver su comentario); esto es el paso
            // anterior: la propia conexión/DbContext muere DENTRO de la
            // operación envuelta. LiberacionDeAccesoADatosAlCerrarCircuito hace
            // que el cierre del circuito espere a la operación en vuelo antes de
            // disponer el scope del circuito (no el de la pasada SSR, que no
            // tiene ese punto de cierre); esta rama sigue cubriendo lo que no
            // pase por ahí y lo que supere el tope de espera.
            // Aquí sí es cierto que no queda nadie al otro lado
            // esperando una redirección, así que no hay nada que hacer salvo no
            // dejar la excepción sin observar.
            //
            // Sigue sin usarse ExcepcionDeCircuitoDesconectado.Es (los otros
            // cuatro sitios de layout sí, REC-166), pero ya no por el motivo de
            // entonces —"atrapar de más aquí es fallar abierto"—, que este
            // cambio deja obsoleto: con el estado del circuito decidiendo, un
            // tipo de más no abre nada, porque el camino de circuito vivo
            // redirige igual. Es que, con ese criterio, el predicado por tipo y
            // por mensaje ya no distingue nada que importe.
            Logger.LogWarning(ex, "MainLayout descartó una excepción con el circuito ya cerrado: {TipoExcepcion} — {Mensaje}",
                ex.GetType().Name, ex.Message);
        }
    }
}
