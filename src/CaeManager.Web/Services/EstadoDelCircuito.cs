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
/// dentro de un circuito vivo, si <c>App.razor</c> monta <c>&lt;Routes /&gt;</c>
/// sin <c>@rendermode</c>?</b> Sí. Que el elemento raíz no lleve <c>@rendermode</c>
/// solo fija el modo de la primera pasada (SSR); no impide que una página hija
/// —la mayoría de las protegidas, como <c>Features/Dashboard/Pages/Inicio.razor</c>—
/// declare <c>@rendermode InteractiveServer</c> en sí misma, y esa página vuelve
/// a ejecutar todo su árbol de ascendientes, MainLayout incluido, una segunda
/// vez dentro del circuito real una vez este se establece. La prueba no es una
/// lectura de <c>App.razor</c>: es un hecho ya documentado en
/// <c>ActividadUsuarioService.RegistrarYEvaluarAsync</c> — "MainLayout e Inicio
/// comparten este servicio con ámbito de circuito y los dos lo invocan en la
/// misma carga" — y un servicio <c>scoped</c> solo puede compartirse así si las
/// dos llamadas caen en el mismo scope de DI, que en Blazor Server es el del
/// circuito. El comentario de <c>MainLayout.razor</c> ("ese modo no se propaga
/// hacia arriba al Layout") habla de otra cosa: del cableado de eventos de UI de
/// los componentes que viven fuera de <c>@Body</c> (toasts, atajos, buscador),
/// no de si el método C# <c>OnParametersSetAsync</c> de MainLayout se invoca
/// dentro de un circuito vivo.
/// </para>
///
/// <para>
/// Eso sí dejar claro dos pasadas distintas, ambas con <see cref="Cerrado"/> en
/// <c>false</c> pero por motivos distintos: el <b>prerenderizado</b> de esa
/// misma página interactiva (antes de que el circuito real exista, en un DI
/// scope desechable — ver el <c>remarks</c> de <c>RegistrarYEvaluarAsync</c>) y
/// una petición realmente <b>SSR sin ningún descendiente interactivo</b> (por
/// ejemplo, una página con <c>AuthLayout</c> en vez de este). En ambas, una
/// excepción del guard es un fallo real de acceso a datos, no la carrera de
/// desconexión: no hay circuito que haya podido irse, así que fallar cerrado
/// (redirigir) es la respuesta correcta, no un efecto colateral del criterio
/// conservador — ver <c>MainLayoutFallaCerradoTests</c>.
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
