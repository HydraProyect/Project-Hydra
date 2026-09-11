using CaeManager.Application.Plantillas.Commands.AgregarVersionPlantilla;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillasDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace CaeManager.Web.Features.Documentos.Components;

public partial class PlantillasTab : ComponentBase, IDisposable
{
    private const long TamanoMaximoArchivoBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Qué es una plantilla. Lo pinta la propia pestaña cuando va embebida en
    /// /documentos, y la cabecera Gen 2 de /plantillas como entradilla: un solo
    /// texto para que las dos superficies no deriven.
    /// </summary>
    public const string TextoDescripcion =
        "Formularios que entrega un centro (acceso, autorización, ficha de riesgos…) configurados una vez para "
        + "rellenarse automáticamente con los datos ya conocidos de cada Trabajador o Empresa.";

    private IReadOnlyList<PlantillaDocumentoListaDto> _plantillas = [];
    private bool _cargando = true;
    private bool _errorCarga;
    private int _avisosPendientes;

    /// <summary>
    /// La página que monta la pestaña ya pinta la cabecera (título,
    /// entradilla y «+ Nueva plantilla»): /plantillas lo hace con
    /// <see cref="CabeceraPagina"/>. Embebida en /documentos no se pasa, y la
    /// pestaña conserva su propia entradilla y su botón de alta.
    /// </summary>
    [Parameter] public bool CabeceraEnLaPagina { get; set; }

    /// <summary>
    /// Número de la última carga del catálogo. Se captura ANTES del
    /// <c>await</c> y, al volver, solo se escribe estado si sigue siendo la
    /// vigente y la pestaña sigue viva. Mismo patrón que Empresas.
    /// </summary>
    private int _cargaVigente;

    /// <summary>
    /// Se cancela al retirarse la pestaña (salir de la página o cambiar de
    /// pestaña exterior en /documentos): la consulta en curso deja de trabajar
    /// para nadie. Mismo patrón que DeteccionTrabajadores y Empresas.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    /// <summary>Plantilla con el foco de teclado (atajos j/k, P3-31).</summary>
    private Guid? _idEnfocado;

    public void Dispose()
    {
        if (_desechado)
            return;

        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    /// <summary>La respuesta es de la carga vigente y la pestaña sigue viva.</summary>
    private bool EsVigente(int carga) => !_desechado && carga == _cargaVigente;

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

    /// <summary>
    /// El badge de "Generados" no es la cardinalidad de la pestaña (cuántos
    /// documentos generados hay): es "N documentos generados con avisos,
    /// pendientes de revisar" (Plantillas TALVEG.dc.html). Se omite en cero
    /// para no leerse como una alarma vacía — "Generados" a secas basta
    /// cuando no hay nada que revisar.
    /// </summary>
    private string EtiquetaGenerados => _avisosPendientes > 0 ? $"Generados ({_avisosPendientes} con avisos)" : "Generados";

    private IReadOnlyList<PestanaDefinicion> PestanasConContador =>
        [new("catalogo", $"Catálogo ({_plantillas.Count})"), new("generados", EtiquetaGenerados)];

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
        if (_desechado)
            return;

        var carga = ++_cargaVigente;
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var plantillas = await Mediator.Send(new ObtenerPlantillasDocumentoQuery(), _ciclo.Token);
            if (!EsVigente(carga))
                return;

            _plantillas = plantillas;
            _idEnfocado = null;
        }
        catch (Exception) when (!EsVigente(carga))
        {
            // Una carga superada (o cancelada al retirarse) que falla no es un
            // error de la vigente: no puede tapar su resultado con el estado de error.
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            if (EsVigente(carga))
                _cargando = false;
        }
    }

    /// <summary>
    /// Texto del estado de la última versión. Antes se pintaba el nombre del
    /// enum tal cual, y una versión pendiente de revisión salía como
    /// «PendienteRevision».
    /// </summary>
    private static string TextoEstado(EstadoConfiguracionPlantilla estado) => estado switch
    {
        EstadoConfiguracionPlantilla.Borrador => "Borrador",
        EstadoConfiguracionPlantilla.PendienteRevision => "Pendiente de revisión",
        EstadoConfiguracionPlantilla.Confirmada => "Confirmada",
        _ => estado.ToString()
    };

    /// <summary>Mismos rótulos que SelectorLoteDocumental: el enum no lleva tildes.</summary>
    private static string TextoAmbito(AmbitoAplicacion ambito) => ambito switch
    {
        AmbitoAplicacion.Vehiculo => "Vehículo",
        _ => ambito.ToString()
    };

    private void ManejarAtajo(string tecla)
    {
        if (_plantillas.Count == 0) return;

        var indiceActual = _idEnfocado is { } id ? _plantillas.ToList().FindIndex(p => p.Id == id) : -1;

        switch (tecla)
        {
            case "j":
                _idEnfocado = _plantillas[Math.Min(indiceActual + 1, _plantillas.Count - 1)].Id;
                break;
            case "k":
                _idEnfocado = _plantillas[Math.Max(indiceActual - 1, 0)].Id;
                break;
            case "Enter":
                if (indiceActual >= 0)
                    IrAConfigurar(_plantillas[indiceActual].UltimaVersionId);
                break;
        }
    }

    private void CambiarAvisosPendientes(int avisos) => _avisosPendientes = avisos;

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
