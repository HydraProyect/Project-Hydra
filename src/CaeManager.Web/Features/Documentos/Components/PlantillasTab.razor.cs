using System.Globalization;
using CaeManager.Application.Common;
using CaeManager.Application.Plantillas.Commands.AgregarVersionPlantilla;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillasDocumento;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Plantillas.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.WebUtilities;

namespace CaeManager.Web.Features.Documentos.Components;

public partial class PlantillasTab : ComponentBase, IDisposable
{
    /// <summary>
    /// Lista blanca de los parámetros de VISTA de la URL: lo que guarda un filtro guardado y lo que recuerda la vista
    /// recordada, junto con la búsqueda libre (<c>q</c>). «Pestana» no entra: recargar abre Catálogo (DDL-080).
    /// </summary>
    public static readonly IReadOnlyList<string> ParametrosDeVista = ["q", "ambito", "formato", "estado", "alcance", "plantilla", "trabajador"];

    private const string AlcanceOrganizacion = "org";
    private const string PrefijoCentro = "centro:";
    private const string PrefijoCliente = "cliente:";

    private IReadOnlyList<PlantillaDocumentoListaDto> _plantillas = [];
    private Dictionary<Guid, string> _nombresCentro = [];
    private Dictionary<Guid, string> _nombresCliente = [];
    private bool _cargando = true;
    private bool _errorCarga;
    private int _avisosPendientes;

    private readonly ConexionFiltrosGuardados _filtrosGuardados = new();
    private readonly ConexionVistaRecordada _vistaRecordada = new();

    // Filtros de la vista (todos viajan en la URL, ver ParametrosDeVista). Vacío = sin filtro.
    private string _busqueda = string.Empty;
    private string _ambito = string.Empty;
    private string _formato = string.Empty;
    private string _estado = string.Empty;
    private string _alcance = string.Empty;
    private string _plantillaFiltro = string.Empty;
    private string _trabajadorFiltro = string.Empty;

    // Lo que el panel de Generados devuelve para pintar la barra: sus Trabajadores y los recuentos por estado.
    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresGenerados = [];
    private IReadOnlyDictionary<string, int> _recuentosGenerados = new Dictionary<string, int>();
    private int _totalSinEstadoGenerados;

    /// <summary>
    /// Número de la última carga del catálogo. Se captura ANTES del
    /// <c>await</c> y, al volver, solo se escribe estado si sigue siendo la
    /// vigente y la pestaña sigue viva. Mismo patrón que Empresas.
    /// </summary>
    private int _cargaVigente;

    /// <summary>
    /// Se cancela al retirarse la pestaña (salir de la página): la consulta en curso deja de trabajar
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
        Navigation.LocationChanged -= AlCambiarLaUbicacion;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    /// <summary>La respuesta es de la carga vigente y la pestaña sigue viva.</summary>
    private bool EsVigente(int carga) => !_desechado && carga == _cargaVigente;

    /// <summary>
    /// Sub-pestaña que pide el padre (Catálogo/Generados) — ver
    /// <see cref="OnParametersSet"/> para cuándo se aplica. La página
    /// standalone /plantillas (Plantillas.razor.cs) la recalcula en cada
    /// render desde su propio query string ?Pestana=.
    /// </summary>
    [Parameter] public string PestanaInicial { get; set; } = "catalogo";

    /// <summary>Se invoca cada vez que cambia la vista: Plantillas.razor la escucha para reflejarla en su URL.</summary>
    [Parameter] public EventCallback<string> PestanaActivaChanged { get; set; }

    /// <summary>Cuántas plantillas tiene el catálogo (sin filtrar): la cifra junto al título de la página.</summary>
    [Parameter] public EventCallback<int> TotalCambiado { get; set; }

    private string _pestanaActiva = "catalogo";
    private string? _pestanaInicialAplicada;

    /// <summary>
    /// Hallazgo de revisión adversarial de Codex: aplicar PestanaInicial solo
    /// en OnInitialized deja de resincronizarse si el query string de una
    /// instancia YA viva de /plantillas cambia por una vía distinta al propio
    /// clic de este componente (atrás/adelante del navegador, otro enlace a
    /// ?Pestana=generados). Reaplicar solo cuando el valor CAMBIA (no en cada
    /// render) reproduce ese comportamiento sin pisar la vista elegida aquí.
    /// </summary>
    protected override void OnParametersSet()
    {
        if (PestanaInicial != _pestanaInicialAplicada)
        {
            _pestanaInicialAplicada = PestanaInicial;
            _pestanaActiva = PestanaInicial;
        }

        SincronizarConLaUrl();
    }

