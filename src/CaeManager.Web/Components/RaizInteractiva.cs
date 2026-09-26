using CaeManager.Application.Common;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Components;

/// <summary>
/// Contención de errores de una raíz de circuito InteractiveServer (P1-E1b, P1-E1c). No se
/// hereda directamente: una página hereda de <see cref="PaginaInteractiva"/> y un islote
/// interactivo del layout de <see cref="IsloteInteractivo"/>; las dos bases solo cambian cómo
/// se presenta el aviso (lo exigen <c>PaginasInteractivasConLimiteDeErroresTests</c> e
/// <c>IslotesInteractivosConLimiteDeErroresTests</c>).
///
/// <para>
/// Por qué hace falta. Un componente con <c>@rendermode InteractiveServer</c> (en su
/// directiva o en el punto de uso, como los de MainLayout) es una raíz de su circuito, y
/// todas las raíces de una pantalla comparten circuito: una excepción no contenida en
/// cualquiera de ellas lo tumba y se lleva la pantalla entera. Un ErrorBoundary colocado
/// dentro de la raíz atrapa lo que falle al pintar su contenido y en sus componentes
/// hijos, pero nunca lo que lanza la propia raíz: su <c>OnInitializedAsync</c>/
/// <c>OnParametersSetAsync</c> corren antes de que el límite exista, y el receptor de sus
/// manejadores (<c>@onclick="Guardar"</c>, un <c>OnClick</c> de un hijo que apunta a un
/// método suyo) es la raíz, que está por encima del límite.
/// </para>
///
/// <para>
/// Qué hace: contiene las excepciones del ciclo de vida
/// (<see cref="SetParametersAsync"/>), de los manejadores de evento
/// (<see cref="IHandleEvent"/>) y de <c>OnAfterRender(Async)</c>
/// (<see cref="IHandleAfterRender"/>); las registra en el log y en Sentry por la misma
/// vía que <see cref="LimiteDeErrores"/> y marca <see cref="FalloDeRaiz"/>, con lo que
/// el límite sustituye el contenido por el aviso. El reintento recarga la pantalla: el
/// estado de una raíz cuya inicialización falló no es de fiar, así que no se vuelve a
/// pintar tal cual. El aviso nunca enseña el mensaje ni el tipo de la excepción.
/// </para>
///
/// <para>
/// Qué deja pasar: <see cref="NavigationException"/>, que es como NavigationManager
/// redirige durante el prerenderizado — contenerla rompería las redirecciones. Y, como
/// ComponentBase, una tarea cancelada no es un fallo.
/// </para>
/// </summary>
public abstract class RaizInteractiva : ComponentBase, IHandleEvent, IHandleAfterRender
{
    // Por IServiceProvider y no por [Inject] directo: el log y Sentry solo se necesitan
    // cuando algo falla, y pedirlos al construir haría de ellos una precondición para
    // pintar cualquier raíz sana (y para cada test de página o de islote). Si faltaran en
    // producción, GetRequiredService falla en voz alta al primer error.
    [Inject] private IServiceProvider Servicios { get; set; } = default!;

    private bool _yaNotificadoTrasPintar;

    // Solo PaginaInteractiva e IsloteInteractivo: el aviso depende de cuál de las dos es.
    private protected RaizInteractiva()
    {
    }

    /// <summary>
    /// La raíz lanzó en su ciclo de vida o en un manejador y se ha contenido: su
    /// <see cref="LimiteDeErrores"/> muestra el aviso en lugar del contenido.
    /// </summary>
    public bool FalloDeRaiz { get; private set; }

    public override async Task SetParametersAsync(ParameterView parameters)
    {
        try
        {
            await base.SetParametersAsync(parameters);
        }
        catch (Exception excepcion) when (DebeContenerse(excepcion))
        {
            Contener(excepcion);
        }
    }

    // Mismo contrato que la implementación explícita de ComponentBase (repintar al
    // empezar y al terminar; una tarea cancelada no es un fallo), con el fallo contenido.
    async Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object? arg)
    {
        try
        {
            var tarea = callback.InvokeAsync(arg);
            var hayQueEsperar = tarea.Status is not (TaskStatus.RanToCompletion or TaskStatus.Canceled);
            StateHasChanged();
            if (!hayQueEsperar)
            {
                return;
            }

            try
            {
                await tarea;
            }
            catch when (tarea.IsCanceled)
            {
                return;
            }

            StateHasChanged();
        }
        catch (Exception excepcion) when (DebeContenerse(excepcion))
        {
            Contener(excepcion);
        }
    }

    // Replica la implementación explícita de ComponentBase (que no se puede invocar desde
    // aquí): primera vez = firstRender. Con la raíz ya caída no se vuelve a llamar a sus
    // ganchos: uno que fallara siempre repintaría y volvería a fallar sin fin.
    async Task IHandleAfterRender.OnAfterRenderAsync()
    {
        if (FalloDeRaiz)
        {
            return;
        }

        var primeraVez = !_yaNotificadoTrasPintar;
        _yaNotificadoTrasPintar = true;
        try
        {
            OnAfterRender(primeraVez);
            var tarea = OnAfterRenderAsync(primeraVez);
            try
            {
                await tarea;
            }
            catch when (tarea.IsCanceled)
            {
                // Como el renderizador: una tarea cancelada (el token de la raíz al
                // desecharla, un interop que se abandona) no es un fallo. Revisión Codex.
                return;
            }
        }
        catch (Exception excepcion) when (DebeContenerse(excepcion))
        {
            Contener(excepcion);
        }
    }

    private static bool DebeContenerse(Exception excepcion) =>
        excepcion is not NavigationException;

    private void Contener(Exception excepcion)
    {
        LimiteDeErrores.Registrar(Servicios, excepcion);
        if (FalloDeRaiz)
        {
            return;
        }

        FalloDeRaiz = true;
        StateHasChanged();
    }
}
