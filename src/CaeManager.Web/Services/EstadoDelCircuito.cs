using Microsoft.AspNetCore.Components.Server.Circuits;

namespace CaeManager.Web.Services;

/// <summary>
/// Criterio <b>observable</b> de "el circuito de Blazor ya se cerró", para
/// distinguirlo del tipo de la excepción que se esté tratando.
///
/// <para>
/// Existe por <see cref="Components.Layout.MainLayout"/>: su guard de
/// seguridad trata una excepción como "el circuito se fue mientras la consulta
/// seguía en vuelo" y en ese caso no redirige. Inferir eso del <b>tipo</b> de
/// la excepción no distingue el circuito muerto —donde nadie ve la página y no
/// hay nada que hacer— del circuito vivo, donde terminar sin redirigir muestra
/// la página sin aplicar el guard: fallar abierto.
/// </para>
///
/// <para>
/// <b>Scoped</b>, como <see cref="RevalidacionCircuitoActivoHandler"/>: en
/// Blazor Server el scope de DI es el del circuito, así que cada circuito tiene
/// su propia instancia y el componente inyecta exactamente la del suyo.
/// </para>
///
/// <para>
/// El criterio es conservador por construcción, y esa es la propiedad que lo
/// hace utilizable como guarda de seguridad: <see cref="Cerrado"/> solo pasa a
/// <c>true</c> desde <see cref="OnCircuitClosedAsync"/>, que el framework llama
/// al cerrar el circuito y <b>antes</b> de disponer su scope de DI — es decir,
/// antes de que el <c>DbContext</c> scoped pueda lanzar la
/// <see cref="ObjectDisposedException"/> que motiva la pregunta. Si ese orden
/// no se cumpliera en alguna topología, el efecto sería leer "vivo" un circuito
/// ya muerto: una redirección que nadie ve. El error contrario —leer "muerto"
/// uno vivo, que sí sería fallar abierto— no puede ocurrir, porque nada más
/// escribe la bandera.
/// </para>
///
/// <para>
/// <see cref="OnConnectionDownAsync"/> a propósito <b>no</b> marca nada: la
/// conexión SignalR puede caerse y reconectar sobre el mismo circuito, con su
/// estado y su página intactos. Una conexión caída no es un circuito muerto, y
/// tratarla como tal devolvería justo el fallo abierto que esta clase existe
/// para cerrar.
/// </para>
///
/// <para>
/// <b>¿<see cref="Components.Layout.MainLayout"/> llega a ejecutarse alguna vez
/// dentro de un circuito vivo (<c>RendererInfo.IsInteractive == true</c>)?
/// Medido, no inferido: no.</b> Una versión anterior de este comentario
/// afirmaba que sí, apoyándose en que
/// <c>ActividadUsuarioService.RegistrarYEvaluarAsync</c> documenta que
/// "MainLayout e Inicio comparten este servicio con ámbito de circuito y los
/// dos lo invocan en la misma carga". Esa cita es cierta pero no discrimina:
/// MainLayout e Inicio también comparten el mismo scope de DI durante el
/// <b>prerenderizado</b> SSR, porque ambos se renderizan en la misma petición
/// HTTP — compartir el servicio no prueba que compartan el circuito. La
/// pregunta se resolvió instrumentando temporalmente
/// <c>OnParametersSetAsync</c> para registrar <c>RendererInfo.IsInteractive</c>
/// y ejecutando un E2E real (basado en <c>SelectorTemaTests</c>, que ya prueba
/// que el circuito está vivo e interactivo en ese punto: el cambio de tema se
/// aplica de verdad vía interoperación de JS) con dos formas de navegación —
/// recarga completa (<c>page.GotoAsync</c>) y navegación por enlace interno
/// (<c>NavLink</c> de <c>NavMenu</c>, la "enhanced navigation" de Blazor Web
/// Apps sin recarga de página) — contra las dos rutas "/" y "/empresas". Las
/// cinco invocaciones registradas, sin excepción, mostraron
/// <c>IsInteractive=false Name=Static</c>. El comentario de
/// <c>MainLayout.razor</c> ("ese modo no se propaga hacia arriba al Layout")
/// no hablaba solo del cableado de eventos de UI, como se asumió antes: habla
/// literalmente de esto. El límite de la interactividad para
/// <c>@rendermode InteractiveServer</c> declarado en una página lo marca esa
/// página, no sus ascendientes — MainLayout se renderiza una única vez, en la
/// pasada SSR, y nunca se reejecuta dentro del circuito real que sus islas
/// interactivas (<c>SelectorTema</c>, <c>TrazaSoporte</c>, etc., cada una con
/// su propio <c>@rendermode</c>) sí establecen.
/// </para>
///
/// <para>
/// Consecuencia sobre <see cref="Cerrado"/>, y aquí el razonamiento vuelve a
/// ser INFERENCIA, no medición directa: si MainLayout se renderiza siempre en
/// el scope de DI desechable de la pasada SSR/prerenderizado (nunca en el
/// scope persistente del circuito real), el <see cref="EstadoDelCircuito"/>
/// concreto que MainLayout inyecta pertenece a ese scope desechable, y se
/// dispone junto a él —mucho antes de que el circuito real, si llega a
/// existir, pueda cerrarse y llamar a <see cref="OnCircuitClosedAsync"/> sobre
/// una instancia que ya no es esa—. Es decir: es plausible que
/// <see cref="Cerrado"/> nunca llegue a valer <c>true</c> para la instancia de
/// MainLayout en producción, lo que dejaría la rama "circuito cerrado, no
/// redirigir" como código muerto en la práctica actual, y toda excepción del
/// guard —incluida una cancelación por petición realmente abortada—
/// redirigiendo y registrando un error, con el ruido de monitorización que eso
/// implica (la preocupación original que motivó revisar este fichero). No se
/// midió directamente: reproducir un cierre de circuito real a mitad de la
/// ejecución del guard de MainLayout no es determinista desde un E2E. Se deja
/// la rama tal cual, como <b>defensa en profundidad</b>: si el día de mañana
/// MainLayout (o cualquier ascendiente que hoy no lo hace) pasa a declarar su
/// propio <c>@rendermode</c>, o si este razonamiento sobre el scope resulta
/// incompleto, <c>Cerrado</c> sigue siendo el criterio correcto sin tener que
/// tocar este fichero — el coste de mantenerla es una rama que hoy no se
/// ejerce, no un riesgo de seguridad nuevo.
/// </para>
/// </summary>
public sealed class EstadoDelCircuito : CircuitHandler
{
    private volatile bool _cerrado;

    /// <summary>
    /// <c>true</c> solo cuando el circuito de este scope ya se cerró. En una
    /// petición HTTP sin circuito (renderizado del servidor) nunca lo es: ahí
    /// no hay circuito que pueda haberse ido, así que una excepción de acceso a
    /// datos es un fallo real, no esta carrera.
    /// </summary>
    public bool Cerrado => _cerrado;

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _cerrado = true;
        return Task.CompletedTask;
    }
}