    protected override void OnInitialized() => Navigation.LocationChanged += AlCambiarLaUbicacion;

    /// <summary>
    /// Atrás/adelante del navegador, una vista guardada o un enlace con filtros sobre la pantalla ya abierta: la URL es la
    /// fuente de verdad de los filtros. Solo vale la dirección de esta pantalla: al salir a otra no hay nada que leer.
    /// </summary>
    private void AlCambiarLaUbicacion(object? sender, LocationChangedEventArgs e)
    {
        if (_desechado || !new Uri(e.Location).AbsolutePath.TrimEnd('/').EndsWith("/plantillas", StringComparison.OrdinalIgnoreCase))
            return;

        _ = InvokeAsync(() =>
        {
            if (_desechado)
                return;

            SincronizarConLaUrl();
            StateHasChanged();
        });
    }

    private void SincronizarConLaUrl()
    {
        var consulta = QueryHelpers.ParseQuery(new Uri(Navigation.Uri).Query);
        string Leer(string nombre) => consulta.TryGetValue(nombre, out var valor) ? valor.ToString() : string.Empty;

        AplicarVista(ParametrosDeVista.ToDictionary(p => p, p => (string?)Leer(p)));
    }

    /// <summary>
    /// La vista (de la URL o de un filtro guardado) pasa por la misma validación: lo que no es de esta pantalla o de esta
    /// vista se quita en vez de filtrar por nada.
    /// </summary>
    private void AplicarVista(IReadOnlyDictionary<string, string?> vista)
    {
        string Valor(string nombre) => vista.GetValueOrDefault(nombre) ?? string.Empty;

        _busqueda = Valor("q");
        _ambito = Enum.TryParse<AmbitoAplicacion>(Valor("ambito"), out var ambito) && Enum.IsDefined(ambito) ? ambito.ToString() : string.Empty;
        _formato = Enum.TryParse<FormatoOrigenPlantilla>(Valor("formato"), out var formato) && Enum.IsDefined(formato) ? formato.ToString() : string.Empty;
        _estado = EstadosValidos(Valor("estado"));
        _alcance = AlcanceValido(Valor("alcance"));
        _plantillaFiltro = Guid.TryParse(Valor("plantilla"), out var plantilla) ? plantilla.ToString() : string.Empty;
        _trabajadorFiltro = Guid.TryParse(Valor("trabajador"), out var trabajador) ? trabajador.ToString() : string.Empty;

        // Catálogo y Generados comparten el menú de filtros guardados y la vista recordada, pero no los filtros: lo que es
        // de la otra vista se descarta, para no dejar un filtro oculto que no se ve ni se puede quitar.
        if (_pestanaActiva == "catalogo")
            _plantillaFiltro = _trabajadorFiltro = string.Empty;
        else
            _ambito = _formato = _alcance = string.Empty;

        _idEnfocado = null;
    }

    /// <summary>Solo vale «organizacion» o «centro:/cliente:» con un Guid; se devuelve normalizado (el Guid en su forma canónica).</summary>
    private static string AlcanceValido(string valor)
    {
        if (valor == AlcanceOrganizacion)
            return valor;

        foreach (var prefijo in new[] { PrefijoCentro, PrefijoCliente })
        {
            if (valor.StartsWith(prefijo, StringComparison.Ordinal) && Guid.TryParse(valor[prefijo.Length..], out var id))
                return prefijo + id;
        }

        return string.Empty;
    }

    /// <summary>Los estados de código que admite la vista activa: los de la última versión del catálogo o los del documento generado.</summary>
    private IReadOnlyList<string> EstadosAdmitidos =>
        _pestanaActiva == "catalogo"
            ? [nameof(EstadoConfiguracionPlantilla.Borrador), nameof(EstadoConfiguracionPlantilla.PendienteRevision), nameof(EstadoConfiguracionPlantilla.Confirmada)]
            : [nameof(EstadoDocumentoGenerado.Generado), nameof(EstadoDocumentoGenerado.GeneradoConAvisos)];

    private string EstadosValidos(string? valor) =>
        SeleccionEstados.Unir(SeleccionEstados.Separar(valor).Where(EstadosAdmitidos.Contains)) ?? string.Empty;

