using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// La parte de servidor de <c>wwwroot/js/aviso-salida-navegador.js</c>: el punto al que el navegador pregunta, antes de
/// atender un clic en un enlace interno o un «atrás»/«adelante», si algún <see cref="AvisoCambiosSinGuardar"/> del
/// circuito tiene algo que perder. Esas dos navegaciones no pasan por el <c>NavigationLock</c> (el Router no es
/// interactivo y las atiende la navegación mejorada en el cliente); la que lanza el servidor con <c>NavigateTo</c> sí.
///
/// <para>
/// Una por circuito (la clave es su <see cref="NavigationManager"/>, como el registro de avisos montados), creada por el
/// primer aviso que se monta. No decide nada: quién detiene la navegación y qué pasa al contestar es del aviso.
/// </para>
/// </summary>
public sealed class SalidaDelNavegador
{
    private static readonly ConditionalWeakTable<NavigationManager, SalidaDelNavegador> PorCircuito = new();

    private readonly NavigationManager _navegacion;
    private readonly IJSRuntime _js;
    private readonly DotNetObjectReference<SalidaDelNavegador> _referencia;

    private SalidaDelNavegador(NavigationManager navegacion, IJSRuntime js)
    {
        _navegacion = navegacion;
        _js = js;
        _referencia = DotNetObjectReference.Create(this);
    }

    /// <summary>
    /// Deja al navegador apuntando a este circuito. Lo llama cada aviso tras su primer render: una página nueva puede
    /// traer un circuito nuevo, y el navegador pregunta siempre al último que se presentó.
    /// </summary>
    internal static async Task ConectarAsync(NavigationManager navegacion, IJSRuntime js)
    {
        var salida = PorCircuito.GetValue(navegacion, n => new SalidaDelNavegador(n, js));
        try
        {
            await js.InvokeVoidAsync("talvegAvisoSalida.conectar", salida._referencia);
        }
        catch (Exception)
        {
            // Presentarse es lo mejor que se puede hacer, nunca un motivo para tumbar el formulario que lleva el aviso:
            // sin navegador al otro lado (circuito cerrándose), sin el script o con un intérprete de JS que no lo conoce
            // (los arneses de bUnit en modo estricto), la navegación que lanza el servidor sigue cubierta por el
            // NavigationLock y la del navegador pasa sin consultar.
        }
    }

    /// <summary>
    /// ¿Algún aviso detiene esta navegación del navegador? <c>true</c>: ya está preguntando y el navegador no navega (un
    /// recorrido del historial lo deshace). <c>false</c>: nada que perder, sigue su camino.
    /// </summary>
    [JSInvokable]
    public bool ConsultarSalida(string destino, bool esRecorrido) =>
        // El destino lo dice el navegador: solo se atiende el que está dentro de la aplicación, porque «Salir y
        // descartar» navega a él desde el servidor.
        Uri.TryCreate(destino, UriKind.Absolute, out var uri)
        && uri.AbsoluteUri.StartsWith(_navegacion.BaseUri, StringComparison.OrdinalIgnoreCase)
        && AvisoCambiosSinGuardar.DetenerSalidaDelNavegador(
            _navegacion, destino, esRecorrido ? ReanudarRecorridoAsync : null, []);

    private async Task ReanudarRecorridoAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("talvegAvisoSalida.reanudar");
        }
        catch (Exception e) when (e is JSDisconnectedException or OperationCanceledException)
        {
            // El navegador ya no está: no queda recorrido que repetir.
        }
    }
}
