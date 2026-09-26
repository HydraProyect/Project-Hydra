using CaeManager.Application.Common;
using CaeManager.Web.Features.Trabajadores.Recursos;
using CaeManager.Web.Recursos;
using Microsoft.Extensions.Localization;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Asignaciones.Commands.DarDeBajaAsignaciones;
using CaeManager.Application.Asignaciones.Commands.ReactivarAsignacion;
using CaeManager.Application.Gestiones.Commands.CompletarGestion;
using CaeManager.Application.Gestiones.Commands.CrearGestionesParaTrabajador;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos.Components;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Trabajadores.Pages;

/// <summary>
/// Trabajador 360 (Parte XVI PROMPT 04) — mismo criterio que Centro 360
/// (<c>CentroDetalle.razor.cs</c>): NO reimplementa edición de Información,
/// Vehículos ni Historial, eso ya vive en <c>TrabajadorWorkspacePanel</c>;
/// esta página compone lo que ese panel no ofrece de un vistazo —
/// cumplimiento agregado, documentación exigida COMPLETA por cada centro
/// (a diferencia de la pestaña "Documentación" del panel, que es plana) y
/// las gestiones pendientes.
///
/// "Reclamar faltantes" del menú de acciones del mockup: <c>EnviarReclamacionCommand</c>
/// opera a grano Cliente (reclama todo lo pendiente de ese Cliente, no hace
/// falta acotar por Centro), así que el único caso ambiguo es un trabajador
/// con asignaciones activas en Centros de Clientes distintos a la vez —
/// escenario común, no raro (ver <c>DatosPruebaSeeder</c>). Decisión del
/// usuario 2026-08-18: el gestor elige a qué Cliente(s) reclamar
/// (<see cref="ReclamarFaltantesAsync"/> salta directo si solo hay uno,
/// abre selector si hay varios) — nunca se reclama sin que el gestor lo
/// confirme, y nunca se inventa un criterio automático (próxima visita,
/// "el más urgente"...) que el usuario no pidió.
/// </summary>
public partial class TrabajadorDetalle : ComponentBase, IDisposable
{
    private static readonly IReadOnlyDictionary<EstadoDocumento, int> OrdenSeveridad = new Dictionary<EstadoDocumento, int>
    {
        [EstadoDocumento.Faltante] = 0,
        [EstadoDocumento.Vencido] = 1,
        [EstadoDocumento.Urgente] = 2,
        [EstadoDocumento.Proximo] = 3,
        // Sin vigencia confirmada: detrás de lo malo conocido y delante de lo
        // vigente (mismo orden que EstadoDocumentalFiltro.ClaveOrden).
        [EstadoDocumento.SinConfirmar] = 4,
        [EstadoDocumento.Vigente] = 5
    };

