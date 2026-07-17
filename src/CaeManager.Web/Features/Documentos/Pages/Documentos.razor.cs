using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Commands.EliminarDocumento;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculosParaSelector;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Documentos;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.QuickGrid;

namespace CaeManager.Web.Features.Documentos.Pages;

public partial class Documentos : ComponentBase
{
    private const long TamanoMaximoArchivoBytes = 10 * 1024 * 1024;
    private const int MaximoArchivosPorSubida = 20;

    /// <summary>
    /// Permite llegar aquí desde Alertas o Calendario con un documento
    /// concreto ya listo para gestionar (p. ej. "/documentos?documentoId=...")
    /// en vez de obligar a buscarlo manualmente en la lista.
    /// </summary>
    [SupplyParameterFromQuery] public Guid? DocumentoId { get; set; }

    /// <summary>
    /// Permite llegar aquí desde el Dashboard con el filtro de Estado ya
    /// aplicado (p. ej. la tarjeta KPI "Vigentes" enlaza a
    /// "/documentos?estado=Vigente").
    /// </summary>
    [SupplyParameterFromQuery] public string? Estado { get; set; }

    protected override async Task OnInitializedAsync()
    {
        if (!string.IsNullOrWhiteSpace(Estado) && Enum.TryParse<EstadoDocumento>(Estado, out _))
            _estadoFiltro = Estado;

        if (DocumentoId is not null)
            await AbrirEditarAsync(DocumentoId.Value);
    }

    private readonly PaginationState _paginacion = new() { ItemsPerPage = 20 };
    private QuickGrid<DocumentoListaDto>? _grid;

    private string _busqueda = string.Empty;
    private string _ambitoFiltro = string.Empty;
    private string _estadoFiltro = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresDisponibles = [];
    private IReadOnlyList<ClienteSelectorDto> _clientesDisponibles = [];
    private IReadOnlyList<EmpresaSelectorDto> _empresasDisponibles = [];
    private IReadOnlyList<VehiculoSelectorDto> _vehiculosDisponibles = [];
    private IReadOnlyList<TipoDocumentoListaDto> _tiposDisponibles = [];

    private bool _drawerVisible;
    private Guid? _editandoId;
    private string _ambitoAplicacion = nameof(AmbitoAplicacion.Trabajador);
    private string _trabajadorId = string.Empty;
    private string _clienteId = string.Empty;
    private string _empresaId = string.Empty;
    private string _vehiculoId = string.Empty;
    private string _propietarioNombreSoloLectura = string.Empty;
    private string _tipoDocumentoId = string.Empty;
    private string _tipoDocumentoNombreSoloLectura = string.Empty;
    private bool _tipoDocumentoAplicaVencimientoAutomaticoEdit;
    private string _fechaEmision = string.Empty;
    private DateOnly? _fechaEmisionOriginal;
    private string _fechaVencimientoManual = string.Empty;
    private string? _glosarioDescripcion;
    private string? _glosarioCriteriosValidacion;
    private string? _glosarioSeSolicitaA;
    private string? _glosarioObservaciones;
    private string? _archivoUrl;
    private bool _subiendoArchivo;
    private string _comentarios = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _propietarioAEliminar = string.Empty;
    private string _tipoDocumentoAEliminar = string.Empty;
    private bool _eliminando;

    private bool _confirmarVigenciaAnteriorVisible;
    private bool _procesandoConfirmacionVigencia;

    /// <summary>
    /// Solo los tipos de documento sin vencimiento automático piden una
    /// fecha de vencimiento a mano — los automáticos la calculan siempre a
    /// partir de la vigencia en meses, así que no tiene sentido mostrarles
    /// el campo ni el botón de copiar.
    /// </summary>
    private bool RequiereVencimientoManual =>
        _editandoId is null
            ? _tiposDisponibles.FirstOrDefault(t => t.Id.ToString() == _tipoDocumentoId) is { AplicaVencimientoAutomatico: false }
            : !_tipoDocumentoAplicaVencimientoAutomaticoEdit;

    private bool TieneGlosario =>
        !string.IsNullOrWhiteSpace(_glosarioDescripcion)
        || !string.IsNullOrWhiteSpace(_glosarioCriteriosValidacion)
        || !string.IsNullOrWhiteSpace(_glosarioSeSolicitaA)
        || !string.IsNullOrWhiteSpace(_glosarioObservaciones);

