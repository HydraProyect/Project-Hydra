using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Web.Features.IncorporacionCartera.Recursos;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.IncorporacionCartera.Components;

/// <summary>
/// Estado vacío de alcance cero: quien mira no tiene ninguna Asignación de
/// Cartera vigente en el Tenant propietario, así que la pantalla no tiene nada
/// que enseñarle. Dice eso y a quién pedirla (el Coordinador CAE), en vez de
/// un vacío positivo («al día», «sin pendientes») o de invitar a crear lo que
/// quizá ya existe fuera de su alcance (P0-9a, FS-03 a FS-06). Alcance cero
/// es un estado correcto, no un error.
/// <para>
/// Si quien mira es Gestor CAE de un Operador CAE con Empresas que puede pedir,
/// ofrece «Añadir a mi cartera» con <see cref="DialogoSolicitarIncorporacion"/>,
/// igual que Mi trabajo. Si la consulta de candidatos falla o no hay ninguno,
/// el botón no aparece y el texto sigue remitiendo al Coordinador CAE.
/// </para>
/// </summary>
public partial class AvisoSinAsignacionCartera : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosIncorporacionCartera> Textos { get; set; } = default!;
    [Inject] private ILogger<AvisoSinAsignacionCartera> Logger { get; set; } = default!;

    /// <summary>Título propio de la pantalla; por defecto, «Sin Asignación de Cartera».</summary>
    [Parameter] public string? Titulo { get; set; }

    /// <summary>Descripción propia de la pantalla; por defecto, la genérica que remite al Coordinador CAE.</summary>
    [Parameter] public string? Descripcion { get; set; }

    private bool _puedeAnadirACartera;
    private bool _dialogoVisible;

    protected override Task OnInitializedAsync() => CargarCandidatosAsync();

    private async Task CargarCandidatosAsync()
    {
        try
        {
            var resultado = await Mediator.Send(new ObtenerCandidatosIncorporacionCarteraQuery());
            _puedeAnadirACartera = resultado.EsExitoso && resultado.Valor.Count > 0;
        }
        catch (Exception ex)
        {
            // Un botón secundario no tumba el aviso: se esconde y queda registrado.
            Logger.LogWarning(ex, "El aviso de alcance cero no pudo consultar los candidatos de incorporación a cartera.");
            _puedeAnadirACartera = false;
        }
    }
}
