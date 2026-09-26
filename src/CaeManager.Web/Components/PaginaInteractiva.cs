using CaeManager.Application.Common;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Components;

/// <summary>
/// Base obligatoria de toda página con <c>@rendermode InteractiveServer</c> (P1-E1b;
/// lo exige <c>PaginasInteractivasConLimiteDeErroresTests</c>). Es la mitad del envoltorio
/// común; la otra mitad es envolver TODO el marcado de la página en
/// <c>&lt;LimiteDeErrores Pagina="this"&gt;</c>.
///
/// <para>
/// Por qué hacen falta las dos mitades. Una página InteractiveServer es la raíz de su
/// circuito: el límite de MainLayout se pinta en estático y no está por encima de ella.
/// Un ErrorBoundary colocado dentro de la página atrapa lo que falle al pintar su
/// contenido y en sus componentes hijos, pero nunca lo que lanza la propia página: su
/// <c>OnInitializedAsync</c>/<c>OnParametersSetAsync</c> corren antes de que el límite
/// exista, y el receptor de sus manejadores (<c>@onclick="Guardar"</c>, un
/// <c>OnClick</c> de un hijo que apunta a un método de la página) es la página, que
/// está por encima del límite. Sin esta base, una excepción ahí tumba el circuito entero.
/// </para>
///
/// <para>
/// Qué hace: contiene las excepciones del ciclo de vida de la página
/// (<see cref="SetParametersAsync"/>), de sus manejadores de evento
/// (<see cref="IHandleEvent"/>) y de <c>OnAfterRender(Async)</c>
/// (<see cref="IHandleAfterRender"/>); las registra en el log y en Sentry por la misma
/// vía que <see cref="LimiteDeErrores"/> y marca <see cref="FalloDePagina"/>, con lo que
/// el límite sustituye el contenido por el aviso recuperable. El reintento recarga la
/// página: el estado de una página cuya inicialización falló no es de fiar, así que no se
/// vuelve a pintar tal cual. El aviso nunca enseña el mensaje ni el tipo de la excepción.
/// </para>
///
/// <para>
/// Qué deja pasar: <see cref="NavigationException"/>, que es como NavigationManager
/// redirige durante el prerenderizado — contenerla rompería las redirecciones. Y, como
/// ComponentBase, una tarea cancelada no es un fallo.
/// </para>
/// </summary>
public abstract class PaginaInteractiva : ComponentBase, IHandleEvent, IHandleAfterRender
{
    // Por IServiceProvider y no por [Inject] directo: el log y Sentry solo se necesitan
    // cuando algo falla, y pedirlos al construir haría de ellos una precondición para
    // pintar cualquier página sana (y para cada test de página). Si faltaran en
    // producción, GetRequiredService falla en voz alta al primer error.
    [Inject] private IServiceProvider Servicios { get; set; } = default!;

    private bool _yaNotificadoTrasPintar;

    /// <summary>
    /// La página lanzó en su ciclo de vida o en un manejador y se ha contenido: su
    /// <see cref="LimiteDeErrores"/> muestra el aviso en lugar del contenido.
    /// </summary>
    public bool FalloDePagina { get; private set; }

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
    // aquí): primera vez = firstRender. Con la página ya caída no se vuelve a llamar a sus
    // ganchos: uno que fallara siempre repintaría y volvería a fallar sin fin.
    async Task IHandleAfterRender.OnAfterRenderAsync()
    {
        if (FalloDePagina)
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
                // Como el renderizador: una tarea cancelada (el token de la página al
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
        if (FalloDePagina)
        {
            return;
        }

        FalloDePagina = true;
        StateHasChanged();
    }
}