    private async ValueTask<GridItemsProviderResult<DocumentoListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<DocumentoListaDto> request)
    {
        _cargando = true;
        _errorCarga = false;

        try
        {
            var pagina = (request.StartIndex / _paginacion.ItemsPerPage) + 1;

            var ambitoFiltro = Enum.TryParse<AmbitoAplicacion>(_ambitoFiltro, out var ambito) ? ambito : (AmbitoAplicacion?)null;
            var estadoFiltro = Enum.TryParse<EstadoDocumento>(_estadoFiltro, out var estado) ? estado : (EstadoDocumento?)null;

            var resultado = await Mediator.Send(new ObtenerDocumentosQuery(
                TrabajadorId: null,
                Ambito: ambitoFiltro,
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                Estado: estadoFiltro,
                Pagina: pagina,
                TamanoPagina: _paginacion.ItemsPerPage));

            _totalElementos = resultado.TotalElementos;

            return GridItemsProviderResult.From(resultado.Elementos.ToList(), resultado.TotalElementos);
        }
        catch (Exception)
        {
            _errorCarga = true;
            return GridItemsProviderResult.From(new List<DocumentoListaDto>(), 0);
        }
        finally
        {
            _cargando = false;
            StateHasChanged();
        }
    }

    private async Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        await RecargarAsync();
    }

    private async Task CambiarAmbitoFiltroAsync(string valor)
    {
        _ambitoFiltro = valor;
        await RecargarAsync();
    }

    private async Task CambiarEstadoFiltroAsync(string valor)
    {
        _estadoFiltro = valor;
        await RecargarAsync();
    }

    private async Task RecargarAsync()
    {
        await _paginacion.SetCurrentPageIndexAsync(0);

        if (_grid is not null)
            await _grid.RefreshDataAsync();

        StateHasChanged();
    }

    private async Task AbrirCrearAsync()
    {
        _ambitoAplicacion = nameof(AmbitoAplicacion.Trabajador);
        _trabajadoresDisponibles = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery());
        _tiposDisponibles = await Mediator.Send(new ObtenerTiposDocumentoQuery(AmbitoAplicacion: AmbitoAplicacion.Trabajador));

        _editandoId = null;
        _trabajadorId = string.Empty;
        _clienteId = string.Empty;
        _empresaId = string.Empty;
        _vehiculoId = string.Empty;
        _tipoDocumentoId = string.Empty;
        _fechaEmision = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        _fechaEmisionOriginal = null;
        _fechaVencimientoManual = string.Empty;
        _archivoUrl = null;
        _comentarios = string.Empty;
        _glosarioDescripcion = null;
        _glosarioCriteriosValidacion = null;
        _glosarioSeSolicitaA = null;
        _glosarioObservaciones = null;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private async Task CambiarAmbitoAsync(string valor)
    {
        _ambitoAplicacion = valor;
        _trabajadorId = string.Empty;
        _clienteId = string.Empty;
        _empresaId = string.Empty;
        _vehiculoId = string.Empty;

        var ambito = Enum.Parse<AmbitoAplicacion>(valor);

        if (ambito == AmbitoAplicacion.Cliente && _clientesDisponibles.Count == 0)
            _clientesDisponibles = await Mediator.Send(new ObtenerClientesParaSelectorQuery());
        else if (ambito == AmbitoAplicacion.Empresa && _empresasDisponibles.Count == 0)
            _empresasDisponibles = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery());
        else if (ambito == AmbitoAplicacion.Vehiculo && _vehiculosDisponibles.Count == 0)
            _vehiculosDisponibles = await Mediator.Send(new ObtenerVehiculosParaSelectorQuery());

        _tiposDisponibles = await Mediator.Send(new ObtenerTiposDocumentoQuery(AmbitoAplicacion: ambito));
        CambiarTipoDocumento(string.Empty);
    }

    private void CambiarTipoDocumento(string valor)
    {
        _tipoDocumentoId = valor;

        var tipo = _tiposDisponibles.FirstOrDefault(t => t.Id.ToString() == valor);
        _glosarioDescripcion = tipo?.Descripcion;
        _glosarioCriteriosValidacion = tipo?.CriteriosValidacion;
        _glosarioSeSolicitaA = tipo?.SeSolicitaA;
        _glosarioObservaciones = tipo?.Observaciones;
    }

    private async Task AbrirEditarAsync(Guid id)
    {
        var documento = await Mediator.Send(new ObtenerDocumentoPorIdQuery(id));
        if (documento is null)
        {
            ToastService.Mostrar("No encontramos este documento. Puede que ya se haya eliminado.", TonoToast.Error);
            await RecargarAsync();
            return;
        }

        _editandoId = documento.Id;
        _ambitoAplicacion = documento.Ambito.ToString();
        _propietarioNombreSoloLectura = documento.PropietarioNombre;
        _tipoDocumentoNombreSoloLectura = documento.TipoDocumentoNombre;
        _tipoDocumentoAplicaVencimientoAutomaticoEdit = documento.TipoDocumentoAplicaVencimientoAutomatico;
        _fechaEmision = documento.FechaEmision.ToString("yyyy-MM-dd");
        _fechaEmisionOriginal = documento.FechaEmision;
        _fechaVencimientoManual = documento.FechaVencimiento?.ToString("yyyy-MM-dd") ?? string.Empty;
        _archivoUrl = documento.ArchivoUrl;
        _comentarios = documento.Comentarios ?? string.Empty;
        _glosarioDescripcion = documento.TipoDocumentoDescripcion;
        _glosarioCriteriosValidacion = documento.TipoDocumentoCriteriosValidacion;
        _glosarioSeSolicitaA = documento.TipoDocumentoSeSolicitaA;
        _glosarioObservaciones = documento.TipoDocumentoObservaciones;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        return Task.CompletedTask;
    }

    private void CopiarFechaEmisionAVencimiento() => _fechaVencimientoManual = _fechaEmision;

    /// <summary>
    /// Acepta PDF, JPG y PNG. Las imágenes se convierten a PDF automáticamente;
    /// si se seleccionan varios archivos a la vez (p. ej. varias fotos de las
    /// páginas de un mismo documento), se combinan en un único PDF multipágina
    /// antes de guardarse — nunca se adjunta más de un archivo por Documento.
    /// </summary>
    private async Task ManejarArchivoSeleccionadoAsync(InputFileChangeEventArgs e)
    {
        var archivos = e.GetMultipleFiles(MaximoArchivosPorSubida);

        foreach (var archivo in archivos)
        {
            if (!ConversorArchivosPdf.EsPdf(archivo.Name) && !ConversorArchivosPdf.EsImagen(archivo.Name))
            {
                ToastService.Mostrar($"\"{archivo.Name}\" no es un PDF, JPG ni PNG.", TonoToast.Error);
                return;
            }

            if (archivo.Size > TamanoMaximoArchivoBytes)
            {
                ToastService.Mostrar($"\"{archivo.Name}\" supera los 10 MB.", TonoToast.Error);
                return;
            }
        }

        _subiendoArchivo = true;
        StateHasChanged();

        try
        {
            var contenidos = new List<(byte[] Contenido, string NombreArchivo)>();
            foreach (var archivo in archivos)
            {
                await using var flujo = archivo.OpenReadStream(TamanoMaximoArchivoBytes);
                using var memoria = new MemoryStream();
                await flujo.CopyToAsync(memoria);
                contenidos.Add((memoria.ToArray(), archivo.Name));
            }

            var pdfUnificado = ConversorArchivosPdf.Unificar(contenidos);

            using var flujoPdf = new MemoryStream(pdfUnificado);
            _archivoUrl = await AlmacenamientoArchivos.GuardarAsync(flujoPdf, "documento.pdf");
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos procesar el archivo. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _subiendoArchivo = false;
        }
    }

    /// <summary>
    /// Al renovar, si la nueva fecha de emisión es anterior a la que ya
    /// tenía el documento probablemente se subió el archivo equivocado —
    /// se pide confirmación explícita en vez de guardar directamente.
    /// </summary>
    private async Task GuardarAsync()
    {
        if (_editandoId is not null
            && _fechaEmisionOriginal is not null
            && DateOnly.TryParse(_fechaEmision, out var nuevaFecha)
            && nuevaFecha < _fechaEmisionOriginal)
        {
            _confirmarVigenciaAnteriorVisible = true;
            return;
        }

        await GuardarInternoAsync();
    }

    private async Task ConfirmarGuardarConVigenciaAnteriorAsync()
    {
        _procesandoConfirmacionVigencia = true;
        try
        {
            await GuardarInternoAsync();
        }
        finally
        {
            _procesandoConfirmacionVigencia = false;
            _confirmarVigenciaAnteriorVisible = false;
        }
    }

    private async Task GuardarInternoAsync()
    {
        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();

        try
        {
            if (!DateOnly.TryParse(_fechaEmision, out var fechaEmision))
            {
                _mensajeErrorFormulario = "Introduce una fecha de emisión válida.";
                return;
            }

            DateOnly? fechaVencimientoManual = RequiereVencimientoManual && DateOnly.TryParse(_fechaVencimientoManual, out var fv)
                ? fv
                : null;

            var comentarios = string.IsNullOrWhiteSpace(_comentarios) ? null : _comentarios;
            string? mensajeError;

            if (_editandoId is null)
            {
                var ambito = Enum.Parse<AmbitoAplicacion>(_ambitoAplicacion);
                var propietarioId = ambito switch
                {
                    AmbitoAplicacion.Trabajador => _trabajadorId,
                    AmbitoAplicacion.Cliente => _clienteId,
                    AmbitoAplicacion.Vehiculo => _vehiculoId,
                    _ => _empresaId
                };

                if (!Guid.TryParse(propietarioId, out var idPropietario))
                {
                    _mensajeErrorFormulario = ambito switch
                    {
                        AmbitoAplicacion.Trabajador => "Selecciona un trabajador.",
                        AmbitoAplicacion.Cliente => "Selecciona un cliente.",
                        AmbitoAplicacion.Vehiculo => "Selecciona un vehículo.",
                        _ => "Selecciona una empresa."
                    };
                    return;
                }

                if (!Guid.TryParse(_tipoDocumentoId, out var tipoDocumentoId))
                {
                    _mensajeErrorFormulario = "Selecciona un tipo de documento.";
                    return;
                }

                var resultado = await Mediator.Send(new CrearDocumentoCommand(
                    TrabajadorId: ambito == AmbitoAplicacion.Trabajador ? idPropietario : null,
                    ClienteId: ambito == AmbitoAplicacion.Cliente ? idPropietario : null,
                    EmpresaId: ambito == AmbitoAplicacion.Empresa ? idPropietario : null,
                    VehiculoId: ambito == AmbitoAplicacion.Vehiculo ? idPropietario : null,
                    TipoDocumentoId: tipoDocumentoId,
                    FechaEmision: fechaEmision,
                    FechaVencimientoManual: fechaVencimientoManual,
                    ArchivoUrl: _archivoUrl,
                    Comentarios: comentarios));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }
            else
            {
                var resultado = await Mediator.Send(
                    new RenovarDocumentoCommand(_editandoId.Value, fechaEmision, fechaVencimientoManual, _archivoUrl, comentarios));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }

            if (mensajeError is not null)
            {
                _mensajeErrorFormulario = mensajeError;
                return;
            }

            ToastService.Mostrar(
                _editandoId is null ? "Documento creado correctamente." : "Documento renovado correctamente.",
                TonoToast.Exito);

            _drawerVisible = false;
            await RecargarAsync();
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

    private void AbrirEliminar(Guid id, string propietarioNombre, string tipoDocumentoNombre)
    {
        _idAEliminar = id;
        _propietarioAEliminar = propietarioNombre;
        _tipoDocumentoAEliminar = tipoDocumentoNombre;
        _confirmarEliminarVisible = true;
    }

    private async Task ConfirmarEliminarAsync()
    {
        _eliminando = true;

        try
        {
            var usuarioId = await CurrentUserService.ObtenerUsuarioActualIdAsync();
            var resultado = await Mediator.Send(new EliminarDocumentoCommand(_idAEliminar, usuarioId ?? Guid.Empty));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                ToastService.Mostrar("Documento eliminado correctamente.", TonoToast.Exito);
                _confirmarEliminarVisible = false;
                await RecargarAsync();
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar el documento. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }
}
