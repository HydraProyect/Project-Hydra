using CaeManager.Application.Plantillas.Commands.AgregarVersionPlantilla;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillasDocumento;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace CaeManager.Web.Features.Documentos.Components;

public partial class PlantillasTab : ComponentBase
{
    private const long TamanoMaximoArchivoBytes = 10 * 1024 * 1024;

    private IReadOnlyList<PlantillaDocumentoListaDto> _plantillas = [];
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalGenerados;

    /// <summary>
    /// Sub-pestaña que pide el padre (Catálogo/Generados) — ver
    /// <see cref="OnParametersSet"/> para cuándo se aplica. La página
    /// standalone /plantillas (Plantillas.razor.cs) la recalcula en cada
    /// render desde su propio query string ?Pestana=; embebida dentro de
    /// /documentos no se pasa nunca, así que se queda fija en "catalogo".
    /// </summary>
    [Parameter] public string PestanaInicial { get; set; } = "catalogo";

    /// <summary>
    /// Se invoca cada vez que cambia la sub-pestaña — solo Plantillas.razor
    /// la escucha, para reflejarla en su URL; /documentos la ignora (no le
    /// pasa manejador), porque aquí la sub-pestaña es puramente interna.
    /// </summary>
    [Parameter] public EventCallback<string> PestanaActivaChanged { get; set; }

    private string _pestanaActiva = "catalogo";
    private string? _pestanaInicialAplicada;

    /// <summary>
    /// Hallazgo de revisión adversarial de Codex: aplicar PestanaInicial solo
    /// en OnInitialized (como una primera versión de este componente hacía)
    /// deja de resincronizarse si el query string de una instancia YA viva de
    /// /plantillas cambia por una vía distinta al propio clic de este
    /// componente (atrás/adelante del navegador, otro enlace a
    /// ?Pestana=generados sobre la página ya abierta) — la página original
    /// sí lo hacía, con [SupplyParameterFromQuery] resuelto en cada
    /// OnParametersSet. Reaplicar solo cuando el valor CAMBIA (no en cada
    /// render) reproduce ese comportamiento sin romper el caso embebido en
    /// /documentos, donde PestanaInicial nunca varía y el selector debe
    /// quedarse en memoria aunque la página padre se re-renderice por otro
    /// motivo (cambiar de pestaña exterior, refiltrar la rejilla...).
    /// </summary>
    protected override void OnParametersSet()
    {
        if (PestanaInicial == _pestanaInicialAplicada) return;

        _pestanaInicialAplicada = PestanaInicial;
        _pestanaActiva = PestanaInicial;
    }

    private async Task CambiarPestanaAsync(string pestana)
    {
        _pestanaActiva = pestana;
        await PestanaActivaChanged.InvokeAsync(pestana);
    }

    private IReadOnlyList<PestanaDefinicion> PestanasConContador =>
        [new("catalogo", $"Catálogo ({_plantillas.Count})"), new("generados", $"Generados ({_totalGenerados})")];

    // Subir nueva versión (PR10) — el gestor decide manualmente que este PDF
    // sustituye al de una plantilla ya existente (ADR-010 § 4, § 6).
    private Guid? _plantillaParaNuevaVersionId;
    private string? _plantillaParaNuevaVersionNombre;
    private byte[]? _archivoNuevaVersion;
    private string? _nombreArchivoNuevaVersion;
    private bool _subiendoNuevaVersion;

    private bool ModalNuevaVersionVisible => _plantillaParaNuevaVersionId is not null;

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            _plantillas = await Mediator.Send(new ObtenerPlantillasDocumentoQuery());
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    private void CambiarTotalGenerados(int total) => _totalGenerados = total;

    private void IrAConfigurar(Guid versionId) => Navigation.NavigateTo($"/plantillas/{versionId}/editar");

    private void AbrirModalNuevaVersion(Guid plantillaId, string nombre)
    {
        _plantillaParaNuevaVersionId = plantillaId;
        _plantillaParaNuevaVersionNombre = nombre;
        _archivoNuevaVersion = null;
        _nombreArchivoNuevaVersion = null;
    }

    private void CerrarModalNuevaVersion(bool visible)
    {
        if (visible) return;
        _plantillaParaNuevaVersionId = null;
    }

    private async Task ManejarArchivoNuevaVersionAsync(InputFileChangeEventArgs e)
    {
        await using var flujo = e.File.OpenReadStream(TamanoMaximoArchivoBytes);
        using var memoria = new MemoryStream();
        await flujo.CopyToAsync(memoria);
        _archivoNuevaVersion = memoria.ToArray();
        _nombreArchivoNuevaVersion = e.File.Name;
    }

    private bool PuedeSubirNuevaVersion => !_subiendoNuevaVersion && _archivoNuevaVersion is { Length: > 0 };

    private async Task SubirNuevaVersionAsync()
    {
        if (!PuedeSubirNuevaVersion || _plantillaParaNuevaVersionId is not { } plantillaId || _archivoNuevaVersion is null) return;

        _subiendoNuevaVersion = true;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new AgregarVersionPlantillaCommand(
                plantillaId, _archivoNuevaVersion, _nombreArchivoNuevaVersion ?? "plantilla.pdf"));

            if (resultado.EsFallido)
            {
                Toasts.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            if (resultado.Valor.ArchivoIdenticoAVersionAnterior)
                Toasts.Mostrar("Este archivo es idéntico a la versión actual — probablemente no hacía falta subir nada nuevo.", TonoToast.Advertencia);

            Navigation.NavigateTo($"/plantillas/{resultado.Valor.PlantillaDocumentoVersionId}/editar");
        }
        finally
        {
            _subiendoNuevaVersion = false;
        }
    }
}
