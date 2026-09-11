using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Subcontratas.Commands.CrearSubcontrata;
using CaeManager.Application.Subcontratas.Commands.EliminarSubcontrata;
using CaeManager.Application.Subcontratas.Commands.EliminarSubcontratas;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentValidation;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Subcontratas.Pages;

public partial class Subcontratas : ComponentBase
{
    // Igual que Centros.razor.cs (Centro 360): QuickGrid no soporta filas
    // expandibles, así que la paginación se gestiona a mano — la Query sigue
    // paginando en servidor, solo cambia el control visual.
    private int _tamanoPagina = 20;

    private string _busqueda = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;
    private int _pagina = 1;

    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_totalElementos / (double)_tamanoPagina));

    private IReadOnlyList<ClienteSelectorDto> _clientesDisponibles = [];
    private IReadOnlyList<EmpresaSelectorDto> _empresasDisponibles = [];
    private IReadOnlyList<ElementoSeleccionable> _clientesDisponiblesSelector => _clientesDisponibles
        .Select(c => new ElementoSeleccionable(c.Id, c.RazonSocial))
        .ToList();
    private IReadOnlyList<ElementoSeleccionable> _empresasDisponiblesSelector => _empresasDisponibles
        .Select(e => new ElementoSeleccionable(e.Id, e.RazonSocial))
        .ToList();

    private bool _drawerVisible;
    private string _razonSocial = string.Empty;
    private string _cif = string.Empty;
    private HashSet<Guid> _clienteIdsSeleccionados = [];
    private HashSet<Guid> _empresaIdsSeleccionados = [];

    // DDL-076: en perfil Cliente Directo con una única Empresa, el selector
    // de Empresas no aparece — se marca en silencio. Mismo mecanismo que
    // Trabajadores.razor.cs, adaptado a la relación N:N de Subcontrata con
    // Empresa (aquí no hay "tipo de empleador" que alternar: una Subcontrata
    // siempre se relaciona con Empresas, nunca con una sola de forma exclusiva).
    private bool _resolverEmpresaEnSilencio;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private readonly HashSet<Guid> _seleccionados = [];
    private bool _seleccionMultiple;

    private void AlternarSeleccionMultiple(bool activa)
    {
        _seleccionMultiple = activa;
        if (!activa)
            _seleccionados.Clear();
    }

    /// <summary>Qué filas tienen el acordeón abierto — mismo criterio que Centros.razor.cs.</summary>
    private readonly HashSet<Guid> _expandidos = [];
    private List<SubcontrataListaDto> _elementosPagina = [];
    private Guid? _idEnfocado;
    private bool _eliminandoLote;
    private bool _confirmarEliminarLoteVisible;

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _razonSocialAEliminar = string.Empty;
    private bool _eliminando;

    // Vista previa (Subcontratas TALVEG.dc.html, mismo patrón que Empresas y
    // Vehículos): el nombre de la fila y "Detalles" abren esto primero, no el
    // Context Workspace directamente. Antes los dos abrían Subcontrata 360.
    private Guid? _previewSubcontrataId;
    private bool _previewVisible;

    /// <summary>
    /// La fila que la vista previa enseña, leída de la página ya cargada: el
    /// panel reutiliza el cumplimiento y los recuentos que la lista ya tiene,
    /// y se entera sola cuando <see cref="RefrescarSubcontrataAsync"/> los
    /// sustituye tras gestionar un documento desde el acordeón.
    /// </summary>
    private SubcontrataListaDto? FilaEnVistaPrevia =>
        _previewSubcontrataId is { } id ? _elementosPagina.FirstOrDefault(e => e.Id == id) : null;

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    /// <summary>Comando del palette "Crear subcontrata": /subcontratas?accion=crear abre el modal directamente — mismo patrón que Clientes/Empresas/Centros/Trabajadores/Documentos.</summary>
    [SupplyParameterFromQuery] public string? Accion { get; set; }

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private IValidator<CrearSubcontrataCommand> ValidadorCrear { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        _busqueda = TerminoBusquedaInicial ?? string.Empty;
        await CargarAsync();

        if (Accion == "crear")
            await AbrirCrear();
    }

    /// <summary>Se re-ejecuta en cada navegación dentro de la propia página — mismo criterio que Centros.razor.cs.</summary>
    protected override async Task OnParametersSetAsync()
    {
        var deLaUrl = TerminoBusquedaInicial ?? string.Empty;
        if (deLaUrl == _busqueda)
            return;

        _busqueda = deLaUrl;
        await CargarAsync(resetPagina: true);
    }

    private async Task CargarAsync(bool resetPagina = false)
    {
        if (resetPagina)
            _pagina = 1;

        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ObtenerSubcontratasQuery(
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                Pagina: _pagina,
                TamanoPagina: _tamanoPagina));

            _totalElementos = resultado.TotalElementos;
            _elementosPagina = resultado.Elementos.ToList();
            _seleccionados.Clear();
            _expandidos.Clear();
            _idEnfocado = null;

            // La vista previa se alimenta de la fila de la página: si esa fila
            // ya no está (otra página, otra búsqueda, eliminada), el panel no
            // tiene nada cierto que enseñar y se cierra.
            if (FilaEnVistaPrevia is null)
                _previewVisible = false;
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            _cargando = false;
            StateHasChanged();
        }
    }

    private Task CambiarPaginaAsync(int pagina)
    {
        _pagina = pagina;
        return CargarAsync();
    }

    private Task CambiarTamanoPaginaAsync(int tamano)
    {
        _tamanoPagina = tamano;
        return CargarAsync(resetPagina: true);
    }

    /// <summary>
    /// Tras gestionar un documento in situ desde el acordeón hace falta
    /// refrescar el cumplimiento/recuentos de ESA fila — CargarAsync()
    /// completo colapsaría el acordeón que el usuario acaba de usar (mismo
    /// motivo que RefrescarCentroAsync en Centros.razor.cs).
    /// </summary>
    private async Task RefrescarSubcontrataAsync(Guid subcontrataId)
    {
        var resultado = await Mediator.Send(new ObtenerSubcontratasQuery(Busqueda: null, SubcontrataId: subcontrataId));
        var actualizada = resultado.Elementos.FirstOrDefault();
        if (actualizada is null) return;

        var indice = _elementosPagina.FindIndex(s => s.Id == subcontrataId);
        if (indice >= 0)
            _elementosPagina[indice] = actualizada;

        StateHasChanged();
    }

    private async Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        NavigationManager.ActualizarFiltroEnUrl("q", valor);
        await CargarAsync(resetPagina: true);
    }

    private bool HayFiltrosActivos => !string.IsNullOrWhiteSpace(_busqueda);

    /// <summary>
    /// Limpia la búsqueda en memoria Y en la URL: el filtro vuelve por
    /// <see cref="OnParametersSetAsync"/> desde <c>?q=</c>, así que limpiar
    /// solo el campo dejaría que la siguiente pasada de parámetros lo
    /// repusiera. <see cref="BuscarAsync"/> ya hace las dos cosas.
    /// </summary>
    private Task LimpiarFiltrosAsync() => BuscarAsync(string.Empty);

    /// <summary>
    /// «N de M» de la barra de herramientas. M es el total que devuelve la
    /// consulta, que con búsqueda ya viene filtrado — por eso la frase lo dice,
    /// y no promete cuántas hay sin filtro, que esta pantalla no sabe.
    /// </summary>
    private string TextoConteo =>
        $"{_elementosPagina.Count} de {_totalElementos} {(_totalElementos == 1 ? "subcontrata" : "subcontratas")}"
        + (HayFiltrosActivos ? " con esta búsqueda" : string.Empty);

    private string ClaseRejilla(string claseBase) =>
        $"{claseBase} rejilla-subcontratas" + (_seleccionMultiple ? " rejilla-subcontratas-seleccion" : string.Empty);

    private string ClaseTarjeta(Guid id) =>
        "tarjeta-fila-acordeon"
        + (id == _idEnfocado ? " fila-enfocada" : string.Empty)
        + (_previewVisible && id == _previewSubcontrataId ? " fila-en-vista-previa" : string.Empty);

    private void AbrirPreview(Guid id)
    {
        _previewSubcontrataId = id;
        _previewVisible = true;
    }

    private Task AbrirDesdePreviewAsync((Guid Id, string Pestana) destino)
    {
        var nombre = _elementosPagina.FirstOrDefault(e => e.Id == destino.Id)?.RazonSocial ?? string.Empty;
        return WorkspaceService.AbrirAsync(EntidadWorkspace.Subcontrata, destino.Id, nombre, destino.Pestana);
    }

    private async Task AbrirCrear()
    {
        _clientesDisponibles = await Mediator.Send(new ObtenerClientesParaSelectorQuery());
        _empresasDisponibles = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery());

        var perfil = await Mediator.Send(new ObtenerPerfilVocabularioActualQuery());
        _resolverEmpresaEnSilencio = perfil == PerfilVocabularioTenant.ClienteDirecto && _empresasDisponibles.Count == 1;

        _razonSocial = string.Empty;
        _cif = string.Empty;
        _clienteIdsSeleccionados = [];
        _empresaIdsSeleccionados = _resolverEmpresaEnSilencio ? [_empresasDisponibles[0].Id] : [];
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private void AlternarCliente(Guid clienteId, bool seleccionado)
    {
        if (seleccionado)
            _clienteIdsSeleccionados.Add(clienteId);
        else
            _clienteIdsSeleccionados.Remove(clienteId);
    }

    private void AlternarEmpresa(Guid empresaId, bool seleccionado)
    {
        if (seleccionado)
            _empresaIdsSeleccionados.Add(empresaId);
        else
            _empresaIdsSeleccionados.Remove(empresaId);
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        return Task.CompletedTask;
    }

    private async Task GuardarAsync()
    {
        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();

        try
        {
            var clienteIds = _clienteIdsSeleccionados.ToList();
            var empresaIds = _empresaIdsSeleccionados.ToList();
            var cif = string.IsNullOrWhiteSpace(_cif) ? null : _cif;

            var resultado = await Mediator.Send(new CrearSubcontrataCommand(_razonSocial, cif, clienteIds, empresaIds));
            if (resultado.EsFallido)
            {
                _mensajeErrorFormulario = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Subcontrata creada correctamente.", TonoToast.Exito);
            _drawerVisible = false;
            await CargarAsync();
        }
        catch (ValidationException ex)
        {
            _erroresCampo = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _mensajeErrorFormulario = "No pudimos guardar los cambios. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardando = false;
        }
    }

    private string? ObtenerError(string campo) => _erroresCampo.GetValueOrDefault(campo);

    /// <summary>
    /// Validación inline al salir del campo (mismo patrón que Centros.razor,
    /// UX_PATTERNS.md, P1-18 de docs/business/MATURITY_REVIEW.md).
    /// </summary>
    private Task ValidarRazonSocialAsync() => ValidarCampoAsync(nameof(CrearSubcontrataCommand.RazonSocial));

    private Task ValidarCifAsync() => ValidarCampoAsync(nameof(CrearSubcontrataCommand.Cif));

    private async Task ValidarCampoAsync(string campo)
    {
        var cif = string.IsNullOrWhiteSpace(_cif) ? null : _cif;
        var resultado = await ValidadorCrear.ValidateAsync(
            new CrearSubcontrataCommand(_razonSocial, cif, _clienteIdsSeleccionados.ToList(), _empresaIdsSeleccionados.ToList()),
            opciones => opciones.IncludeProperties(campo));

        if (resultado.IsValid)
            _erroresCampo.Remove(campo);
        else
            _erroresCampo[campo] = resultado.Errors[0].ErrorMessage;
    }

    private bool TodosSeleccionados =>
        _elementosPagina.Count > 0 && _elementosPagina.All(e => _seleccionados.Contains(e.Id));

    private bool TodosExpandidos =>
        _elementosPagina.Count > 0 && _elementosPagina.All(e => _expandidos.Contains(e.Id));

    private void AlternarExpansion(Guid id)
    {
        if (!_expandidos.Add(id))
            _expandidos.Remove(id);
    }

    private void AlternarTodosExpandidos(bool expandir)
    {
        if (expandir)
            foreach (var elemento in _elementosPagina) _expandidos.Add(elemento.Id);
        else
            _expandidos.Clear();
    }

    private void AlternarSeleccionTodos(bool marcar)
    {
        if (marcar)
            foreach (var elemento in _elementosPagina) _seleccionados.Add(elemento.Id);
        else
            _seleccionados.Clear();
    }

    private void AlternarSeleccion(Guid id, bool marcado)
    {
        if (marcado) _seleccionados.Add(id);
        else _seleccionados.Remove(id);
    }

    private void AbrirEliminar(Guid id, string razonSocial)
    {
        _idAEliminar = id;
        _razonSocialAEliminar = razonSocial;
        _confirmarEliminarVisible = true;
    }

    /// <summary>
    /// Eliminación de una sola fila desde su menú, con el mismo comando que
    /// ya existía (EliminarSubcontrataCommand) y que la lista no usaba. Sin
    /// "Deshacer": no hay comando de restauración de subcontratas, y ofrecer
    /// un botón sin camino detrás sería prometer algo que no ocurre. El motivo
    /// de un rechazo —p. ej. que aún tenga trabajadores— lo da el propio
    /// comando y se enseña tal cual.
    /// </summary>
    private async Task ConfirmarEliminarAsync()
    {
        _eliminando = true;

        try
        {
            var resultado = await Mediator.Send(new EliminarSubcontrataCommand(_idAEliminar));
            _confirmarEliminarVisible = false;

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Subcontrata eliminada correctamente.", TonoToast.Exito);
            await CargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar la subcontrata. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }

    private async Task ConfirmarEliminarLoteAsync()
    {
        _eliminandoLote = true;

        try
        {
            var resultado = await Mediator.Send(new EliminarSubcontratasCommand(_seleccionados.ToList()));
            var dto = resultado.Valor;

            ToastService.Mostrar(
                dto.Errores.Count == 0
                    ? $"{dto.Eliminados} subcontrata(s) eliminada(s)."
                    : $"{dto.Eliminados} eliminada(s). {dto.Errores.Count} no se pudieron borrar: {string.Join(" ", dto.Errores)}",
                dto.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);

            _seleccionados.Clear();
            _confirmarEliminarLoteVisible = false;
            await CargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar las subcontratas seleccionadas. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _eliminandoLote = false;
        }
    }

    private Task ManejarAtajoAsync(string tecla)
    {
        if (_elementosPagina.Count == 0) return Task.CompletedTask;

        switch (tecla)
        {
            case "j":
                {
                    var indiceActual = _idEnfocado is null ? -1 : _elementosPagina.FindIndex(e => e.Id == _idEnfocado);
                    _idEnfocado = _elementosPagina[Math.Min(indiceActual + 1, _elementosPagina.Count - 1)].Id;
                    break;
                }
            case "k":
                {
                    var indiceActual = _idEnfocado is null ? 0 : _elementosPagina.FindIndex(e => e.Id == _idEnfocado);
                    _idEnfocado = _elementosPagina[Math.Max(indiceActual - 1, 0)].Id;
                    break;
                }
            case "x":
                if (_idEnfocado is { } idAlternar)
                    AlternarSeleccion(idAlternar, !_seleccionados.Contains(idAlternar));
                break;
            case "Enter":
                // Enter hace lo mismo que pulsar el nombre de la fila: abrir la
                // vista previa. Mismo criterio que Empresas.
                if (_idEnfocado is { } idAbrir)
                    AbrirPreview(idAbrir);
                break;
        }

        StateHasChanged();
        return Task.CompletedTask;
    }

    /// <summary>Nombre accesible de un badge de recuento — mismo criterio que Centros.razor.cs.DescribirRecuento.</summary>
    private static string DescribirRecuento(IReadOnlyList<IncidenciaSubcontrataDto> incidencias, string calificativo) =>
        incidencias.Count == 1
            ? $"1 documento {calificativo}"
            : $"{incidencias.Count} documentos {(calificativo.EndsWith('o') ? calificativo + "s" : calificativo)}";

    /// <summary>Texto visible del badge de recuento: «1 vencido», «3 próximos».</summary>
    private static string TextoRecuento(int total, string singular, string plural) =>
        $"{total} {(total == 1 ? singular : plural)}";

    /// <summary>
    /// Nombre accesible del anillo. Antes se interpolaba el porcentaje sin
    /// mirar si existía, y una subcontrata sin universo de requisitos se
    /// anunciaba como «% de cumplimiento…» — un número que no hay. Null
    /// significa que ningún trabajador tiene un documento exigido por un
    /// centro activo (ver <see cref="SubcontrataListaDto"/>).
    /// </summary>
    private static string EtiquetaCumplimiento(int? porcentaje) =>
        porcentaje is { } p
            ? $"{p}% de cumplimiento — documentos al día / exigidos entre sus trabajadores"
            : "Sin trabajadores con documentos exigidos por algún centro activo";

    /// <summary>
    /// Qué significa cada nivel de servicio, con las mismas palabras que el
    /// panel de Subcontrata 360. El mockup decía «TALVEG gestiona su
    /// documentación», y eso atribuye a la plataforma el papel de Operador
    /// CAE: quien gestiona es la organización que opera el tenant, no TALVEG.
    /// </summary>
    private static string DescribirNivel(NivelServicioSubcontrata nivel) => nivel switch
    {
        NivelServicioSubcontrata.Supervisada =>
            "Supervisada: no se gestionan sus documentos — solo se audita su cumplimiento en las plataformas del titular de cada centro.",
        _ => "Gestionada: su documentación se sube y se valida en esta plataforma."
    };
}
