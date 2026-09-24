using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.IncorporacionCartera.Components;

/// <summary>
/// Envuelve el estado vacío «sin filtros» de una lista o una cola. Antes de
/// pintarlo pregunta si quien mira tiene alcance cero en el Tenant vigente
/// (<see cref="ObtenerAlcanceCeroQuery"/>): con alcance cero pinta
/// <see cref="AvisoSinAsignacionCartera"/> en vez del vacío de la pantalla, que
/// diría «al día» o invitaría a crear el primero (P0-9a, FS-03 a FS-06). Con
/// alcance, pinta el vacío de siempre.
/// <para>
/// Mientras la consulta no ha respondido no pinta nada: enseñar el vacío
/// positivo un instante sería justo lo que se quiere evitar. Si la consulta
/// falla, cae al vacío de la pantalla —el comportamiento anterior— y queda
/// registrado: una comprobación secundaria no deja la lista en blanco.
/// </para>
/// <para>
/// <see cref="AlcanceCero"/> admite <c>@bind</c> para que la página esconda
/// también su «+ Nuevo» de cabecera en ese estado.
/// </para>
/// </summary>
public partial class VacioSegunAlcance : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ILogger<VacioSegunAlcance> Logger { get; set; } = default!;

    /// <summary>El estado vacío propio de la pantalla, para cuando quien mira sí tiene alcance.</summary>
    [Parameter] public RenderFragment? ChildContent { get; set; }

    /// <summary>Descripción del aviso adaptada a la pantalla; por defecto, la genérica.</summary>
    [Parameter] public string? DescripcionSinAlcance { get; set; }

    /// <summary>Si quien mira tiene alcance cero; solo de salida (se fija al resolver la consulta).</summary>
    [Parameter] public bool AlcanceCero { get; set; }

    [Parameter] public EventCallback<bool> AlcanceCeroChanged { get; set; }

    private bool? _alcanceCero;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _alcanceCero = await Mediator.Send(new ObtenerAlcanceCeroQuery());
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "No se pudo comprobar el alcance cero; se muestra el estado vacío de la pantalla.");
            _alcanceCero = false;
        }

        if (_alcanceCero != AlcanceCero)
            await AlcanceCeroChanged.InvokeAsync(_alcanceCero.Value);
    }
}
