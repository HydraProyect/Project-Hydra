using CaeManager.Application.Common;
using CaeManager.Application.DocumentosIa.Queries;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.AuditoriaIa.Pages;

public partial class AuditoriaIa : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "proveedor")]
    public string? ProveedorInicial { get; set; }

    private ResultadoPaginado<RegistroAuditoriaIaDto>? _resultado;
    private bool _cargando = true;
    private bool _error;
    private string? _filtroProveedor;
    private int _pagina = 1;
    private const int TamanoPagina = 30;

    protected override Task OnInitializedAsync()
    {
        // Ver el comentario equivalente en Auditoria.razor.cs: OnInitializedAsync
        // corre antes que OnParametersSet en el primer render, así que la
        // carga inicial necesita el filtro ya resuelto aquí, no solo allí.
        _filtroProveedor = string.IsNullOrWhiteSpace(ProveedorInicial) ? null : ProveedorInicial;
        return CargarAsync();
    }

    /// <summary>
    /// Re-sincroniza el filtro con la URL en navegaciones posteriores (P1-18
    /// de docs/business/MATURITY_REVIEW.md) — la recarga la sigue disparando
    /// cada manejador de filtro explícitamente, no este método.
    /// </summary>
    protected override void OnParametersSet()
    {
        _filtroProveedor = string.IsNullOrWhiteSpace(ProveedorInicial) ? null : ProveedorInicial;
    }

    private async Task CargarAsync()
    {
        _cargando = true;
        _error = false;
        StateHasChanged();

        try
        {
            _resultado = await Mediator.Send(new ObtenerAuditoriaIaQuery(_filtroProveedor, _pagina, TamanoPagina));
        }
        catch (Exception)
        {
            _error = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    private Task FiltrarPorProveedorAsync(string? proveedor)
    {
        _filtroProveedor = string.IsNullOrWhiteSpace(proveedor) ? null : proveedor;
        _pagina = 1;
        NavigationManager.ActualizarFiltroEnUrl("proveedor", proveedor);
        return CargarAsync();
    }

    /// <summary>
    /// Único filtro de la página. Ver <c>Auditoria.razor.cs</c>: separa "no hay
    /// nada" de "nada con este filtro", que aquí además se leen al revés — un
    /// cero filtrando por fallos es una buena noticia, no una IA parada.
    /// </summary>
    private bool HayFiltrosActivos => !string.IsNullOrWhiteSpace(_filtroProveedor);

    /// <summary>Ver el comentario de <c>Auditoria.razor.cs</c>: la condición se
    /// aplana en una propiedad para que la guarda quede en el idioma que el
    /// trinquete de vacío-por-filtro sabe leer.</summary>
    private bool SinRegistros => _resultado is null || _resultado.Elementos.Count == 0;

    /// <summary>
    /// La página ya cargada. Solo se usa en la rama que <see cref="SinRegistros"/>
    /// descarta, donde nunca es null — pero el compilador no puede verlo a
    /// través de una propiedad, y CI compila con <c>-warnaserror</c>.
    /// </summary>
    private ResultadoPaginado<RegistroAuditoriaIaDto> Resultado => _resultado!;

    private Task LimpiarFiltrosAsync() => FiltrarPorProveedorAsync(null);

    private Task IrAPaginaAsync(int pagina)
    {
        _pagina = pagina;
        return CargarAsync();
    }

    private static TonoBadge BadgeParaProveedor(string proveedorCodigo) => proveedorCodigo switch
    {
        "cache" => TonoBadge.Info,
        "ninguno" => TonoBadge.Peligro,
        _ => TonoBadge.Exito
    };

    private static TonoBadge BadgeParaConfianza(int confianza) => confianza switch
    {
        >= 80 => TonoBadge.Exito,
        >= 60 => TonoBadge.Advertencia,
        _ => TonoBadge.Peligro
    };

    private static string FormatearCoste(decimal? coste) =>
        coste.HasValue ? $"${coste.Value:F4}" : "—";

    /// <summary>
    /// "¿qué hizo la IA y quién lo confirmó?" (MACRO_PLAN § 6.6): null sin
    /// DocumentoId significa triage previo a la creación del Documento (no
    /// aplica decisión); null con DocumentoId significa que todavía no se
    /// resolvió la revisión pendiente.
    /// </summary>
    private static string TextoDecision(RegistroAuditoriaIaDto registro) => registro switch
    {
        { DocumentoId: null } => "—",
        { DecisionHumana: DecisionHumanaIa.AutomaticaSinRevision } => "Automática",
        { DecisionHumana: DecisionHumanaIa.ConfirmadaManual } => "Confirmada",
        { DecisionHumana: DecisionHumanaIa.DescartadaManual } => "Descartada",
        _ => "Pendiente"
    };

    private static TonoBadge BadgeParaDecision(RegistroAuditoriaIaDto registro) => registro.DecisionHumana switch
    {
        DecisionHumanaIa.AutomaticaSinRevision => TonoBadge.Info,
        DecisionHumanaIa.ConfirmadaManual => TonoBadge.Exito,
        DecisionHumanaIa.DescartadaManual => TonoBadge.Advertencia,
        _ => TonoBadge.Info
    };
}
