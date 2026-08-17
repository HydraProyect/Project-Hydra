using CaeManager.Application.Plantillas.Commands.AgregarVersionPlantilla;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillasDocumento;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace CaeManager.Web.Features.Plantillas.Pages;

public partial class Plantillas : ComponentBase
{
    private const long TamanoMaximoArchivoBytes = 10 * 1024 * 1024;

    private IReadOnlyList<PlantillaDocumentoListaDto> _plantillas = [];
    private bool _cargando = true;
    private bool _errorCarga;

    private string _pestanaActiva = "catalogo";
    private int _totalGenerados;

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
