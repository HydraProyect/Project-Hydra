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