    private void EscribirUrl() =>
        Navigation.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["q"] = _busqueda,
            ["ambito"] = _ambito,
            ["formato"] = _formato,
            ["estado"] = _estado,
            ["alcance"] = _alcance,
            ["plantilla"] = _plantillaFiltro,
            ["trabajador"] = _trabajadorFiltro
        });

    private async Task CambiarVistaAsync(string vista)
    {
        if (vista == _pestanaActiva)
            return;

        // Los filtros de una vista no son los de la otra: se conserva la búsqueda y se quita lo demás.
        _pestanaActiva = vista;
        _ambito = _formato = _estado = _alcance = _plantillaFiltro = _trabajadorFiltro = string.Empty;
        _idEnfocado = null;
        EscribirUrl();
        await PestanaActivaChanged.InvokeAsync(vista);
    }

    /// <summary>
    /// El badge de "Generados" no es la cardinalidad de la vista (cuántos
    /// documentos generados hay): es "N documentos generados con avisos,
    /// pendientes de revisar" (Plantillas TALVEG.dc.html). Se omite en cero
    /// para no leerse como una alarma vacía — "Generados" a secas basta
    /// cuando no hay nada que revisar.
    /// </summary>
    private string EtiquetaGenerados => _avisosPendientes > 0 ? $"Generados ({_avisosPendientes} con avisos)" : "Generados";

    private IReadOnlyList<(string Id, string Texto)> Vistas =>
        [("catalogo", $"Catálogo ({_plantillas.Count})"), ("generados", EtiquetaGenerados)];

    // ------------------------------------------------------------------ Filtros

    private string PlaceholderBuscador => _pestanaActiva == "catalogo"
        ? Textos["PlantillasBuscadorCatalogo"].Value
        : Textos["PlantillasBuscadorGenerados"].Value;

    private bool HayFiltrosActivos => !string.IsNullOrWhiteSpace(_busqueda) || !string.IsNullOrEmpty(_estado)
        || (_pestanaActiva == "catalogo"
            ? !string.IsNullOrEmpty(_ambito) || !string.IsNullOrEmpty(_formato) || !string.IsNullOrEmpty(_alcance)
            : !string.IsNullOrEmpty(_plantillaFiltro) || !string.IsNullOrEmpty(_trabajadorFiltro));

    private static Guid? IdDeFiltro(string valor) => Guid.TryParse(valor, out var id) ? id : null;

    private Task BuscarAsync(string valor) => CambiarFiltroAsync(() => _busqueda = valor);

    private Task CambiarAmbitoAsync(string valor) => CambiarFiltroAsync(() => _ambito = valor);

    private Task CambiarFormatoAsync(string valor) => CambiarFiltroAsync(() => _formato = valor);

    private Task CambiarAlcanceAsync(string valor) => CambiarFiltroAsync(() => _alcance = AlcanceValido(valor));

    private Task CambiarPlantillaFiltroAsync(string valor) => CambiarFiltroAsync(() => _plantillaFiltro = valor);

    private Task CambiarTrabajadorFiltroAsync(string valor) => CambiarFiltroAsync(() => _trabajadorFiltro = valor);

    private Task CambiarEstadoAsync(string? valor) => CambiarFiltroAsync(() => _estado = EstadosValidos(valor));

    private Task CambiarFiltroAsync(Action cambio)
    {
        cambio();
        _idEnfocado = null;
        EscribirUrl();
        return Task.CompletedTask;
    }

    /// <summary>Quita todos los filtros de la vista en una sola navegación.</summary>
    private Task LimpiarFiltrosAsync() => CambiarFiltroAsync(() =>
        _busqueda = _ambito = _formato = _estado = _alcance = _plantillaFiltro = _trabajadorFiltro = string.Empty);

    /// <summary>Un filtro guardado define la vista entera: lo que no trae se quita.</summary>
    private Task AplicarVistaGuardadaAsync(IReadOnlyDictionary<string, string?> vista)
    {
        AplicarVista(vista);
        EscribirUrl();
        return Task.CompletedTask;
    }

    private static bool Contiene(string? texto, string termino) =>
        texto is not null
        && CultureInfo.CurrentCulture.CompareInfo.IndexOf(texto, termino, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    private bool CoincideAlcance(PlantillaDocumentoListaDto p) => _alcance switch
    {
        "" => true,
        AlcanceOrganizacion => p.CentroId is null && p.ClienteEmpresarialId is null,
        var a when a.StartsWith(PrefijoCentro, StringComparison.Ordinal) => p.CentroId?.ToString() == a[PrefijoCentro.Length..],
        var a => p.ClienteEmpresarialId?.ToString() == a[PrefijoCliente.Length..]
    };

    /// <summary>El catálogo con todos los filtros menos el de estado: la lista a la que la franja cuenta sus botones.</summary>
    private IEnumerable<PlantillaDocumentoListaDto> SinFiltroDeEstado =>
        _plantillas.Where(p =>
            (string.IsNullOrWhiteSpace(_busqueda) || Contiene(p.Nombre, _busqueda.Trim()))
            && (_ambito.Length == 0 || p.AmbitoAplicacion.ToString() == _ambito)
            && (_formato.Length == 0 || p.FormatoOrigen.ToString() == _formato)
            && CoincideAlcance(p));

    private IReadOnlyList<PlantillaDocumentoListaDto> Visibles
    {
        get
        {
            var estados = SeleccionEstados.Separar(_estado);
            return SinFiltroDeEstado.Where(p => estados.Count == 0 || estados.Contains(p.UltimaVersionEstado.ToString())).ToList();
        }
    }

    private IReadOnlyDictionary<string, int> RecuentosCatalogo =>
        SinFiltroDeEstado.GroupBy(p => p.UltimaVersionEstado.ToString()).ToDictionary(g => g.Key, g => g.Count());

    private int TotalSinEstadoCatalogo => SinFiltroDeEstado.Count();

    private IReadOnlyList<OpcionFranjaEstado> OpcionesEstadoCatalogo =>
    [
        new("Confirmada", TonoBadge.Exito, nameof(EstadoConfiguracionPlantilla.Confirmada)),
        new("Pendiente de revisión", TonoBadge.Advertencia, nameof(EstadoConfiguracionPlantilla.PendienteRevision)),
        new("Borrador", TonoBadge.Neutro, nameof(EstadoConfiguracionPlantilla.Borrador))
    ];

    private IReadOnlyList<OpcionFranjaEstado> OpcionesEstadoGenerados =>
    [
        new(Textos["PlantillasEstadoGenerado"], TonoBadge.Exito, nameof(EstadoDocumentoGenerado.Generado)),
        new(Textos["PlantillasEstadoGeneradoConAvisos"], TonoBadge.Advertencia, nameof(EstadoDocumentoGenerado.GeneradoConAvisos))
    ];

    private IReadOnlyList<OpcionEstado> OpcionesAmbito =>
        _plantillas.Select(p => p.AmbitoAplicacion).Distinct()
            .Select(a => new OpcionEstado(a.ToString(), TextoAmbito(a)))
            .OrderBy(o => o.Texto, StringComparer.CurrentCulture).ToList();

    private static IReadOnlyList<OpcionEstado> OpcionesFormato =>
    [
        new(nameof(FormatoOrigenPlantilla.PdfConCampos), TextoFormato(FormatoOrigenPlantilla.PdfConCampos)),
        new(nameof(FormatoOrigenPlantilla.PdfVisual), TextoFormato(FormatoOrigenPlantilla.PdfVisual))
    ];

    /// <summary>«Toda la organización» y, de las plantillas que hay, cada Centro y cada Cliente empresarial al que se atan.</summary>
    private IReadOnlyList<OpcionEstado> OpcionesAlcance
    {
        get
        {
            var opciones = new List<OpcionEstado>();
            if (_plantillas.Any(p => p.CentroId is null && p.ClienteEmpresarialId is null))
                opciones.Add(new(AlcanceOrganizacion, TextoOrganizacion));

            opciones.AddRange(_plantillas.Where(p => p.CentroId is not null).Select(p => p.CentroId!.Value).Distinct()
                .Select(id => new OpcionEstado($"{PrefijoCentro}{id}", NombreCentro(id)))
                .OrderBy(o => o.Texto, StringComparer.CurrentCulture));
            opciones.AddRange(_plantillas.Where(p => p.ClienteEmpresarialId is not null).Select(p => p.ClienteEmpresarialId!.Value).Distinct()
                .Select(id => new OpcionEstado($"{PrefijoCliente}{id}", NombreCliente(id)))
                .OrderBy(o => o.Texto, StringComparer.CurrentCulture));
            return opciones;
        }
    }

    private IReadOnlyList<OpcionEstado> OpcionesPlantilla =>
        _plantillas.Select(p => new OpcionEstado(p.Id.ToString(), p.Nombre)).ToList();

    private IReadOnlyList<OpcionEstado> OpcionesTrabajador =>
        EtiquetasSelectorTrabajador.Construir(_trabajadoresGenerados).Select(t => new OpcionEstado(t.Id.ToString(), t.Texto)).ToList();

    private void RecibirDatosGenerados(DatosFiltrosGenerados datos)
    {
        _trabajadoresGenerados = datos.Trabajadores;
        _recuentosGenerados = datos.Recuentos;
        _totalSinEstadoGenerados = datos.TotalSinEstado;
        StateHasChanged();
    }

    /// <summary>
    /// Por qué no hay filas con los filtros puestos. Nombra solo lo que está puesto: decir «con X en el nombre» sin
    /// búsqueda daría por escrito algo que nadie pidió.
    /// </summary>
    private string DescripcionVacioPorFiltro
    {
        get
        {
            var partes = new List<string> { Textos["PlantillasVacioBase"].Value };
            if (!string.IsNullOrEmpty(_estado))
                partes.Add(string.Format(CultureInfo.CurrentCulture, Textos["PlantillasVacioEstado"].Value,
                    string.Join(" o ", SeleccionEstados.Separar(_estado).Select(e => TextoEstado(Enum.Parse<EstadoConfiguracionPlantilla>(e))))));
            if (!string.IsNullOrWhiteSpace(_busqueda))
                partes.Add(string.Format(CultureInfo.CurrentCulture, Textos["PlantillasVacioBusqueda"].Value, _busqueda.Trim()));
            if (_ambito.Length > 0 || _formato.Length > 0 || _alcance.Length > 0)
                partes.Add(Textos[string.IsNullOrEmpty(_estado) && string.IsNullOrWhiteSpace(_busqueda) ? "PlantillasVacioFiltrosPuestos" : "PlantillasVacioDemasFiltros"].Value);
            return string.Join(" ", partes) + ".";
        }
    }

    // ------------------------------------------------------------------ Nombres del alcance

    private string TextoOrganizacion => Textos["PlantillasTodaLaOrganizacion"].Value;

    private string NombreCentro(Guid id) => _nombresCentro.GetValueOrDefault(id) ?? "Centro";

    private string NombreCliente(Guid id) => _nombresCliente.GetValueOrDefault(id) ?? "Cliente empresarial";

    /// <summary>Segunda línea del nombre (artboard): el alcance de la plantilla y su versión («Toda la organización · v3»).</summary>
    private string TextoAlcance(PlantillaDocumentoListaDto p)
    {
        var alcance = (p.CentroId, p.ClienteEmpresarialId) switch
        {
            (null, null) => TextoOrganizacion,
            ({ } centro, null) => NombreCentro(centro),
            (null, { } cliente) => NombreCliente(cliente),
            ({ } centro, { } cliente) => $"{NombreCentro(centro)} · {NombreCliente(cliente)}"
        };

        return p.UltimaVersionNumero > 0 ? $"{alcance} · v{p.UltimaVersionNumero}" : alcance;
    }

    // Subir nueva versión (PR10) — el gestor decide manualmente que este PDF
    // sustituye al de una plantilla ya existente (ADR-010 § 4, § 6).
    private Guid? _plantillaParaNuevaVersionId;
    private string? _plantillaParaNuevaVersionNombre;
    private byte[]? _archivoNuevaVersion;
    private string? _nombreArchivoNuevaVersion;
    private bool _subiendoNuevaVersion;

    // La última subida terminó con error: el kit no enseña el «Hecho» de un guardado bueno.
    private bool _subidaNuevaVersionFallida;

    private bool ModalNuevaVersionVisible => _plantillaParaNuevaVersionId is not null;

    /// <summary>P1-E2b: un PDF de nueva versión ya elegido y sin subir se pierde al cerrar, salir o cambiar de vista.</summary>
    private bool HayCambiosSinGuardar => ModalNuevaVersionVisible && _archivoNuevaVersion is { Length: > 0 };

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
            await TotalCambiado.InvokeAsync(plantillas.Count);
            await CargarNombresDelAlcanceAsync(plantillas, carga);
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
    /// Los nombres de los Centros y Clientes empresariales a los que se atan las plantillas, para la segunda línea y la
    /// pastilla Alcance. Solo se piden si alguna plantilla tiene alcance, y por las consultas de selector que ya respetan la
    /// Asignación de Cartera: si no se resuelve un nombre, la fila dice «Centro» o «Cliente empresarial» en vez de fallar.
    /// </summary>
    private async Task CargarNombresDelAlcanceAsync(IReadOnlyList<PlantillaDocumentoListaDto> plantillas, int carga)
    {
        try
        {
            if (plantillas.Any(p => p.CentroId is not null))
            {
                var centros = await Mediator.Send(new ObtenerCentrosParaSelectorQuery(), _ciclo.Token);
                if (EsVigente(carga))
                    _nombresCentro = centros.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Nombre);
            }

            if (plantillas.Any(p => p.ClienteEmpresarialId is not null))
            {
                var clientes = await Mediator.Send(new ObtenerClientesParaSelectorQuery(), _ciclo.Token);
                if (EsVigente(carga))
                    _nombresCliente = clientes.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().RazonSocial);
            }
        }
        catch (Exception) when (EsVigente(carga))
        {
            // Los nombres son un adorno de la segunda línea y de la pastilla: el catálogo se pinta igual, con «Centro» o
            // «Cliente empresarial» (y la cancelación al retirarse la pestaña sigue su camino).
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

    private static TonoBadge TonoEstado(EstadoConfiguracionPlantilla estado) => estado switch
    {
        EstadoConfiguracionPlantilla.Confirmada => TonoBadge.Exito,
        EstadoConfiguracionPlantilla.Borrador => TonoBadge.Neutro,
        _ => TonoBadge.Advertencia
    };

    private static string TextoFormato(FormatoOrigenPlantilla formato) =>
        formato == FormatoOrigenPlantilla.PdfConCampos ? "Campos AcroForm" : "Posición manual";

    /// <summary>Mismos rótulos que SelectorLoteDocumental: el enum no lleva tildes.</summary>
    private static string TextoAmbito(AmbitoAplicacion ambito) => ambito switch
    {
        AmbitoAplicacion.Vehiculo => "Vehículo",
        _ => ambito.ToString()
    };

    private void ManejarAtajo(string tecla)
    {
        var visibles = Visibles;
        if (visibles.Count == 0) return;

        var indiceActual = _idEnfocado is { } id ? visibles.ToList().FindIndex(p => p.Id == id) : -1;

        switch (tecla)
        {
            case "j":
                _idEnfocado = visibles[Math.Min(indiceActual + 1, visibles.Count - 1)].Id;
                break;
            case "k":
                _idEnfocado = visibles[Math.Max(indiceActual - 1, 0)].Id;
                break;
            case "Enter":
                if (indiceActual >= 0)
                    IrAConfigurar(visibles[indiceActual].UltimaVersionId);
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
        _subidaNuevaVersionFallida = false;
    }

    private void CerrarModalNuevaVersion(bool visible)
    {
        if (visible) return;
        _plantillaParaNuevaVersionId = null;
    }

    private async Task ManejarArchivoNuevaVersionAsync(InputFileChangeEventArgs e)
    {
        await using var flujo = e.File.OpenReadStream(LimitesArchivoSubido.TamanoMaximoBytes);
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
        _subidaNuevaVersionFallida = false;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new AgregarVersionPlantillaCommand(
                plantillaId, _archivoNuevaVersion, _nombreArchivoNuevaVersion ?? "plantilla.pdf"));

            if (resultado.EsFallido)
            {
                _subidaNuevaVersionFallida = true;
                Toasts.MostrarError(resultado.Error);
                return;
            }

            if (resultado.Valor.ArchivoIdenticoAVersionAnterior)
                Toasts.Mostrar("Este archivo es idéntico a la versión actual — probablemente no hacía falta subir nada nuevo.", TonoToast.Advertencia);

            // Subida: ya no hay nada pendiente, y la navegación al editor no pregunta (Codex, P1-E2b).
            _plantillaParaNuevaVersionId = null;
            _archivoNuevaVersion = null;
            Navigation.NavigateTo($"/plantillas/{resultado.Valor.PlantillaDocumentoVersionId}/editar");
        }
        finally
        {
            _subiendoNuevaVersion = false;
        }
    }
}
