using System.Globalization;
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
    /// <summary>
    /// Códigos que el desplegable ofrece, con su nombre legible. Es también la
    /// tabla de traducción del badge de proveedor, que antes imprimía el código
    /// en crudo («mistral-ocr», «ninguno»). Un código que no esté aquí se
    /// muestra tal cual: inventarle un nombre sería peor que enseñarlo.
    /// </summary>
    private static readonly (string Codigo, string Nombre)[] ProveedoresDelDesplegable =
    [
        ("anthropic", "Anthropic"),
        ("gemini", "Gemini"),
        ("mistral-ocr", "Mistral OCR"),
        ("cache", "Caché"),
        ("ninguno", "Sin proveedor (fallo)"),
    ];

    private static readonly CultureInfo Espanol = CultureInfo.GetCultureInfo("es-ES");

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

    /// <summary>Registro con el panel de detalle abierto; como mucho uno.</summary>
    private Guid? _registroAbierto;

    /// <summary>
    /// Número de la última carga pedida. Cada <see cref="CargarAsync"/> se
    /// queda con el suyo y, al volver del <c>await</c>, solo escribe estado si
    /// sigue siendo el vigente: una respuesta vieja que llegue la última (se
    /// filtró o se paginó mientras estaba en vuelo) pintaría sus filas bajo el
    /// filtro y la página nuevos.
    /// </summary>
    private int _cargaVigente;

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
        var carga = ++_cargaVigente;
        _cargando = true;
        _error = false;
        _registroAbierto = null;
        StateHasChanged();

        ResultadoPaginado<RegistroAuditoriaIaDto>? resultado = null;
        var fallo = false;
        try
        {
            resultado = await Mediator.Send(new ObtenerAuditoriaIaQuery(_filtroProveedor, _pagina, TamanoPagina));
        }
        catch (Exception)
        {
            fallo = true;
        }

        // Una carga posterior ya manda: ni sus datos ni su error son de la
        // pantalla que se está viendo, y tampoco puede apagar su «cargando».
        if (carga != _cargaVigente)
            return;

        if (!fallo)
            _resultado = resultado;
        _error = fallo;
        _cargando = false;
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

    private bool FiltroFueraDelDesplegable =>
        HayFiltrosActivos && ProveedoresDelDesplegable.All(p => p.Codigo != _filtroProveedor);

    private Task LimpiarFiltrosAsync() => FiltrarPorProveedorAsync(null);

    private Task IrAPaginaAsync(int pagina)
    {
        _pagina = pagina;
        return CargarAsync();
    }

    private void AlternarDetalle(Guid registroId) =>
        _registroAbierto = _registroAbierto == registroId ? null : registroId;

    private static string NombreProveedor(string proveedorCodigo) =>
        ProveedoresDelDesplegable.FirstOrDefault(p => p.Codigo == proveedorCodigo).Nombre ?? proveedorCodigo;

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

    private static string FormatearFecha(DateTime utc) => utc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

    private static string FormatearCoste(decimal? coste) =>
        coste.HasValue ? $"${coste.Value:F4}" : "—";

    /// <summary>Con separador de miles («12.040»): la columna se lee alineada a la derecha.</summary>
    private static string FormatearMilisegundos(long milisegundos) => milisegundos.ToString("N0", Espanol);

    /// <summary>
    /// La huella completa son 64 caracteres hexadecimales y rompería el panel;
    /// se abrevia a la vista y se copia entera con el botón (y va en el title).
    /// </summary>
    private static string AbreviarHuella(string hash) => hash.Length <= 12 ? hash : hash[..12] + "…";

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
