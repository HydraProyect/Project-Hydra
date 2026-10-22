using CaeManager.Web.Features.Documentos;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionEmpresasDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;
using CaeManager.Domain.Documentos;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Centros.Queries.ObtenerCanalesGestionDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerCredencialCanalGestion;
using CaeManager.Application.Centros.Commands.EliminarCentro;
using CaeManager.Application.Centros.Commands.RestaurarCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Domain.Centros;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Services;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Centros.Pages;

/// <summary>
/// Centro 360 (Centro 360 TALVEG, Parte XVI PROMPT 13) — la "segunda pantalla"
/// a la que se llega al pedir ver un centro por completo, ya sea desde el
/// enlace "Ver Centro 360" de la vista previa recortada de /centros
/// (AcordeonAsignacionesCentro.SoloIncidencias) o directamente por URL.
///
/// La edición de Información y Requisitos sigue en CentroWorkspacePanel. La ficha muestra
/// los requisitos aplicables y la documentación de las Empresas empleadoras, mediante lecturas
/// autorizadas diferidas. También compone la cabecera, Visitas, Trabajadores, Ficha e Historial;
/// no añade comandos ni cambia la configuración del Centro.
/// </summary>
public partial class CentroDetalle : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    [Parameter] public Guid CentroId { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "pestana")] public string? Pestana { get; set; }

    /// <summary>
    /// Filtros de la barra de trabajo, en la URL. Viajan ahí para que un
    /// centro filtrado se pueda enlazar y para que recargar no pierda lo que
    /// el gestor estaba mirando — igual que el resto de listas de la
    /// aplicación.
    /// </summary>
    [Parameter, SupplyParameterFromQuery(Name = "q")] public string? Busqueda { get; set; }

    [Parameter, SupplyParameterFromQuery(Name = "estado")] public string? Estado { get; set; }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ContextWorkspaceService WorkspaceService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private int? _numeroTrabajadores;

    private void ActualizarResumenTrabajadores((Guid CentroId, int? Trabajadores) resumen)
    {
        if (resumen.CentroId != CentroId) return;
        _numeroTrabajadores = resumen.Trabajadores;
    }

    private const string PestanaTrabajadores = "trabajadores";
    private const string PestanaDocumentacionCentro = "documentacion-centro";
    private const string PestanaRequisitos = "requisitos";
    private const string PestanaVisitas = "visitas";
    private const string PestanaFicha = "ficha";
    private const string PestanaHistorial = "historial";

    private bool HayAccesoFicha => _detalle is { } d &&
        (!string.IsNullOrWhiteSpace(d.Direccion) || d.FichaVisible &&
            (d.HorarioAcceso is not null || d.ControlAccesosNombre is not null || d.ControlAccesosTelefono is not null));
    private bool HayDatosBasicosFicha => _detalle is { } d &&
        (!string.IsNullOrWhiteSpace(d.CodigoCentro) || !string.IsNullOrWhiteSpace(d.Contacto) || d.ContratoVigenteHasta is not null);
    private bool HayPrevencionFicha => _detalle is { FichaVisible: true } d &&
        (d.TecnicoPrevencionNombre is not null || d.TecnicoPrevencionCorreo is not null || d.TecnicoPrevencionTelefono is not null
            || d.TrabajosEnAltura is not null || d.EspaciosConfinados is not null || d.AtmosferasExplosivas is not null);
    // Decisión de producto (2026-10-16): en Outbound un Centro nace vacío. Quien puede leer la ficha ve
    // siempre las cajas de Acceso y Prevención; sin datos muestran un estado vacío con «Configurar»,
    // nunca un «No» por defecto. Ningún campo de estas cajas genera requisitos ni entra en el cumplimiento.
    private bool AccesoSinConfigurar => _detalle is { } d && string.IsNullOrWhiteSpace(d.Direccion)
        && d.HorarioAcceso is null && d.ControlAccesosNombre is null && d.ControlAccesosTelefono is null;
    private bool PrevencionSinConfigurar => !HayPrevencionFicha;
    private bool MuestraCajaAcceso => HayAccesoFicha || _detalle is { FichaVisible: true };
    private bool MuestraCajaPrevencion => _detalle is { FichaVisible: true };
    private bool _editarFichaVisible;

    // Solo cambia el orden de presentación: _visitas[0] conserva la ventana de riesgo que eligió el lector.
    private IReadOnlyList<VisitaResumenDto> VisitasParaMostrar => _visitas
        .OrderBy(v => v.FechaInicio).ThenBy(v => v.FechaFin).ThenBy(v => v.VisitaId).ToList();
    private string _pestana = PestanaTrabajadores;
    private string? _pestanaDeLaUrl;

    private int TrabajadoresConIncidencias => _resumen is null ? 0 : _resumen.Recuentos.Vencidas
        .Concat(_resumen.Recuentos.Proximas)
        .Where(i => i.Ambito == AmbitoCausa.Trabajador && i.TrabajadorId is not null)
        .Select(i => i.TrabajadorId)
        .Distinct()
        .Count();

    // Centro 360 del 20-10: el estado se rotula con la plataforma del primer canal de gestión por plataforma; sin él, el rótulo genérico.
    private string? NombrePlataformaCentro => _canales
        .FirstOrDefault(c => c.Tipo == TipoCanalGestion.Plataforma && !string.IsNullOrWhiteSpace(c.ProveedorPlataformaCaeNombre))
        ?.ProveedorPlataformaCaeNombre;

    // Orden de la maqueta: primero lo que bloquea (Rechazado o Pendiente de subir), luego lo pendiente de validar, luego el resto.
    private IEnumerable<(DocumentacionEmpresaCentroDto Empresa, DocumentoEmpresaCentroDto Documento)> DocumentosEmpresaOrdenados =>
        _documentacionEmpresas
            .SelectMany(e => e.Documentos.Select(d => (Empresa: e, Documento: d)))
            .OrderBy(x => OrdenDocumentoEmpresa(x.Documento));

    private static int OrdenDocumentoEmpresa(DocumentoEmpresaCentroDto documento) => BloqueaPlataforma(documento) ? 0
        : documento.AcreditacionesEnElCentro?.Any(a => a.Estado == EstadoAcreditacion.Subida) == true ? 1 : 2;

    /// <summary>
    /// Bloque 4 del 21-10: las Empresas subcontratadas con documentación exigida en el Centro, cada una en su desplegable de
    /// «Documentación Empresa». Salen tras las Empresas del Centro, en el orden en que las devuelve la consulta.
    /// </summary>
    private IEnumerable<DocumentacionEmpresaCentroDto> SubcontratasConDocumentos =>
        _documentacionEmpresas.Where(e => e.DesdeRelacionSubcontrata && e.Documentos.Count > 0);

    /// <summary>Desplegables de Subcontrata abiertos mientras dure la vista: todos nacen plegados, como los grupos de «Agrupar».</summary>
    private readonly GruposAbiertos _subcontratasAbiertas = new();

    /// <summary>Subcontratas cuya lista de documentos ya se pidió completa («Ver los N restantes»).</summary>
    private readonly HashSet<Guid> _subcontratasCompletas = [];

    /// <summary>Un documento «con incidencia» es el que bloquea en la plataforma o está en rojo en TALVEG (vencido o faltante).</summary>
    private static bool DocumentoConIncidencia(DocumentoEmpresaCentroDto documento) =>
        BloqueaPlataforma(documento) || EstadoDocumentoFicha360.Tono(documento.Estado) == TonoBadge.Peligro;

    private IReadOnlyList<PestanaDefinicion> Pestanas =>
    [
        new(PestanaTrabajadores, Textos["PestanaTrabajadores"])
        {
            Contador = new ContadorPestana(TrabajadoresConIncidencias,
                Textos[TrabajadoresConIncidencias == 1 ? "GlosaTrabajadorConIncidencias" : "GlosaTrabajadoresConIncidencias"],
                EnAlerta: TrabajadoresConIncidencias > 0)
        },
        // Maqueta del 20-10: el contador de «Documentación Empresa» son los documentos de la Empresa con incidencia, que antes
        // iban en un bloque al abrir «Trabajadores». Una pestaña sin nada que contar no enseña «0».
        new(PestanaDocumentacionCentro, Textos["PestanaDocumentacionCentro360"])
        {
            Contador = IncidenciasEmpresaHeader.Count > 0
                ? new ContadorPestana(IncidenciasEmpresaHeader.Count,
                    Textos[IncidenciasEmpresaHeader.Count == 1 ? "GlosaDocumentoEmpresaConIncidencia" : "GlosaDocumentosEmpresaConIncidencia"],
                    EnAlerta: true)
                : null
        },
        new(PestanaRequisitos, Textos["PestanaRequisitos360"]),
        new(PestanaVisitas, Textos["PestanaVisitas"])
        {
            Contador = _visitas.Count > 0 ? new ContadorPestana(_visitas.Count, Textos["VisitasProgramadas360"]) : null
        },
        new(PestanaFicha, Textos["PestanaFicha"]),
        new(PestanaHistorial, Textos["PestanaHistorial"])
    ];

    private async Task CambiarPestana(string pestana)
    {
        _pestana = pestana;
        _pestanaDeLaUrl = pestana == PestanaTrabajadores ? null : pestana;
        NavigationManager.ActualizarFiltroEnUrl("pestana", _pestanaDeLaUrl);
        await CargarLecturasFase2Async();
    }

    private CentroDetalleDto? _detalle;
    private CentroListaDto? _resumen;
    private IReadOnlyList<VisitaResumenDto> _visitas = [];
    private IReadOnlyList<CanalGestionResumenDto> _canales = [];
    private int _documentosPorReclamar;

    /// <summary>
    /// Por qué cada Trabajador está bloqueado en ESTE Centro (<see cref="IEvaluacionDeAccesoPorCentroService"/>, la misma regla y
    /// los mismos datos que Mi trabajo). «Bloqueado» es un estado del Trabajador, nunca del Centro: el Centro enseña el detalle.
    /// </summary>
    private IReadOnlyList<DocumentacionBloqueantePendienteDto> _bloqueos = [];
    private bool _cargando = true;
    private bool _error;
    /// <summary>
    /// La consulta devolvió null: la ficha no está en el Tenant activo (o no existe; es el mismo estado, § 4.5).
    /// No es un fallo transitorio, así que no lleva «Reintentar»: lo pinta <c>FichaNoEncontrada</c>.
    /// </summary>
    private bool _noEncontrada;
    private bool _cargandoCanales = true;

    /// <summary>
    /// Centro que esta instancia tiene cargado. La ruta es una sola
    /// (<c>/centros/{id}</c>): Blazor reutiliza esta instancia al pasar de un
    /// centro a otro, y sin esto cada cambio de filtro en la URL relanzaría la
    /// carga entera.
    /// </summary>
    private Guid? _centroCargado;

    /// <summary>
    /// Generación del centro que se enseña, y un contador por carga. Cambiar de
    /// centro y retirar la página los suben: cada respuesta compara el suyo
    /// antes de escribir y, si ya no es el vigente, se descarta. Abrir otro
    /// centro con una carga en vuelo no puede pintar el anterior.
    /// </summary>
    private int _generacion;
    private int _cargaDetalle;
    private int _cargaCanales;

    /// <summary>
    /// Los contadores impiden que una respuesta tardía escriba; esto corta la
    /// consulta misma. Todas las consultas de esta página llevan su token y
    /// <see cref="Dispose"/> lo cancela: al navegar fuera, lo que estaba en
    /// vuelo deja de ocupar el DbContext del circuito. El token se copia al
    /// inicializar porque leer <c>Token</c> de un
    /// <see cref="CancellationTokenSource"/> ya desechado lanza.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private CancellationToken _cancelacion;

    private string _busqueda = string.Empty;
    private string _estadoFiltro = string.Empty;
    private string? _busquedaDeLaUrl;
    private string? _estadoDeLaUrl;

    private IReadOnlyList<IncidenciaCentroDto> IncidenciasEmpresaHeader =>
        _resumen is null
            ? []
            : _resumen.Recuentos.Vencidas.Concat(_resumen.Recuentos.Proximas).Where(i => i.Ambito == AmbitoCausa.Empresa).ToList();

    private IReadOnlyList<BreadcrumbElemento> Miguero =>
        new[] { new BreadcrumbElemento("Centros"), new BreadcrumbElemento(_detalle?.Nombre ?? "…") };

    protected override void OnInitialized() => _cancelacion = _ciclo.Token;

    /// <summary>
    /// Solo recarga cuando cambia el centro. Los filtros de la barra de trabajo
    /// también llegan por aquí (viajan en la URL) y no tocan ninguna consulta:
    /// filtran client-side lo ya cargado.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        if (!string.Equals(_pestanaDeLaUrl, Pestana, StringComparison.Ordinal))
        {
            _pestanaDeLaUrl = Pestana;
            _pestana = Pestana is PestanaVisitas or PestanaFicha or PestanaHistorial or PestanaDocumentacionCentro or PestanaRequisitos ? Pestana : PestanaTrabajadores;
        }

        AdoptarFiltrosDeLaUrl();

        if (_centroCargado == CentroId)
        {
            await CargarLecturasFase2Async();
            return;
        }

        _centroCargado = CentroId;
        ReiniciarParaNuevoCentro();
        await CargarAsync();
        await CargarLecturasFase2Async();
    }

    /// <summary>
    /// Los filtros de la URL se adoptan cuando cambian AHÍ FUERA —un deep link,
    /// el botón «atrás»—, no cada vez que se repinta: releerlos siempre pisaría
    /// lo que se está tecleando, porque <c>CampoTexto</c> tarda 300 ms en
    /// avisar y la URL va por detrás de la tecla.
    /// </summary>
    private void AdoptarFiltrosDeLaUrl()
    {
        if (!string.Equals(_busquedaDeLaUrl, Busqueda, StringComparison.Ordinal))
        {
            _busquedaDeLaUrl = Busqueda;
            _busqueda = Busqueda ?? string.Empty;
        }

        if (!string.Equals(_estadoDeLaUrl, Estado, StringComparison.Ordinal))
        {
            _estadoDeLaUrl = Estado;
            _estadoFiltro = Estado ?? string.Empty;
        }
    }

    private void Buscar(string valor)
    {
        _busqueda = valor;
        _busquedaDeLaUrl = string.IsNullOrWhiteSpace(valor) ? null : valor;
        NavigationManager.ActualizarFiltroEnUrl("q", valor);
    }

    private void CambiarEstado(string? valor)
    {
        _estadoFiltro = valor ?? string.Empty;
        _estadoDeLaUrl = string.IsNullOrWhiteSpace(valor) ? null : valor;
        NavigationManager.ActualizarFiltroEnUrl("estado", valor);
    }

    private bool HayFiltrosActivos => !string.IsNullOrWhiteSpace(_busqueda) || !string.IsNullOrWhiteSpace(_estadoFiltro);

    /// <summary>
    /// Quitar los filtros también los quita de la URL, en una sola navegación:
    /// una pantalla sin filtro cuya URL siga llevándolos los repone en cuanto
    /// alguien recarga o comparte el enlace.
    /// </summary>
    private void LimpiarFiltros()
    {
        _busqueda = string.Empty;
        _estadoFiltro = string.Empty;
        _busquedaDeLaUrl = null;
        _estadoDeLaUrl = null;
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?> { ["q"] = null, ["estado"] = null });
    }

    /// <summary>Invalida cualquier carga en vuelo: su respuesta ya no escribirá nada.</summary>
    private void InvalidarCargas()
    {
        _generacion++;
        _cargaDetalle++;
        _cargaCanales++;
    }

    /// <summary>
    /// Nada del centro anterior sobrevive al cambio: ni sus datos, ni su menú
    /// de acciones abierto, ni el recuento que decide si «Reclamar
    /// documentación» tiene objeto.
    /// </summary>
    private void ReiniciarParaNuevoCentro()
    {
        InvalidarCargas();

        _documentacionEmpresas = [];
        _requisitosCentro = [];
        _lecturaDocumentacionCentro = null;
        _lecturaRequisitosCentro = null;
        _errorLecturasFase2 = false;
        _cargandoLecturasFase2 = false;
        _detalle = null;
        _resumen = null;
        _numeroTrabajadores = null;
        _visitas = [];
        _canales = [];
        _documentosPorReclamar = 0;
        _bloqueos = [];
        _error = false;
        _cargandoCanales = true;
    }

    private async Task CargarAsync()
    {
        var carga = ++_cargaDetalle;
        var centroId = CentroId;
        _cargando = true;
        _error = false;
        _noEncontrada = false;

        try
        {
            var detalle = await Mediator.Send(new ObtenerCentroPorIdQuery(centroId), _cancelacion);
            if (carga != _cargaDetalle) return;
            _detalle = detalle;
            if (_detalle is null)
            {
                _noEncontrada = true;
                return;
            }


            var pagina = await Mediator.Send(new ObtenerCentrosQuery(
                Busqueda: null, ClienteId: null, Pagina: 1, TamanoPagina: 1, CentroId: centroId), _cancelacion);
            if (carga != _cargaDetalle) return;
            _resumen = pagina.Elementos.FirstOrDefault();

            var visitasPorCentro = await Mediator.Send(new ObtenerProximaVisitaPorCentroQuery([centroId]), _cancelacion);
            if (carga != _cargaDetalle) return;
            _visitas = visitasPorCentro.GetValueOrDefault(centroId) ?? [];

            // Cuántos documentos hay pendientes de reclamar en ESTE centro.
            // Decide si el botón "Reclamar documentación" se pinta y con qué
            // recuento: antes aparecía siempre, también cuando no había nada
            // que reclamar, y remitía al panel para que el gestor descubriera
            // allí que no había objeto. Mismo criterio de "no tumbar la
            // página" que CentroWorkspacePanel: si esto falla, el botón
            // simplemente no aparece — es contexto, no el Centro.
            try
            {
                var lotes = await Mediator.Send(new ObtenerLoteReclamacionQuery(CentroId: centroId), _cancelacion);
                if (carga != _cargaDetalle) return;
                _documentosPorReclamar = lotes.FirstOrDefault()?.Documentos.Count ?? 0;
            }
            catch (Exception)
            {
                if (carga == _cargaDetalle)
                    _documentosPorReclamar = 0;
            }

            // Trabajadores bloqueados en este Centro: contexto, no el Centro. Mismo criterio de «no tumbar la página».
            try
            {
                var bloqueos = await Mediator.Send(new ObtenerDocumentacionBloqueantePendienteQuery(centroId), _cancelacion);
                if (carga != _cargaDetalle) return;
                _bloqueos = bloqueos;
            }
            catch (Exception)
            {
                if (carga == _cargaDetalle)
                    _bloqueos = [];
            }

            // RendererInfo.IsInteractive: mismo motivo que TrabajadorDetalle
            // (ver ese commit) — OnParametersSetAsync también corre durante
            // el prerenderizado estático de una carga en frío, y un "_ = "
            // sin await ahí deja esta tarea en vuelo cuando ASP.NET Core ya
            // liberó el scope de DI al terminar esa fase, tirando el
            // DbContext a mitad de consulta (reportado en producción vía
            // Sentry: ObjectDisposedException sobre IServiceProvider,
            // mismo patrón exacto de pila que causó la carga en frío
            // colgada de Trabajador 360).
            if (RendererInfo.IsInteractive)
                _ = CargarCanalesAsync();
        }
        catch (Exception)
        {
            if (carga == _cargaDetalle)
                _error = true;
        }
        finally
        {
            if (carga == _cargaDetalle)
                _cargando = false;
        }
    }

    /// <summary>
    /// Copia de una credencial del canal (DEC-53/DEC-62): la petición al servidor
    /// solo se hace en el momento del clic explícito, nunca al abrir la página, y
    /// el valor no se pinta — va del servidor al portapapeles. La consulta está
    /// marcada como de secretos y usa el alcance de GESTIÓN del Centro.
    /// </summary>
    private Func<Task<string?>> CopiarCredencial(Guid canalId, bool contrasena) => async () =>
    {
        var centroId = CentroId;
        CredencialCanalGestionDto? credencial;
        try
        {
            credencial = await Mediator.Send(new ObtenerCredencialCanalGestionQuery(centroId, canalId), _cancelacion);
        }
        catch (SegundoFactorRequeridoParaCredencialesException)
        {
            if (centroId == CentroId)
                SegundoFactorParaCredenciales.IrAConfigurar(NavigationManager);
            return null;
        }
        if (centroId != CentroId) return null;

        var valor = contrasena ? credencial?.Contrasena : credencial?.Usuario;
        if (string.IsNullOrEmpty(valor))
        {
            ToastService.Mostrar(contrasena ? "No hay ninguna contraseña guardada." : "No hay ningún usuario guardado.", TonoToast.Info);
            return null;
        }

        return valor;
    };

    /// <summary>Aparte del resto: "Canales de gestión" se muestra en Ficha, no debe bloquear el render de la cabecera ni del cuerpo si tarda o falla.</summary>
    private async Task CargarCanalesAsync()
    {
        var carga = ++_cargaCanales;
        var centroId = CentroId;
        _cargandoCanales = true;
        try
        {
            var canales = await Mediator.Send(new ObtenerCanalesGestionDeCentroQuery(centroId), _cancelacion);
            if (carga != _cargaCanales) return;
            _canales = canales;
        }
        catch (Exception)
        {
            if (carga == _cargaCanales)
                _canales = [];
        }
        finally
        {
            if (carga == _cargaCanales)
            {
                _cargandoCanales = false;
                StateHasChanged();
            }
        }
    }

    /// <summary>
    /// Al navegar fuera se invalida lo que estuviera en vuelo y se cancela la
    /// consulta emitida. La cancelación llega como una excepción que el catch
    /// de cada carga ya descarta, porque su contador dejó de ser el vigente.
    /// Solo <see cref="IDisposable"/>: esta página no implementa
    /// <c>IAsyncDisposable</c>, así que este es el único que Blazor llama.
    /// </summary>
    public void Dispose()
    {
        InvalidarCargas();
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    private bool _confirmarEliminarVisible;
    private bool _eliminando;

    /// <summary>
    /// D-16: baja lógica del Centro desde su ficha (mismo EliminarCentroCommand que la baja en
    /// lote de /centros: oculta el Centro y da de baja sus asignaciones activas). El aviso ofrece
    /// «Deshacer» (RestaurarCentroCommand), como Empresa.
    /// </summary>
    private async Task ConfirmarEliminarAsync()
    {
        if (_eliminando) return;
        _eliminando = true;
        var id = CentroId;
        var nombre = _detalle?.Nombre ?? string.Empty;

        try
        {
            var resultado = await Mediator.Send(new EliminarCentroCommand(id));
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            WorkspaceService.RetirarSiEstaAbierto(EntidadWorkspace.Centro, [id]);
            _confirmarEliminarVisible = false;
            ToastService.Mostrar(Textos["ToastCentroEliminado", nombre].Value, TonoToast.Exito, "Deshacer", () => DeshacerEliminarAsync(id));
            NavigationManager.NavigateTo("/centros");
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["ToastCentroEliminadoError"].Value, TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }

    private async Task DeshacerEliminarAsync(Guid id)
    {
        var resultado = await Mediator.Send(new RestaurarCentroCommand(id));
        if (resultado.EsFallido)
            ToastService.MostrarError(resultado.Error);
        else
            ToastService.Mostrar(Textos["ToastCentroRestaurado"].Value, TonoToast.Exito);
    }

    private void IrABreadcrumb(int indice)
    {
        if (indice == 0)
            NavigationManager.NavigateTo("/centros");
    }

    private void AbrirInformacion() =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Centro, CentroId, _detalle?.Nombre ?? string.Empty, "informacion");

    private void AbrirPlataforma() =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Centro, CentroId, _detalle?.Nombre ?? string.Empty, "plataforma");

    private void AbrirClienteEmpresarial() =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Cliente, _detalle!.ClienteId, _detalle.ClienteRazonSocial, "informacion");

    private void ProgramarVisita() =>
        NavigationManager.NavigateTo($"/visitas?centroId={CentroId}");

    /// <summary>
    /// "Generar informe del centro" del mockup no describe un informe nuevo —
    /// /reportes ya genera Vigencia documental/Incidencias/Asignaciones con
    /// alcance por Centro (GenerarInformeVigenciaQuery/GenerarInformeAsignacionesQuery,
    /// ambas con CentroId). Es un deep-link que preselecciona cliente y centro,
    /// mismo patrón que "Desglosar por cliente" de Inicio.
    /// </summary>
    private void GenerarInformeDelCentro() =>
        NavigationManager.NavigateTo($"/reportes?clienteId={_detalle!.ClienteId}&centroId={CentroId}");

    private IReadOnlyList<DocumentacionEmpresaCentroDto> _documentacionEmpresas = [];
    private static bool BloqueaPlataforma(DocumentoEmpresaCentroDto documento) =>
        documento.AcreditacionesEnElCentro?.Any(a => a.Estado is EstadoAcreditacion.Rechazada or EstadoAcreditacion.PendienteDeSubir) == true;

    private string TextoPlataforma(EstadoAcreditacion estado) => Textos[estado switch
    {
        EstadoAcreditacion.Rechazada => "PlataformaRechazado360",
        EstadoAcreditacion.PendienteDeSubir => "PlataformaPendienteSubir360",
        EstadoAcreditacion.Subida => "PlataformaPendienteValidacion360",
        EstadoAcreditacion.Aceptada => "PlataformaValidado360",
        EstadoAcreditacion.NoRequerida => "PlataformaNoExigido360",
        _ => "PlataformaSinDatos360"
    }];

    private static TonoBadge TonoPlataforma(EstadoAcreditacion estado) => estado switch
    {
        EstadoAcreditacion.Rechazada or EstadoAcreditacion.PendienteDeSubir => TonoBadge.Peligro,
        EstadoAcreditacion.Subida => TonoBadge.Advertencia,
        EstadoAcreditacion.Aceptada => TonoBadge.Exito,
        _ => TonoBadge.Neutro
    };
    private IReadOnlyList<DocumentacionRequeridaCentroDto> _requisitosCentro = [];
    private Guid? _lecturaDocumentacionCentro;
    private Guid? _lecturaRequisitosCentro;
    private bool _cargandoLecturasFase2;
    private bool _errorLecturasFase2;

    private async Task CargarLecturasFase2Async()
    {
        if (_detalle is null || _pestana is not (PestanaDocumentacionCentro or PestanaRequisitos)) return;
        var id = CentroId;
        var carga = _cargaDetalle;
        var documentacion = _pestana == PestanaDocumentacionCentro;
        if ((documentacion ? _lecturaDocumentacionCentro : _lecturaRequisitosCentro) == id) return;
        if (documentacion) _lecturaDocumentacionCentro = id; else _lecturaRequisitosCentro = id;
        _cargandoLecturasFase2 = true;
        _errorLecturasFase2 = false;
        try
        {
            if (documentacion)
            {
                var resultado = await Mediator.Send(new ObtenerDocumentacionEmpresasDeCentroQuery(id), _cancelacion);
                if (carga != _cargaDetalle) return;
                _documentacionEmpresas = resultado;
            }
            else
            {
                var resultado = await Mediator.Send(new ObtenerDocumentacionRequeridaDeCentroQuery(id), _cancelacion);
                if (carga != _cargaDetalle) return;
                _requisitosCentro = _detalle.GestionCae == ModalidadGestionCae.SinGestionCae ? [] : (resultado ?? []).Where(r => r.Aplica).ToList();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (carga == _cargaDetalle) _errorLecturasFase2 = true;
        }
        finally
        {
            if (carga == _cargaDetalle) _cargandoLecturasFase2 = false;
        }
    }

    /// <summary>
    /// Tras anotar a mano un estado en la plataforma (o deshacerlo) en «Documentación Empresa»: se vuelve a leer el Centro
    /// (contadores de pestañas, bloqueos) y la tabla, para recalcular el bloqueo, el tinte y el orden de cada fila.
    /// </summary>
    private async Task RecargarTrasCambioPlataformaAsync()
    {
        _lecturaDocumentacionCentro = null;
        await CargarAsync();
        await CargarLecturasFase2Async();
    }

    private Task AbrirEmpresaDocumentacion(DocumentacionEmpresaCentroDto empresa) =>
        WorkspaceService.AbrirAsync(empresa.DesdeRelacionSubcontrata ? EntidadWorkspace.Subcontrata : EntidadWorkspace.Empresa,
            empresa.EmpresaId, empresa.Nombre, "documentacion");
    private void IrAEmpresaDocumentacion(DocumentacionEmpresaCentroDto empresa) =>
        NavigationManager.NavigateTo($"/{(empresa.DesdeRelacionSubcontrata ? "subcontratas" : "empresas")}/{empresa.EmpresaId}");
    private void IrATipoDocumento(Guid id) => NavigationManager.NavigateTo($"/documentos/tipos/{id}");

    // ---------- Nota interna (mismo patrón que Empresa 360, editor en NotaInternaEditable) ----------

    /// <summary>Manda solo la nota y la <c>Version</c> que la ficha leyó.</summary>
    private Task<CaeManager.Domain.Common.Result> GuardarNotaInternaAsync(string nota) =>
        Mediator.Send(new CaeManager.Application.Centros.Commands.GuardarNotaInternaCentro
            .GuardarNotaInternaCentroCommand(_detalle!.Id, nota, _detalle.Version));

    /// <summary>
    /// Relee solo la cabecera (nota y Version) sin el spinner de CargarAsync, que rehará la ficha y perdería el
    /// borrador del editor. Si falla, la ficha se queda con lo que ya enseñaba: el siguiente guardado dará conflicto.
    /// </summary>
    private async Task RecargarTrasNotaInternaAsync()
    {
        var generacion = _generacion;
        var centroId = _detalle?.Id;
        if (centroId is null) return;

        try
        {
            var detalle = await Mediator.Send(new ObtenerCentroPorIdQuery(centroId.Value), _cancelacion);
            if (generacion == _generacion && detalle is not null && detalle.Id == _detalle?.Id)
                _detalle = detalle;
        }
        catch (Exception)
        {
            // Sin relectura la Version queda vieja: el siguiente guardado dará conflicto y releerá.
        }
    }
}