    [Parameter] public Guid TrabajadorId { get; set; }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ContextWorkspaceService WorkspaceService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosTrabajadores> Textos { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosComunes> Comunes { get; set; } = default!;

    private TrabajadorDetalleDto? _detalle;
    private IReadOnlyList<CentroDocumentacionTrabajadorDto> _centros = [];
    private bool _cargando = true;
    private bool _error;

    private string _pestanaActiva = "operacion";
    private readonly HashSet<Guid> _expandidosCentro = [];

    private DrawerGestionDocumento _drawerGestion = default!;

    private bool _crearGestionVisible;
    private IReadOnlyList<TipoDocumentoListaDto> _tiposDocumentoDisponibles = [];
    private string _tipoDocumentoParaGestion = string.Empty;
    private bool _creandoGestion;

    private bool _reclamarFaltantesVisible;
    private bool _reclamandoFaltantes;
    private IReadOnlyList<ClienteReclamableDto> _clientesReclamables = [];
    private readonly HashSet<Guid> _clientesSeleccionadosReclamar = [];

    private record ClienteReclamableDto(Guid ClienteId, string ClienteRazonSocial, IReadOnlyList<Guid> DocumentoIds);

    /// <summary>
    /// Tope de la lista de documentos del trabajador. Un trabajador real tiene
    /// decenas; si alguna vez pasa del tope, la pestaña lo dice («Mostrando N
    /// de M») en vez de callarlo.
    /// </summary>
    private const int TopeDocumentos = 200;

    /// <summary>
    /// Documentación del trabajador a día de hoy, sin pasar por los Centros
    /// (decisión del propietario 2026-09-24, Q1–Q4 = (a)): alimenta la franja
    /// «Por vencer» de Operación y la pestaña Documentación. Es la misma
    /// ObtenerDocumentosQuery que la pestaña del panel, con su alcance.
    /// </summary>
    private IReadOnlyList<DocumentoListaDto> _documentos = [];
    private int _totalDocumentos;

    /// <summary>
    /// Tipos de ámbito Trabajador que se piden con carácter general
    /// (<see cref="RequisitoDocumental.Si"/>). Lo que un Centro exige fuera de
    /// esta lista lo incluyó la configuración de ese Centro. Null hasta cargar.
    /// </summary>
    private HashSet<Guid>? _tiposGenerales;
    private bool _cargandoDocumentos = true;
    private bool _errorDocumentos;

    private IReadOnlyList<GestionListaDto> _gestionesPendientes = [];
    private bool _cargandoGestiones = true;
    private readonly HashSet<Guid> _completandoGestion = [];
    private readonly HashSet<Guid> _dandoDeBajaAsignacion = [];
    private bool _confirmarBajaAsignacionVisible;
    private Guid? _asignacionABajar;
    private string _centroABajar = string.Empty;
    private readonly HashSet<Guid> _reactivandoAsignacion = [];

    /// <summary>Trabajador cuya ficha se está pintando: al cambiar, lo que quedara preparado en una modal deja de valer.</summary>
    private Guid _trabajadorEnPantalla;

    /// <summary>
    /// Se cancela al retirarse la página: las consultas en curso dejan de
    /// trabajar para nadie y ninguna respuesta tardía repinta un componente
    /// ya desechado. Mismo patrón que <c>Empresas.razor.cs</c>.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    /// <summary>
    /// Número de la última carga. Cada carga captura el suyo ANTES del
    /// <c>await</c> y, al volver, solo escribe estado si sigue siendo la
    /// vigente. Sin esto, navegar de un trabajador a otro —o recargar tras
    /// guardar un documento— dejaba que la respuesta de la pregunta anterior
    /// pintara la cabecera, el anillo y los centros del trabajador que ya no
    /// se está mirando. La carga de gestiones comparte el mismo número: una
    /// respuesta de antes de recargar ya no pertenece a lo que se ve.
    /// </summary>
    private int _cargaVigente;

    public void Dispose()
    {
        if (_desechado)
            return;

        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    /// <summary>La respuesta es de la pregunta vigente y la página sigue viva.</summary>
    private bool EsVigente(int carga) => !_desechado && carga == _cargaVigente;

    private string? NombreCompleto => _detalle is null ? null : $"{_detalle.Nombre} {_detalle.Apellidos}";

    /// <summary>
    /// Las pestañas de esta ficha. «Contactos» ya no es pestaña: sus datos
    /// viven en el lateral (decisión del propietario 2026-09-24). No es estático porque el recuento de
    /// «Operación» depende de los datos cargados: el mockup Gen 2 pinta ahí
    /// la píldora con los documentos que hoy tienen incidencia.
    /// </summary>
    private IReadOnlyList<PestanaDefinicion> Pestanas =>
    [
        new("operacion", Textos["PestanaOperacion"])
        {
            Contador = TotalConIncidencia == 0
                ? null
                : new ContadorPestana(
                    TotalConIncidencia,
                    TotalConIncidencia == 1 ? Textos["ContadorIncidenciasUno"] : Textos["ContadorIncidenciasVarios"],
                    EnAlerta: true)
        },
        new("documentacion", Textos["PestanaDocumentacion"]),
        new("historial", Textos["PestanaHistorial"])
    ];

    private int TotalConIncidencia =>
        _centros.Sum(c => c.Documentos.Count(d => d.Estado != EstadoDocumento.Vigente));

    private IReadOnlyList<BreadcrumbElemento> Miguero =>
        new[] { new BreadcrumbElemento(Textos["MigaTrabajadores"]), new BreadcrumbElemento(NombreCompleto ?? "…") };

    private int TotalRequeridos => _centros.Sum(c => c.Documentos.Count);
    private int TotalAlDia => _centros.Sum(c => c.Documentos.Count(d => d.Estado == EstadoDocumento.Vigente));
    private int? Cumplimiento => TotalRequeridos == 0 ? null : (int)Math.Round(TotalAlDia * 100.0 / TotalRequeridos);

    private CentroDocumentacionTrabajadorDto? CentroMasUrgente =>
        _centros.Count == 0 ? null : _centros.MinBy(c => OrdenSeveridad[c.PeorEstado]);

    private EstadoDocumento? PeorEstadoGlobal => CentroMasUrgente?.PeorEstado;

    /// <summary>El primer documento faltante/vencido del centro más urgente — lo que abre el botón primario "Subir documento" de la cabecera (mockup § cabecera: el botón y el badge de urgencia hablan del mismo problema).</summary>
    private DocumentoRequeridoDto? DocumentoMasUrgente =>
        CentroMasUrgente?.Documentos.FirstOrDefault(d => d.Estado != EstadoDocumento.Vigente);

    protected override async Task OnParametersSetAsync() => await CargarAsync();

    private async Task CargarAsync()
    {
        if (_desechado)
            return;

        // Todo lo que define la pregunta se lee ANTES del await.
        var carga = ++_cargaVigente;
        var trabajadorId = TrabajadorId;

        // Las modales se pintan fuera del bloque de la página, así que
        // sobrevivían al cambio de ruta: abrir «Reclamar faltantes» para A, navegar a
        // B y confirmar reclamaba a B. Cambiar de trabajador las cierra y
        // tira lo que habían preparado.
        if (_trabajadorEnPantalla != trabajadorId)
        {
            _trabajadorEnPantalla = trabajadorId;
            CerrarModalesPendientes();

            // Los documentos llegan en una carga aparte, después del detalle
            // y los centros: sin vaciarlos aquí, la franja «Por vencer» de B
            // enseñaría los de A mientras tanto.
            _documentos = [];
            _totalDocumentos = 0;
            _tiposGenerales = null;
            _cargandoDocumentos = true;
            _errorDocumentos = false;
        }

        _cargando = true;
        _error = false;

        try
        {
            var detalle = await Mediator.Send(new ObtenerTrabajadorPorIdQuery(trabajadorId), _ciclo.Token);
            if (!EsVigente(carga))
                return;

            _detalle = detalle;
            if (_detalle is null)
            {
                _error = true;
                return;
            }

            // El acordeón se cierra al cambiar de pregunta: los Ids
            // expandidos eran de la respuesta anterior y no significan nada
            // en la nueva.
            _expandidosCentro.Clear();

            var centros = await Mediator.Send(new ObtenerDocumentacionPorCentroDeTrabajadorQuery(trabajadorId), _ciclo.Token);
            if (!EsVigente(carga))
                return;

            _centros = centros;

            // RendererInfo.IsInteractive: OnParametersSetAsync (y por tanto
            // CargarAsync) también corre durante el prerenderizado estático
            // de una carga en frío — un "_ = " sin await ahí deja la tarea
            // de gestiones en vuelo cuando ASP.NET Core ya dio por completada
            // esa fase y libera el scope de DI, tirando el DbContext a mitad
            // de consulta (reproducido en vivo: "Connection is not open",
            // DbContext ya liberado, ObjectDisposedException en el semáforo
            // de PuertaAccesoDatos — todo en cascada desde este único origen,
            // solo visible en una carga en frío real como un deep-link con
            // "ctx", nunca navegando ya con el circuito interactivo vivo).
            // Esperar a la fase interactiva evita la carrera sin perder la
            // carga en paralelo: sigue sin bloquear el resto de la página.
            if (RendererInfo.IsInteractive)
            {
                _ = CargarGestionesAsync();
                _ = CargarDocumentosAsync();
            }
        }
        catch (Exception) when (!EsVigente(carga))
        {
            // Una carga superada que falla no es un error de la vigente: no
            // puede tapar su resultado con el estado de error.
        }
        catch (Exception)
        {
            _error = true;
        }
        finally
        {
            if (EsVigente(carga))
            {
                _cargando = false;
                StateHasChanged();
            }
        }
    }

    private async Task CargarGestionesAsync()
    {
        if (_desechado)
            return;

        // La respuesta solo vale para la carga que la pidió: tras cambiar de
        // trabajador (o recargar), unas gestiones de antes ya no son de este
        // trabajador ni de este estado.
        var carga = _cargaVigente;
        var trabajadorId = TrabajadorId;

        _cargandoGestiones = true;
        try
        {
            var resultado = await Mediator.Send(new ObtenerGestionesQuery(
                Busqueda: null, Estado: EstadoGestion.Pendiente, TrabajadorId: trabajadorId, TamanoPagina: 50), _ciclo.Token);
            if (!EsVigente(carga))
                return;

            _gestionesPendientes = resultado.Elementos;
        }
        catch (Exception) when (!EsVigente(carga))
        {
            return;
        }
        catch (Exception)
        {
            _gestionesPendientes = [];
        }
        finally
        {
            if (EsVigente(carga))
            {
                _cargandoGestiones = false;
                StateHasChanged();
            }
        }
    }

    /// <summary>
    /// Carga los documentos del trabajador y los tipos generales. Mismo
    /// patrón que <see cref="CargarGestionesAsync"/>: en paralelo con el resto
    /// y solo para la carga que la pidió. Un fallo aquí no tumba la página:
    /// se queda en la pestaña Documentación, con su reintento.
    /// </summary>
    private async Task CargarDocumentosAsync()
    {
        if (_desechado)
            return;

        var carga = _cargaVigente;
        var trabajadorId = TrabajadorId;

        _cargandoDocumentos = true;
        _errorDocumentos = false;
        try
        {
            // Orden por Estado ascendente, resuelto en la base de datos: si hay
            // más documentos que el tope, lo que se queda fuera es lo vigente o
            // lo que no caduca, nunca lo que alimenta la franja «Por vencer».
            var documentos = await Mediator.Send(new ObtenerDocumentosQuery(
                trabajadorId, AmbitoAplicacion.Trabajador, Busqueda: null, TamanoPagina: TopeDocumentos,
                OrdenarPor: nameof(DocumentoListaDto.Estado)), _ciclo.Token);
            if (!EsVigente(carga))
                return;

            var tipos = await Mediator.Send(new ObtenerTiposDocumentoQuery(AmbitoAplicacion: AmbitoAplicacion.Trabajador), _ciclo.Token);
            if (!EsVigente(carga))
                return;

            _documentos = documentos.Elementos;
            _totalDocumentos = documentos.TotalElementos;
            _tiposGenerales = tipos.Where(t => t.Requerido == RequisitoDocumental.Si).Select(t => t.Id).ToHashSet();
        }
        catch (Exception) when (!EsVigente(carga))
        {
            return;
        }
        catch (Exception)
        {
            _documentos = [];
            _totalDocumentos = 0;
            _tiposGenerales = null;
            _errorDocumentos = true;
        }
        finally
        {
            if (EsVigente(carga))
            {
                _cargandoDocumentos = false;
                StateHasChanged();
            }
        }
    }

    /// <summary>
    /// Franja «Por vencer» de Operación (Q3 = (a)): los estados Próximo y
    /// Urgente de CalculadoraEstadoDocumento, que ya aplican el umbral ámbar
    /// del tenant. Lo vencido no entra: ya lo señalan los Centros.
    /// </summary>
    private IReadOnlyList<DocumentoListaDto> DocumentosPorVencer =>
        _documentos.Where(d => d.Estado is EstadoDocumento.Proximo or EstadoDocumento.Urgente)
            .OrderBy(d => d.FechaVencimiento ?? DateOnly.MaxValue)
            .ThenBy(d => d.TipoDocumentoNombre, StringComparer.CurrentCulture)
            .ToList();

    /// <summary>Lo peor primero y, a igualdad, lo que antes caduca.</summary>
    private IReadOnlyList<DocumentoListaDto> DocumentosOrdenados =>
        _documentos.OrderBy(d => OrdenSeveridad.GetValueOrDefault(d.Estado, OrdenSeveridad.Count))
            .ThenBy(d => d.FechaVencimiento ?? DateOnly.MaxValue)
            .ThenBy(d => d.TipoDocumentoNombre, StringComparer.CurrentCulture)
            .ToList();

    /// <summary>Un tipo exigido en un Centro de trabajo concreto, con la fecha del documento que lo cubre.</summary>
    private sealed record ExigenciaDeCentro(
        Guid CentroId, string CentroNombre, string ClienteRazonSocial, DocumentoRequeridoDto Documento, DateOnly? FechaEmision);

    /// <summary>
    /// «Lo que es puro del cliente» (Q2 = (a)): los tipos que un Centro exige y
    /// que no se piden con carácter general, por par Trabajador–Centro. El
    /// modelo no guarda quién originó la exigencia, así que se rotula como
    /// «exigido en el Centro X (del Cliente empresarial Y)», nunca como
    /// «exigido por Y». La fecha de renovación es la caducidad del documento
    /// (Q1 = (a)): la misma en todos los Centros, porque Documento no tiene
    /// CentroId.
    /// </summary>
    private IReadOnlyList<ExigenciaDeCentro> ExigenciasPropiasDeCentros
    {
        get
        {
            if (_tiposGenerales is not { } generales)
                return [];

            var emisionPorDocumento = _documentos.ToDictionary(d => d.Id, d => d.FechaEmision);
            return _centros
                .SelectMany(c => c.Documentos
                    .Where(d => !generales.Contains(d.TipoDocumentoId))
                    .Select(d => new ExigenciaDeCentro(
                        c.CentroId, c.CentroNombre, c.ClienteRazonSocial, d,
                        d.DocumentoId is { } id && emisionPorDocumento.TryGetValue(id, out var emision) ? emision : null)))
                .OrderBy(e => e.ClienteRazonSocial, StringComparer.CurrentCulture)
                .ThenBy(e => e.CentroNombre, StringComparer.CurrentCulture)
                .ThenBy(e => e.Documento.TipoDocumentoNombre, StringComparer.CurrentCulture)
                .ToList();
        }
    }

    private string TextoVence(DateOnly? fechaVencimiento, EstadoDocumento estado) =>
        fechaVencimiento is { } vence ? vence.ToString("dd/MM/yyyy")
        : estado == EstadoDocumento.SinCaducidad ? Textos["SinCaducidad"]
        : "—";

    private void IrABreadcrumb(int indice)
    {
        if (indice == 0)
            NavigationManager.NavigateTo("/trabajadores");
    }

    private void AlternarExpansionCentro(Guid centroId)
    {
        if (!_expandidosCentro.Remove(centroId))
            _expandidosCentro.Add(centroId);
    }

    private void AbrirInformacion() =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Trabajador, TrabajadorId, NombreCompleto ?? string.Empty, "informacion");

    private void AbrirEmpleador()
    {
        if (_detalle is null) return;

        if (_detalle.EmpresaId is { } empresaId)
            WorkspaceService.AbrirAsync(EntidadWorkspace.Empresa, empresaId, _detalle.EmpleadorNombre, "informacion");
        else if (_detalle.SubcontrataId is { } subcontrataId)
            WorkspaceService.AbrirAsync(EntidadWorkspace.Subcontrata, subcontrataId, _detalle.EmpleadorNombre, "informacion");
    }

    private Task GestionarDocumentoAsync(DocumentoRequeridoDto documento) =>
        documento.DocumentoId is { } documentoId
            ? _drawerGestion.AbrirEditarAsync(documentoId)
            : _drawerGestion.AbrirCrearParaFaltanteAsync(TrabajadorId, documento.TipoDocumentoId);

    private Task SubirDocumentoMasUrgenteAsync() =>
        DocumentoMasUrgente is { } documento ? GestionarDocumentoAsync(documento) : Task.CompletedTask;

    private async Task ManejarDocumentoGuardadoAsync() => await CargarAsync();

    /// <summary>
    /// FS-13 (auditoría UX de flujos sin salida, 2026-09-24): la baja salía al
    /// primer clic y sin vuelta atrás; la única corrección era un alta nueva,
    /// que partía la historia en dos filas. Ahora se confirma y el aviso
    /// ofrece «Deshacer».
    /// </summary>
    private void AbrirBajaAsignacionConfirm(Guid asignacionId, string centroNombre)
    {
        _asignacionABajar = asignacionId;
        _centroABajar = centroNombre;
        _confirmarBajaAsignacionVisible = true;
    }

    private async Task ConfirmarBajaAsignacionAsync()
    {
        if (_asignacionABajar is not { } asignacionId) return;
        await DarDeBajaAsignacionAsync(asignacionId);

        // Si mientras tanto se abrió otro diálogo (otro trabajador, otra fila),
        // es del usuario: solo se cierra el que confirmó esta baja.
        if (_asignacionABajar != asignacionId) return;
        _confirmarBajaAsignacionVisible = false;
        _asignacionABajar = null;
    }

    private async Task DarDeBajaAsignacionAsync(Guid asignacionId)
    {
        // Guarda de reentrada por asignación, como en CompletarGestionAsync:
        // dos eventos seguidos mandaban dos bajas.
        if (!_dandoDeBajaAsignacion.Add(asignacionId)) return;

        try
        {
            var resultado = await Mediator.Send(new DarDeBajaAsignacionesCommand([asignacionId], DateOnly.FromDateTime(DateTime.UtcNow)));
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            // El comando devuelve éxito aunque no haya dado de baja ninguna
            // (asignación ya cerrada, o fuera de alcance): decir «hecho»
            // entonces es mentir sobre lo que pasó.
            var (dadasDeBaja, errores) = (resultado.Valor.DadasDeBaja, resultado.Valor.Errores);
            if (dadasDeBaja == 0)
            {
                ToastService.Mostrar(
                    errores.Count > 0 ? errores[0] : Textos["ErrorDarDeBaja"].Value,
                    TonoToast.Error);
            }
            else if (errores.Count > 0)
                ToastService.Mostrar(Textos["ToastBajaConAvisos", string.Join("; ", errores)], TonoToast.Advertencia,
                    Textos["ToastAccionDeshacer"], () => DeshacerBajaAsignacionAsync(asignacionId));
            else
                ToastService.Mostrar(Textos["ToastBaja"], TonoToast.Exito,
                    Textos["ToastAccionDeshacer"], () => DeshacerBajaAsignacionAsync(asignacionId));

            await CargarAsync();
        }
        finally
        {
            _dandoDeBajaAsignacion.Remove(asignacionId);
        }
    }

    /// <summary>
    /// Solo cuentan documentos que existen (<c>DocumentoId != null</c>) y no
    /// están vigentes — un Faltante no tiene fila de Documento que reclamar,
    /// igual que en <c>ObtenerLoteReclamacionQuery</c>: "reclamar" pide una
    /// renovación, no puede pedir la creación de algo que nunca existió.
    /// </summary>
    private async Task ReclamarFaltantesAsync()
    {
        var clientes = _centros
            .SelectMany(c => c.Documentos
                .Where(d => d.DocumentoId is not null && d.Estado != EstadoDocumento.Vigente)
                .Select(d => (c.ClienteId, c.ClienteRazonSocial, DocumentoId: d.DocumentoId!.Value)))
            .GroupBy(x => (x.ClienteId, x.ClienteRazonSocial))
            .Select(g => new ClienteReclamableDto(g.Key.ClienteId, g.Key.ClienteRazonSocial, g.Select(x => x.DocumentoId).Distinct().ToList()))
            .ToList();

        if (clientes.Count == 0)
        {
            ToastService.Mostrar(Textos["ToastSinPendientesReclamar"], TonoToast.Info);
            return;
        }

        if (clientes.Count == 1)
        {
            await EnviarReclamacionAClientesAsync(clientes);
            return;
        }

        _clientesReclamables = clientes;
        _clientesSeleccionadosReclamar.Clear();
        foreach (var cliente in clientes)
            _clientesSeleccionadosReclamar.Add(cliente.ClienteId);
        _reclamarFaltantesVisible = true;
    }

    private void AlternarClienteReclamar(Guid clienteId, bool marcado)
    {
        if (marcado) _clientesSeleccionadosReclamar.Add(clienteId);
        else _clientesSeleccionadosReclamar.Remove(clienteId);
    }

    private async Task ConfirmarReclamarFaltantesAsync()
    {
        var seleccionados = _clientesReclamables.Where(c => _clientesSeleccionadosReclamar.Contains(c.ClienteId)).ToList();
        if (seleccionados.Count == 0) return;

        _reclamarFaltantesVisible = false;
        await EnviarReclamacionAClientesAsync(seleccionados);
    }

    /// <summary>
    /// Reutiliza EnviarReclamacionCommand tal cual, un envío por Cliente —
    /// secuencial (no Task.WhenAll) porque el DbContext de la petición Blazor
    /// Server es scoped y no admite uso concurrente.
    /// </summary>
    private async Task EnviarReclamacionAClientesAsync(IReadOnlyList<ClienteReclamableDto> clientes)
    {
        if (_reclamandoFaltantes) return;

        _reclamandoFaltantes = true;
        try
        {
            var enviadosA = new List<string>();
            var fallidos = new List<string>();

            foreach (var cliente in clientes)
            {
                var resultado = await Mediator.Send(new EnviarReclamacionCommand(cliente.ClienteId, cliente.DocumentoIds));
                if (resultado.EsFallido)
                    fallidos.Add($"{cliente.ClienteRazonSocial}: {resultado.Error.Mensaje}");
                else
                    enviadosA.Add(cliente.ClienteRazonSocial);
            }

            if (enviadosA.Count == 1)
                ToastService.Mostrar(Textos["ToastReclamacionEnviadaUno", enviadosA[0]], TonoToast.Exito);
            else if (enviadosA.Count > 1)
                ToastService.Mostrar(Textos["ToastReclamacionEnviadaVarios", enviadosA.Count, string.Join(", ", enviadosA)], TonoToast.Exito);

            foreach (var mensaje in fallidos)
                ToastService.Mostrar(mensaje, TonoToast.Error);

            if (enviadosA.Count > 0)
                await CargarAsync();
        }
        finally
        {
            _reclamandoFaltantes = false;
        }
    }

    private async Task AbrirCrearGestionAsync()
    {
        var carga = _cargaVigente;
        var tipos = await Mediator.Send(new ObtenerTiposDocumentoQuery(AmbitoAplicacion: AmbitoAplicacion.Trabajador), _ciclo.Token);
        if (!EsVigente(carga))
            return;

        _tiposDocumentoDisponibles = tipos;
        _tipoDocumentoParaGestion = string.Empty;
        _crearGestionVisible = true;
    }

    private async Task ConfirmarCrearGestionAsync()
    {
        if (_creandoGestion) return;
        if (!Guid.TryParse(_tipoDocumentoParaGestion, out var tipoDocumentoId)) return;

        _creandoGestion = true;
        try
        {
            var resultado = await Mediator.Send(new CrearGestionesParaTrabajadorCommand(TrabajadorId, tipoDocumentoId));
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            ToastService.Mostrar(Textos["ToastGestionCreada", resultado.Valor.Creadas], TonoToast.Exito);
            _crearGestionVisible = false;
            await CargarGestionesAsync();
        }
        finally
        {
            _creandoGestion = false;
        }
    }

    private async Task CompletarGestionAsync(Guid gestionId)
    {
        _completandoGestion.Add(gestionId);
        try
        {
            var resultado = await Mediator.Send(new CompletarGestionCommand(gestionId, Completada: true));
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            await CargarGestionesAsync();
        }
        finally
        {
            _completandoGestion.Remove(gestionId);
        }
    }

    /// <summary>
    /// «Deshacer» del aviso de baja: reabre la misma asignación, con su fecha
    /// de alta de siempre. El comando comprueba otra vez la autoridad y que no
    /// haya otra alta del trabajador en ese centro.
    /// </summary>
    private async Task DeshacerBajaAsignacionAsync(Guid asignacionId)
    {
        if (!_reactivandoAsignacion.Add(asignacionId)) return;

        try
        {
            var resultado = await Mediator.Send(new ReactivarAsignacionCommand(asignacionId));
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            ToastService.Mostrar(Textos["ToastBajaDeshecha"], TonoToast.Exito);
            await CargarAsync();
        }
        finally
        {
            _reactivandoAsignacion.Remove(asignacionId);
        }
    }

    /// <summary>
    /// Cierra las modales y tira lo que tuvieran preparado. Se llama al cambiar
    /// de trabajador: lo elegido para uno no puede ejecutarse sobre otro.
    /// </summary>
    private void CerrarModalesPendientes()
    {
        _crearGestionVisible = false;
        _tipoDocumentoParaGestion = string.Empty;
        _reclamarFaltantesVisible = false;
        _confirmarBajaAsignacionVisible = false;
        _asignacionABajar = null;
        _clientesReclamables = [];
        _clientesSeleccionadosReclamar.Clear();
    }
}
