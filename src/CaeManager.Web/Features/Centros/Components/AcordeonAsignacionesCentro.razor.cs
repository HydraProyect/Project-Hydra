using CaeManager.Domain.Common;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Alertas;
using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Asignaciones.Commands.CrearAsignaciones;
using CaeManager.Application.Asignaciones.Commands.DarDeBajaAsignaciones;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Asignaciones.Queries.ObtenerDocumentosFaltantesParaAsignacion;
using CaeManager.Application.Asignaciones.Queries.ObtenerTrabajadoresVisitaSinAsignacion;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionVehiculosDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerTrabajadoresAsignadosDeCentro;
using CaeManager.Web.Components.Workspace;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Centros.Recursos;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Features.Documentos.Components;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Centros.Components;

public partial class AcordeonAsignacionesCentro : ComponentBase, IDisposable
{
    private Modal? _modalBajaLote;
    [Inject] private ContextWorkspaceService WorkspaceService { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosCentros> Textos { get; set; } = default!;

    [Parameter, EditorRequired] public Guid CentroId { get; set; }
    [Parameter, EditorRequired] public string CentroNombre { get; set; } = string.Empty;

    /// <summary>Próxima visita activa del centro (Centros.razor la resuelve en lote) — null si no tiene ninguna.</summary>
    [Parameter] public Guid? VisitaId { get; set; }
    [Parameter] public DateOnly? VisitaFechaFin { get; set; }

    /// <summary>Empresa titular del centro, sujeto del ámbito Empresa (OD-13).</summary>
    [Parameter] public Guid EmpresaId { get; set; }
    /// <summary>
    /// Presentación de Centro 360 (maqueta del 20-10): estado en la plataforma por documento y, con ella, la lista de la maqueta —
    /// filtros «Todos» y «Bloquea acceso», la Empresa empleadora junto al nombre y sin baja en lote («no decidido»).
    /// </summary>
    [Parameter] public bool MostrarEstadoPlataforma { get; set; }

    /// <summary>Empresa empleadora de cada Trabajador del Centro, solo en la presentación de Centro 360; vacía si la lectura falla.</summary>
    private IReadOnlyDictionary<Guid, string> _empleadoresPorTrabajador = new Dictionary<Guid, string>();

    private string? EmpleadorDe(Guid trabajadorId) =>
        MostrarEstadoPlataforma && _empleadoresPorTrabajador.TryGetValue(trabajadorId, out var empleador) ? empleador : null;
    /// <summary>Plataforma CAE del primer canal de gestión por plataforma del Centro; rotula la columna de estado.</summary>
    [Parameter] public string? NombrePlataforma { get; set; }
    /// <summary>Barra de búsqueda de la página; recibe si la vista es «Vehículos» para rotular el buscador.</summary>
    [Parameter] public RenderFragment<bool>? Busqueda { get; set; }

    // Internos: la vista «Vehículos» de Centro 360 (VehiculosCentro360) pinta la misma tabla de plataforma.
    internal static bool BloqueaPlataforma(DocumentoRequeridoDto documento) =>
        documento.AcreditacionesEnElCentro?.Any(a => a.Estado is EstadoAcreditacion.Rechazada or EstadoAcreditacion.PendienteDeSubir) == true;
    internal static bool TienePendienteValidacion(DocumentoRequeridoDto documento) =>
        documento.AcreditacionesEnElCentro?.Any(a => a.Estado == EstadoAcreditacion.Subida) == true;
    internal static bool TieneIncidenciaEnPlataforma(IReadOnlyList<DocumentoRequeridoDto> documentos) =>
        (documentos.Count > 0 && !documentos.Any(d => d.AcreditacionesEnElCentro is { Count: > 0 }))
        || documentos.Any(d => BloqueaPlataforma(d) || TienePendienteValidacion(d));
    internal static int OrdenPlataforma(DocumentoRequeridoDto documento) =>
        BloqueaPlataforma(documento) ? 0 : TienePendienteValidacion(documento) ? 1 : 2;
    private string TextoPlataforma(EstadoAcreditacion estado) => Textos[ClaveTextoPlataforma(estado)];
    internal static string ClaveTextoPlataforma(EstadoAcreditacion estado) => estado switch
    {
        EstadoAcreditacion.Rechazada => "PlataformaRechazado360",
        EstadoAcreditacion.PendienteDeSubir => "PlataformaPendienteSubir360",
        EstadoAcreditacion.Subida => "PlataformaPendienteValidacion360",
        EstadoAcreditacion.Aceptada => "PlataformaValidado360",
        EstadoAcreditacion.NoRequerida => "PlataformaNoExigido360",
        _ => "PlataformaSinDatos360"
    };
    internal static TonoBadge TonoPlataforma(EstadoAcreditacion estado) => estado switch
    {
        EstadoAcreditacion.Rechazada or EstadoAcreditacion.PendienteDeSubir => TonoBadge.Peligro,
        EstadoAcreditacion.Subida => TonoBadge.Advertencia,
        EstadoAcreditacion.Aceptada => TonoBadge.Exito,
        _ => TonoBadge.Neutro
    };

    [Parameter] public string EmpresaNombre { get; set; } = string.Empty;

    /// <summary>
    /// Incidencias del ámbito Empresa, ya calculadas por la fila de Centro. No
    /// se vuelven a consultar: son las mismas causas que decidieron el estado
    /// del centro, filtradas por ámbito. El acordeón solo las presenta.
    /// </summary>
    [Parameter] public IReadOnlyList<IncidenciaCentroDto> IncidenciasEmpresa { get; set; } = [];

    /// <summary>
    /// Se dispara tras guardar un documento o una asignación in situ —
    /// Centros.razor vuelve a pedir esta fila (ver RefrescarCentroAsync).
    /// Un nombre solo, no uno por tipo de guardado: al padre le basta con
    /// saber "algo de este centro cambió", no distinguir la causa.
    /// </summary>
    [Parameter] public EventCallback OnCambio { get; set; }

    /// <summary>
    /// "Selección múltiple" de la página (Centros.razor) — los checkboxes de
    /// fila de Trabajador solo se pintan con esto activo, mismo criterio que
    /// ya aplica la fila de Centro (§ 0.9): son ruido permanente para una
    /// acción ocasional, no algo "de serie".
    /// </summary>
    [Parameter] public bool SeleccionMultiple { get; set; }

    /// <summary>
    /// Vista previa para la fila de /centros (hallazgo en vivo 2026-08-16):
    /// con un centro de plantilla grande, desplegar TODOS los trabajadores
    /// dentro de la lista convertía la fila en una lista interminable — peor
    /// aún si además se abre la tabla de documentos de cada uno. Activo,
    /// solo se listan los trabajadores con alguna incidencia (PeorEstado
    /// distinto de Vigente); Centro 360 (la página propia, mockup "Centro
    /// 360 TALVEG") sigue mostrando la lista completa de trabajadores con
    /// actividad, incidencia o no — es la que conserva los tres niveles sin
    /// recortar.
    /// </summary>
    [Parameter] public bool SoloIncidencias { get; set; }

    /// <summary>
    /// Modo listado de la fila desplegada de <c>/centros</c>: solo las filas de
    /// Trabajador con incidencia (nombre, estado, motivo y «Gestionar»), sin la
    /// barra «+ Asignar trabajador» / «Dar de baja seleccionados», sin el
    /// buscador propio y sin casillas de selección por trabajador (maqueta
    /// v29: la fila desplegada no las tiene). Las altas y bajas viven en la
    /// página de Centro 360, que mantiene el modo por defecto (false).
    /// </summary>
    [Parameter] public bool ModoListado { get; set; }

    /// <summary>
    /// Si el acordeón pinta sus propios caminos a la ficha del centro
    /// (<c>/centros/{id}</c>): «Ver los N en Centro 360» en el estado «Todos al
    /// día» y «Ver Centro 360 →» junto a la nota de trabajadores al día
    /// ocultos. Por defecto sí, para que cualquier consumidor que no lo diga
    /// quede como estaba. La lista de /centros lo apaga porque la misma ficha ya
    /// se alcanza con el icono 360 del final de la fila, que está con o sin
    /// desplegar (la maqueta retiró «Ver el centro completo»): con los dos, la
    /// misma fila ofrecía dos caminos al mismo destino. Apagado, el texto
    /// informativo (título «Todos al día», recuento de ocultos) se conserva;
    /// solo desaparece el botón.
    /// </summary>
    [Parameter] public bool MostrarEnlacesCentro360 { get; set; } = true;

    /// <summary>
    /// D-30: «+ Asignar trabajador» es la primaria del cuerpo de Centro 360 (mockup «Centro 360
    /// TALVEG»). Dentro de la fila expandida de /centros el mockup de la lista no la tiene, y
    /// repetida en cada centro expandido (×6) competía con la primaria de la cabecera: allí baja a
    /// secundaria.
    /// </summary>
    [Parameter] public bool AsignarEsPrimaria { get; set; } = true;

    /// <summary>
    /// Búsqueda por nombre gobernada por el consumidor — la barra de trabajo de
    /// Centro 360 («Centro 360 TALVEG.dc.html», <c>data-workbar</c>), que la
    /// pinta arriba y la conserva en la URL. <c>null</c> significa que nadie la
    /// gobierna: entonces el acordeón pinta su propio buscador, como en la fila
    /// de <c>/centros</c>.
    /// </summary>
    [Parameter] public string? FiltroTexto { get; set; }

    /// <summary>
    /// Filtro por el peor estado del Trabajador, gobernado por el consumidor, con los valores
    /// de <see cref="OpcionesFranja"/>. «Bloqueado» prevalece sobre la documentación; la tolerancia
    /// conserva su estado calculado y no se vuelve a clasificar como vencida. Vacío o desconocido no filtra
    /// nada: la lista completa es el desenlace honesto de un filtro que no se
    /// entiende, no una lista vacía.
    /// </summary>
    [Parameter] public string? FiltroEstado { get; set; }
    [Parameter] public EventCallback<string?> FiltroEstadoChanged { get; set; }
    [Parameter] public EventCallback<(Guid CentroId, int? Trabajadores)> OnResumenCargado { get; set; }

    /// <summary>
    /// Pie con los totales documentales de la lista («Totales del centro» del
    /// mockup). Solo tiene sentido con la lista completa: en la vista previa de
    /// <c>/centros</c> lo que se ve está recortado a incidencias y un total
    /// junto a una lista recortada se leería como el total de lo que se ve.
    /// </summary>
    [Parameter] public bool MostrarTotales { get; set; }

    /// <summary>
    /// Los bloqueos de acceso de los Trabajadores de ESTE Centro (la regla única por Centro): marcan «Bloqueado» al Trabajador en su fila y alimentan su motivo con «Pedir» (<see cref="MotivoBloqueoTrabajador"/>).
    /// «Bloqueado» es un estado del Trabajador, nunca del Centro. Si el llamador ya los tiene (Centro 360)
    /// los pasa; si es <c>null</c> (la lista de Centros) el acordeón los carga él mismo, para que la fila
    /// del Trabajador diga lo mismo en las dos superficies.
    /// </summary>
    [Parameter] public IReadOnlyList<CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente.DocumentacionBloqueantePendienteDto>? Bloqueos { get; set; }

    private IReadOnlyList<CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente.DocumentacionBloqueantePendienteDto> _bloqueosPropios = [];

    private IReadOnlyList<CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente.DocumentacionBloqueantePendienteDto> BloqueosEfectivos =>
        Bloqueos ?? _bloqueosPropios;

    /// <summary>
    /// «Bloqueado» del Trabajador: un bloqueo de acceso del Centro o, en Centro 360, un documento que bloquea en la plataforma
    /// CAE del Cliente (Rechazado o Pendiente de subir, bloque 3 del 21-10). Con la misma regla cuentan la fila, su pastilla
    /// «Bloquea acceso», el orden y el filtro con su contador: un rechazo de la plataforma deja al Trabajador bloqueado.
    /// </summary>
    private bool EstaBloqueado(Guid trabajadorId) =>
        BloqueosEfectivos.Any(b => b.TrabajadorId == trabajadorId) || BloqueaEnPlataforma(trabajadorId);

    private bool BloqueaEnPlataforma(Guid trabajadorId) =>
        MostrarEstadoPlataforma && _trabajadores.Any(t => t.TrabajadorId == trabajadorId && t.Documentos.Any(BloqueaPlataforma));

    private IReadOnlyList<DocumentacionBloqueantePendienteDto> BloqueosDe(Guid trabajadorId) =>
        BloqueosEfectivos.Where(b => b.TrabajadorId == trabajadorId).ToList();

    /// <summary>
    /// Lo que el Centro dice de este Trabajador y todavía no bloquea (urgente, próximo, en tolerancia, sin confirmar), sin repetir
    /// los Tipos que ya salen como bloqueo. Solo filtra estados que ya calculó el Centro: no decide nada.
    /// </summary>
    private IReadOnlyList<DocumentoRequeridoDto> OtrosPendientesDe(TrabajadorAsignacionDocumentacionDto trabajador)
    {
        var tiposBloqueantes = BloqueosEfectivos.Where(b => b.TrabajadorId == trabajador.TrabajadorId).Select(b => b.TipoDocumentoId).ToHashSet();
        return trabajador.Documentos
            .Where(d => MotivoBloqueoTrabajador.EsPendienteQueNoBloquea(d.Estado) && !tiposBloqueantes.Contains(d.TipoDocumentoId))
            .ToList();
    }

    private bool _pedirVisible;
    private AmbitoAplicacion? _pedirAmbito;
    private Guid? _pedirEntidadId;

    /// <summary>«Pedir»: abre el selector de reclamación ya apuntado a la entidad; no envía nada por sí solo.</summary>
    private Task PedirAsync(MotivoBloqueoTrabajador.EntidadAPedir entidad)
    {
        _pedirAmbito = entidad.Ambito;
        _pedirEntidadId = entidad.EntidadId;
        _pedirVisible = true;
        return Task.CompletedTask;
    }

    private string DescripcionBloqueo(Guid trabajadorId) =>
        string.Join("; ", BloqueosEfectivos.Where(b => b.TrabajadorId == trabajadorId).Select(b => TextoBloqueoDeAcceso.Linea(Textos, b)));

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private bool _cargando = true;
    private bool _errorCarga;
    private IReadOnlyList<TrabajadorAsignacionDocumentacionDto> _trabajadores = [];
    private string _busquedaTrabajador = string.Empty;
    private readonly HashSet<Guid> _seleccionados = [];
    private bool _seleccionMultipleAnterior;

    /// <summary>
    /// Centro que esta instancia tiene cargado. Centro 360 es una sola ruta
    /// (<c>/centros/{id}</c>) y Blazor reutiliza ESTA instancia al pasar de un
    /// centro a otro, así que sin esto la lista del primero seguiría en
    /// pantalla bajo el nombre del segundo.
    /// </summary>
    private Guid? _centroCargado;

    /// <summary>
    /// Generación del centro que se enseña. Cambiar de centro y retirar el
    /// componente la suben, junto con el contador de carga: cada respuesta
    /// compara el suyo antes de escribir y, si ya no es el vigente, se
    /// descarta. Mismo mecanismo que <c>SubcontrataWorkspacePanel</c>.
    /// </summary>
    private int _generacion;
    private int _cargaLista;

    /// <summary>
    /// Los contadores impiden que una respuesta tardía escriba; esto corta la
    /// consulta misma. Todas las consultas de carga llevan su token y
    /// <see cref="Dispose"/> lo cancela. Los comandos NO lo llevan: cambiar de
    /// centro no deshace una baja que el usuario ya pidió. El token se copia al
    /// inicializar porque leer <c>Token</c> de un
    /// <see cref="CancellationTokenSource"/> ya desechado lanza.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private CancellationToken _cancelacion;

    /// <summary>
    /// Qué filas de Trabajador tienen su tabla de documentos abierta (variante
    /// A, OD-12 cerrada). Estado propio del componente, no de
    /// <c>SeccionColapsable</c> (contrato de fidelidad 2026-08-09 § 1.2/1.4):
    /// esa composición queda prohibida como base visual de esta fila, aunque
    /// el mecanismo de expandir/colapsar en sí — el mismo bool toggle — sí se
    /// reutiliza, igual que ya hace la fila de Centro con su propio HashSet.
    /// </summary>
    private readonly HashSet<Guid> _expandidosTrabajador = [];

    /// <summary>
    /// Fila plegable de la Empresa. En Centro 360 va desplegada de entrada, porque el bloque solo existe cuando hay incidencias que
    /// resolver. Dentro de la fila desplegada de /centros (ModoListado) nace contraída, como todo lo desplegable de los listados
    /// (decisión 2026-10-21, #1530): se abre con su chevron.
    /// </summary>
    private bool _empresaDesplegada = true;

    private IReadOnlyList<TrabajadorSinAsignacionDto> _trabajadoresVisitaSinAsignacion = [];
    private readonly HashSet<Guid> _asignandoDesdeVisita = [];

    private bool _confirmarBajaLoteVisible;
    private string _fechaBajaLote = string.Empty;
    private readonly InstantaneaFormulario _instantaneaBajaLote = new();
    private bool _procesandoBajaLote;

    private DrawerAsignacionMasiva _drawerAsignacion = default!;

    protected override void OnInitialized()
    {
        _cancelacion = _ciclo.Token;
        _empresaDesplegada = !ModoListado;
    }

    /// <summary>
    /// Apagar "Selección múltiple" limpia la selección de trabajadores — mismo
    /// criterio que Centros.razor.cs.AlternarSeleccionMultiple. Y cambiar de
    /// centro reinicia el componente entero: en Centro 360 esta instancia se
    /// reutiliza, y nada preparado para un centro puede quedar preparado sobre
    /// otro.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        if (_seleccionMultipleAnterior && !SeleccionMultiple)
            _seleccionados.Clear();
        _seleccionMultipleAnterior = SeleccionMultiple;

        if (_centroCargado == CentroId)
            return;

        _centroCargado = CentroId;
        ReiniciarParaNuevoCentro();
        await CargarAsync();
    }

    /// <summary>Invalida cualquier carga en vuelo: su respuesta ya no escribirá nada.</summary>
    private void InvalidarCargas()
    {
        _generacion++;
        _cargaLista++;
    }

    /// <summary>
    /// Lo preparado para el centro anterior se tira, no se arrastra: la modal
    /// de baja en lote se cierra, la selección de asignaciones —Ids del centro
    /// anterior— se vacía, y el filtro propio, las filas expandidas y las
    /// asignaciones rápidas en curso dejan de hablar de una lista que ya no es
    /// esta.
    /// </summary>
    private void ReiniciarParaNuevoCentro()
    {
        InvalidarCargas();
        _trabajadores = [];
        _trabajadoresVisitaSinAsignacion = [];
        _bloqueosPropios = [];
        _vehiculos = [];
        _errorVehiculos = false;
        _verVehiculos = false;
        _subcontratasAbiertas.PlegarTodos();
        _errorCarga = false;
        _seleccionados.Clear();
        _expandidosTrabajador.Clear();
        _asignandoDesdeVisita.Clear();
        _busquedaTrabajador = string.Empty;
        _confirmarBajaLoteVisible = false;
        _procesandoBajaLote = false;
        _fechaBajaLote = string.Empty;
    }

    private async Task CargarAsync()
    {
        var carga = ++_cargaLista;
        var centroId = CentroId;
        var visitaId = VisitaId;
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();
        await OnResumenCargado.InvokeAsync((centroId, null));

        try
        {
            var trabajadores = await Mediator.Send(
                new ObtenerAsignacionesDocumentacionPorCentroQuery(centroId, VisitaFechaFin), _cancelacion);
            if (carga != _cargaLista) return;
            _trabajadores = trabajadores;
            _seleccionados.Clear();

            IReadOnlyList<TrabajadorSinAsignacionDto> sinAsignacion = visitaId is { } id
                ? await Mediator.Send(new ObtenerTrabajadoresVisitaSinAsignacionQuery(id, centroId), _cancelacion)
                : [];
            if (carga != _cargaLista) return;
            _trabajadoresVisitaSinAsignacion = sinAsignacion;
            if (MostrarEstadoPlataforma)
            {
                await CargarEmpleadoresAsync(carga, centroId);
                if (!ModoListado)
                    await CargarVehiculosAsync(carga, centroId);
            }
            await OnResumenCargado.InvokeAsync((centroId, _trabajadores.Count));
        }
        catch (Exception)
        {
            if (carga == _cargaLista)
                _errorCarga = true;
        }
        finally
        {
            if (carga == _cargaLista)
            {
                _cargando = false;
                StateHasChanged();
            }
        }

        if (Bloqueos is null)
            await CargarBloqueosPropiosAsync(carga, centroId);
    }

    /// <summary>La Empresa empleadora es un dato de apoyo de la fila: si no se puede leer, la fila se enseña sin ella.</summary>
    private async Task CargarEmpleadoresAsync(int carga, Guid centroId)
    {
        try
        {
            var asignados = await Mediator.Send(new ObtenerTrabajadoresAsignadosDeCentroQuery(centroId), _cancelacion);
            if (carga != _cargaLista) return;
            _empleadoresPorTrabajador = asignados
                .GroupBy(a => a.TrabajadorId)
                .ToDictionary(g => g.Key, g => g.First().EmpleadorNombre);
            _subcontrataPorTrabajador = asignados
                .Where(a => a.SubcontrataId is not null)
                .GroupBy(a => a.TrabajadorId)
                .ToDictionary(g => g.Key, g => (g.First().SubcontrataId!.Value, g.First().EmpleadorNombre, g.First().EmpresaContratistaNombre));
        }
        catch (Exception)
        {
            if (carga == _cargaLista)
            {
                _empleadoresPorTrabajador = new Dictionary<Guid, string>();
                _subcontrataPorTrabajador = new Dictionary<Guid, (Guid, string, string?)>();
            }
        }
    }

    /// <summary>
    /// Los Vehículos de la vista «Vehículos» de Centro 360. Si no se pueden leer, la vista lo dice; la de Trabajadores
    /// no se ve afectada.
    /// </summary>
    private async Task CargarVehiculosAsync(int carga, Guid centroId)
    {
        try
        {
            var vehiculos = await Mediator.Send(new ObtenerDocumentacionVehiculosDeCentroQuery(centroId), _cancelacion);
            if (carga != _cargaLista) return;
            _vehiculos = vehiculos;
            _errorVehiculos = false;
        }
        catch (Exception)
        {
            if (carga == _cargaLista)
            {
                _vehiculos = [];
                _errorVehiculos = true;
            }
        }
    }

    // ── Centro 360, bloque 4 del 21-10: Trabajadores | Vehículos y Subcontratas desplegables ──
    private IReadOnlyDictionary<Guid, (Guid SubcontrataId, string Nombre, string? Contratista)> _subcontrataPorTrabajador =
        new Dictionary<Guid, (Guid, string, string?)>();
    private IReadOnlyList<VehiculoDocumentacionCentroDto> _vehiculos = [];
    private bool _errorVehiculos;
    private bool _verVehiculos;
    private readonly GruposAbiertos _subcontratasAbiertas = new();

    private bool VerVehiculos => MostrarEstadoPlataforma && !ModoListado && _verVehiculos;

    private void ElegirRecurso(bool vehiculos) => _verVehiculos = vehiculos;

    /// <summary>El buscador de la barra de Centro 360 también filtra los Vehículos: matrícula, nombre, modelo o titular.</summary>
    private IReadOnlyList<VehiculoDocumentacionCentroDto> VehiculosFiltrados =>
        string.IsNullOrWhiteSpace(TextoBusqueda) ? _vehiculos
        : _vehiculos.Where(v => $"{v.NumeroPlaca} {v.Nombre} {v.Modelo} {v.EmpleadorNombre}".Contains(TextoBusqueda, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Una entrada de la lista de Trabajadores: un Trabajador o la cabecera de una Subcontrata.</summary>
    private sealed record EntradaLista(TrabajadorAsignacionDocumentacionDto? Trabajador, GrupoSubcontrata<TrabajadorAsignacionDocumentacionDto>? Grupo = null, string? Clave = null);

    /// <summary>
    /// Fuera de Centro 360, la lista tal cual. En Centro 360, primero los Trabajadores de las Empresas del Centro y después
    /// una cabecera por Subcontrata con los suyos debajo solo si está abierta (maqueta del 21-10, bloque 4).
    /// </summary>
    private IEnumerable<EntradaLista> EntradasLista
    {
        get
        {
            if (!MostrarEstadoPlataforma || ModoListado)
                return TrabajadoresFiltrados.Select(t => new EntradaLista(t));
            var (principales, grupos) = GruposPorSubcontrata.Repartir(TrabajadoresFiltrados,
                t => _subcontrataPorTrabajador.TryGetValue(t.TrabajadorId, out var s) ? s.SubcontrataId : null,
                t => _subcontrataPorTrabajador[t.TrabajadorId].Nombre,
                t => _subcontrataPorTrabajador[t.TrabajadorId].Contratista);
            return principales.Select(t => new EntradaLista(t)).Concat(grupos.SelectMany(g =>
            {
                var clave = "trab:" + g.SubcontrataId;
                var cabecera = new EntradaLista(null, g, clave);
                return _subcontratasAbiertas.EstaAbierto(clave)
                    ? g.Filas.Select(t => new EntradaLista(t)).Prepend(cabecera)
                    : [cabecera];
            }));
        }
    }

    /// <summary>La pastilla «N con incidencias» de una Subcontrata: Trabajadores sin datos de plataforma, con bloqueo o pendientes de validación.</summary>
    private bool TrabajadorConIncidenciaEnPlataforma(TrabajadorAsignacionDocumentacionDto trabajador) =>
        EstaBloqueado(trabajador.TrabajadorId) || TieneIncidenciaEnPlataforma(trabajador.Documentos);

    /// <summary>
    /// Los bloqueos son un adorno de la fila: si no se pueden leer, la fila se enseña sin el distintivo
    /// y la lista no falla (mismo criterio que Centro 360).
    /// </summary>
    private async Task CargarBloqueosPropiosAsync(int carga, Guid centroId)
    {
        try
        {
            var bloqueos = await Mediator.Send(new ObtenerDocumentacionBloqueantePendienteQuery(centroId), _cancelacion);
            if (carga != _cargaLista) return;
            _bloqueosPropios = bloqueos;
            StateHasChanged();
        }
        catch (Exception)
        {
            // Fail-soft: sin bloqueos la fila se pinta igual.
        }
    }

    /// <summary>
    /// Retirado el componente —cerrada la fila, o Centro 360 navegando fuera—
    /// se invalida lo que estuviera en vuelo y se cancela la consulta emitida:
    /// deja de ocupar el DbContext del circuito. La cancelación llega como una
    /// excepción que el catch de <see cref="CargarAsync"/> ya descarta, porque
    /// su contador dejó de ser el vigente.
    /// </summary>
    public void Dispose()
    {
        InvalidarCargas();
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    /// <summary>
    /// "Asignación rápida desde visita" (§ 0.3): el trabajador ya está
    /// identificado por <c>VisitaTrabajador</c> — no hace falta un selector,
    /// solo confirmar la fecha de alta (hoy) y avisar si al comprobarlo le
    /// faltaba algún documento de los que se piden, mismo preflight no
    /// bloqueante que el drawer N×M.
    /// </summary>
    private async Task AsignarDesdeVisitaAsync(TrabajadorSinAsignacionDto trabajador)
    {
        // Guarda de reentrada, no el botón: Boton conserva su @onclick
        // enganchado aunque esté disabled, y el segundo clic ya viajaba cuando
        // el primero puso "Cargando". Add devuelve false si ya estaba dentro.
        if (!_asignandoDesdeVisita.Add(trabajador.TrabajadorId))
            return;

        var generacion = _generacion;
        StateHasChanged();

        try
        {
            var hoy = DiaDeNegocio.Hoy();
            var centroId = CentroId;
            var centroNombre = CentroNombre;
            var faltantes = await Mediator.Send(
                new ObtenerDocumentosFaltantesParaAsignacionQuery([trabajador.TrabajadorId], [centroId]), _cancelacion);

            var resultado = await Mediator.Send(new CrearAsignacionCommand(trabajador.TrabajadorId, centroId, hoy));
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            ToastService.Mostrar(
                faltantes.Count == 0
                    ? $"{trabajador.TrabajadorNombre} asignado a {centroNombre}."
                    : $"{trabajador.TrabajadorNombre} asignado a {centroNombre} — al comprobarlo le faltaban {faltantes.Count} documento(s) que se piden.",
                faltantes.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);

            // La asignación se hizo sobre el centro que estaba en pantalla al
            // pulsar; si entretanto se abrió otro, recargar aquí pintaría la
            // lista del anterior sobre el actual.
            if (generacion == _generacion)
                await CargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos asignar al trabajador. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _asignandoDesdeVisita.Remove(trabajador.TrabajadorId);
        }
    }

    /// <summary>Lista realmente pintada — recortada a incidencias en la vista previa de /centros, completa en Centro 360.</summary>
    private IReadOnlyList<TrabajadorAsignacionDocumentacionDto> TrabajadoresAMostrar =>
        SoloIncidencias ? _trabajadores.Where(t => t.PeorEstado != EstadoDocumento.Vigente).ToList() : _trabajadores;

    private int TrabajadoresOcultosAlDia => SoloIncidencias ? _trabajadores.Count - TrabajadoresAMostrar.Count : 0;

    /// <summary>
    /// Quién gobierna la búsqueda: la barra de trabajo del consumidor cuando
    /// pasa <see cref="FiltroTexto"/> (Centro 360), o el propio acordeón con su
    /// campo (la fila de <c>/centros</c>).
    /// </summary>
    private bool BusquedaGobernadaFuera => FiltroTexto is not null;

    private string TextoBusqueda => FiltroTexto ?? _busquedaTrabajador;

    /// <summary>
    /// Búsqueda por nombre y filtro por el peor estado del Trabajador sobre
    /// <see cref="TrabajadoresAMostrar"/> — client-side, sin consulta nueva.
    /// Vencido incluye En tolerancia; los demás estados son exclusivos por peor estado.
    /// Los chips comparten esta regla y, por ese solape, no suman el total:
    /// con una plantilla grande (Centro 360, SoloIncidencias=false) la lista
    /// completa no tenía ninguna forma de encontrar un trabajador concreto sin
    /// desplazarse a mano.
    /// </summary>
    private IReadOnlyList<TrabajadorAsignacionDocumentacionDto> TrabajadoresFiltrados
    {
        get
        {
            var lista = TrabajadoresAMostrar;

            if (!string.IsNullOrWhiteSpace(TextoBusqueda))
                lista = lista.Where(t => t.TrabajadorNombre.Contains(TextoBusqueda, StringComparison.OrdinalIgnoreCase)).ToList();

            var estados = SeleccionEstados.Separar(FiltroEstado).Where(v => OpcionesFranja.Any(o => o.Valores.Contains(v))).ToHashSet();
            if (estados.Count > 0)
                lista = lista.Where(t => estados.Any(estado => CoincideEstadoTrabajador(t, estado))).ToList();

            // OrderBy es estable: el lector ya desempata por apellidos y nombre.
            // No se puede reconstruir ese orden desde TrabajadorNombre (nombre + apellidos).
            return lista.OrderBy(t => EstaBloqueado(t.TrabajadorId) ? 0 : 1 + SeveridadEstadoDocumento.Rango(t.PeorEstado)).ToList();
        }
    }

    private bool HayFiltroAplicado => !string.IsNullOrWhiteSpace(TextoBusqueda) || SeleccionEstados.Separar(FiltroEstado).Count > 0;

    // Maqueta de Centro 360 (20-10): «Todos» y «Bloquea acceso». «Pendiente de validación» depende del estado por documento en
    // la plataforma (decisión 14, sin cerrar) y no se ofrece todavía.
    private IReadOnlyList<OpcionFranjaEstado> OpcionesFranja => MostrarEstadoPlataforma
        ? [new(Textos["BloqueaAccesoEtiqueta"], TonoBadge.Peligro, "Bloqueado")]
        : OpcionesFranjaCompleta;

    private IReadOnlyList<OpcionFranjaEstado> OpcionesFranjaCompleta =>
    [
        new(Textos["BloqueadoEtiqueta"], TonoBadge.Peligro, "Bloqueado"),
        new(EstadoDocumentoFicha360.Texto(EstadoDocumento.Vencido), TonoBadge.Peligro, nameof(EstadoDocumento.Vencido)),
        new(EstadoDocumentoFicha360.Texto(EstadoDocumento.Faltante), TonoBadge.Peligro, nameof(EstadoDocumento.Faltante)),
        new(EstadoDocumentoFicha360.Texto(EstadoDocumento.EnTolerancia), TonoBadge.Tolerancia, nameof(EstadoDocumento.EnTolerancia)),
        new(EstadoDocumentoFicha360.Texto(EstadoDocumento.Urgente), TonoBadge.Advertencia, nameof(EstadoDocumento.Urgente), nameof(EstadoDocumento.Proximo)),
        new(EstadoDocumentoFicha360.Texto(EstadoDocumento.SinConfirmar), TonoBadge.Advertencia, nameof(EstadoDocumento.SinConfirmar)),
        new(EstadoDocumentoFicha360.Texto(EstadoDocumento.Vigente), TonoBadge.Exito, nameof(EstadoDocumento.Vigente))
    ];

    private string EstadoTrabajador(TrabajadorAsignacionDocumentacionDto trabajador) =>
        EstaBloqueado(trabajador.TrabajadorId) ? "Bloqueado" :
        trabajador.PeorEstado == EstadoDocumento.SinCaducidad ? nameof(EstadoDocumento.Vigente) : trabajador.PeorEstado.ToString();

    private bool CoincideEstadoTrabajador(TrabajadorAsignacionDocumentacionDto trabajador, string estado)
    {
        var peorEstado = EstadoTrabajador(trabajador);
        return peorEstado == estado ||
            (peorEstado == nameof(EstadoDocumento.EnTolerancia) && estado == nameof(EstadoDocumento.Vencido));
    }

    private IReadOnlyDictionary<string, int> RecuentosFranja => OpcionesFranja.SelectMany(o => o.Valores)
        .Distinct().ToDictionary(estado => estado, estado => _trabajadores.Count(t => CoincideEstadoTrabajador(t, estado)));

    private static string InicialesTrabajador(string nombre) => string.Concat(nombre
        .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));

    private string TextoFraccionTrabajador(TrabajadorAsignacionDocumentacionDto trabajador) =>
        trabajador.Cumplimiento.Requeridos == 0 ? "—" :
        Textos["FraccionDocumental360", trabajador.Cumplimiento.AlDia, trabajador.Cumplimiento.Requeridos].Value;

    private string MotivoDocumental(TrabajadorAsignacionDocumentacionDto trabajador)
    {
        if (trabajador.Cumplimiento.Requeridos == 0) return Textos["SinExigenciasTrabajador360"];
        if (EstadoDocumentoFicha360.Clave(trabajador.PeorEstado) == EstadoDocumentoFicha360.Clave(EstadoDocumento.Vigente))
            return Textos["DocumentacionVigenteTrabajador360"];
        var documento = trabajador.Documentos.FirstOrDefault(d => d.Estado == trabajador.PeorEstado);
        return documento is null ? EstadoDocumentoFicha360.Texto(trabajador.PeorEstado) :
            documento.Estado == EstadoDocumento.Faltante ? $"{documento.TipoDocumentoNombre} · {EstadoDocumentoFicha360.Texto(documento.Estado)}" :
            $"{documento.TipoDocumentoNombre} · {TextoVigencia(documento)}" +
            (documento.EnToleranciaHasta is { } hasta ? $" · {EstadoDocumentoUi.Texto(EstadoDocumento.EnTolerancia, hasta)}" : string.Empty);
    }

    private static string NombreTrabajadorListado(string nombre)
    {
        var partes = nombre.Split(", ", 2, StringSplitOptions.TrimEntries);
        return partes.Length == 2 ? $"{partes[1]} {partes[0]}" : nombre;
    }

    // La fila compacta no repite el estado en el motivo; la gestión conserva el desglose existente.
    private string MotivoTrabajadorListado(TrabajadorAsignacionDocumentacionDto trabajador)
    {
        if (BloqueosDe(trabajador.TrabajadorId).Count > 0) return DescripcionBloqueo(trabajador.TrabajadorId);
        var documentos = trabajador.Documentos.Where(d =>
            EstadoDocumentoFicha360.Clave(d.Estado) == EstadoDocumentoFicha360.Clave(trabajador.PeorEstado)).ToList();
        if (documentos.Count > 1) return Textos["ListadoDocumentosIncidencia", documentos.Count];
        if (documentos.Count == 0) return MotivoDocumental(trabajador);
        var documento = documentos[0];
        if (documento.EnToleranciaHasta is { } hasta)
            return $"{documento.TipoDocumentoNombre} · {EstadoDocumentoUi.Texto(EstadoDocumento.EnTolerancia, hasta)}";
        if (documento.Estado is EstadoDocumento.Urgente or EstadoDocumento.Proximo && documento.FechaVencimiento is { } fecha)
        {
            var dias = fecha.DayNumber - DiaDeNegocio.Hoy().DayNumber;
            return Textos[dias <= 0 ? "ListadoDocumentoVenceHoy" : dias == 1 ? "ListadoDocumentoPorVencerSingular" : "ListadoDocumentoPorVencer",
                trabajador.Cumplimiento.AlDia, trabajador.Cumplimiento.Requeridos, documento.TipoDocumentoNombre, dias];
        }
        return documento.TipoDocumentoNombre;
    }

    private static bool PuedePedir(TrabajadorAsignacionDocumentacionDto trabajador) => trabajador.Documentos.Any(d =>
        d.Estado == EstadoDocumento.Faltante || (d.DocumentoId is not null &&
        (VentanaReclamacion.EsReclamable(d.FechaVencimiento, DiaDeNegocio.Hoy()) ||
         (d.Estado == EstadoDocumento.SinConfirmar && d.FechaVencimiento is null))));

    /// <summary>
    /// Por qué no se ve ningún trabajador. El vacío por filtro y el vacío por
    /// ausencia de registros no son el mismo hecho y no pueden contarse igual.
    /// </summary>
    private string DescripcionSinCoincidencias
    {
        get
        {
            var porTexto = string.IsNullOrWhiteSpace(TextoBusqueda) ? null : $"«{TextoBusqueda}»";
            var porEstado = OpcionesFranja.FirstOrDefault(o => o.Valores.Any(SeleccionEstados.Separar(FiltroEstado).Contains))?.Texto.ToLowerInvariant();

            return (porTexto, porEstado) switch
            {
                (not null, not null) => $"Ningún trabajador coincide con {porTexto} y su peor estado es «{porEstado}».",
                (not null, null) => $"Ningún trabajador coincide con {porTexto}.",
                (null, not null) => $"Ningún trabajador de este centro tiene como peor estado «{porEstado}».",
                _ => "Ningún trabajador coincide con el filtro."
            };
        }
    }

    /// <summary>
    /// Totales documentales de la lista completa cargada —no de la filtrada, ni
    /// de la recortada—, para el pie «Totales del centro» del mockup. Se
    /// derivan de los mismos <c>Documentos</c> ya cargados, un
    /// <c>DocumentoRequeridoDto</c> por par trabajador × tipo exigido: no hay
    /// ninguna consulta que devuelva estos totales ya sumados.
    ///
    /// <para>
    /// Cuentan SOLO la documentación de los trabajadores con asignación activa
    /// en este centro. La de la Empresa va aparte (bloque Empresa) y no entra
    /// aquí, por eso el rótulo lo dice en vez de llamarlos «del centro».
    /// </para>
    /// </summary>
    private (int Exigidos, int AlDia, int PorVencer, int Vencidos, int Faltantes, int SinValidar) TotalesDocumentales
    {
        get
        {
            // Un documento por par Trabajador × Tipo, el mejor de los que trae el listado: el listado enseña también el vencido
            // que ya tiene renovación, y contarlo aparte inflaría los vencidos frente a «exigidos», que cuenta pares.
            var documentos = _trabajadores
                .SelectMany(t => t.Documentos.GroupBy(d => d.TipoDocumentoId)
                    .Select(g => g.MaxBy(d => SeveridadEstadoDocumento.Rango(d.Estado))!))
                .ToList();
            // «Exigidos» y «al día» salen de CumplimientoDocumental (la porción de cada Trabajador del % del Centro): el
            // listado de documentos no trae los «Sin caducidad» y puede repetir un tipo (vencido y renovación).
            var cumplimiento = FraccionCumplimiento.Sumar(_trabajadores.Select(t => t.Cumplimiento));
            var vencidos = documentos.Count(d => EstadoDocumentoUi.HaVencido(d.Estado));
            var faltantes = documentos.Count(d => d.Estado == EstadoDocumento.Faltante);
            // Revisión 1:1 del 2026-10-21: las partes no sumaban el total. «Al día» ya incluye los por vencer (siguen valiendo
            // hoy) y deja fuera los «Sin confirmar» y los pendientes en la plataforma de un tipo bloqueante; el resto es eso.
            return (
                cumplimiento.Requeridos,
                cumplimiento.AlDia,
                documentos.Count(d => d.Estado is EstadoDocumento.Urgente or EstadoDocumento.Proximo),
                vencidos,
                faltantes,
                Math.Max(0, cumplimiento.Requeridos - cumplimiento.AlDia - vencidos - faltantes));
        }
    }

    private void VerCentroCompleto() => NavigationManager.NavigateTo($"/centros/{CentroId}");

    private void AlternarExpansionTrabajador(Guid asignacionId)
    {
        if (!_expandidosTrabajador.Add(asignacionId))
            _expandidosTrabajador.Remove(asignacionId);
    }

    /// <summary>El nombre y el botón 360 conservan el panel del Trabajador.</summary>
    private Task AbrirDetalleTrabajador(TrabajadorAsignacionDocumentacionDto trabajador) =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Trabajador, trabajador.TrabajadorId, trabajador.TrabajadorNombre, "informacion");

    private void AlternarSeleccion(Guid asignacionId, bool marcado)
    {
        if (marcado) _seleccionados.Add(asignacionId);
        else _seleccionados.Remove(asignacionId);
    }

    private DrawerGestionDocumento _drawerGestion = default!;

    private sealed record AcreditacionAGestionar(DocumentoRequeridoDto Documento, Guid PropietarioId, string PropietarioNombre);

    private AcreditacionAGestionar? _acreditacionAGestionar;
    private bool _panelAcreditacionVisible;

    /// <summary>
    /// «Gestionar» de un documento vigente en TALVEG: abre el panel que cambia su estado en las plataformas de este Centro.
    /// Un Centro sin plataforma para ese documento no tiene estado de acreditación que cambiar, y entonces se abre el
    /// drawer del documento, como antes.
    /// </summary>
    private Task GestionarAcreditacion(TrabajadorAsignacionDocumentacionDto trabajador, DocumentoRequeridoDto documento)
    {
        if (documento.AcreditacionesEnElCentro is not { Count: > 0 })
            return GestionarAsync(trabajador.TrabajadorId, documento);

        _acreditacionAGestionar = new AcreditacionAGestionar(documento, trabajador.TrabajadorId, trabajador.TrabajadorNombre);
        _panelAcreditacionVisible = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gestionar in situ (contrato de fidelidad 2026-08-09, item 3 del
    /// backlog): antes navegaba a /documentos, ahora abre el mismo drawer
    /// compartido sin salir de Centro 360. Un documento faltante no tiene
    /// DocumentoId todavía — abre el drawer de creación con el propietario y
    /// el tipo ya elegidos, mismo patrón que "Gestionar" en Alertas.razor.cs.
    /// </summary>
    private Task GestionarAsync(Guid trabajadorId, DocumentoRequeridoDto documento) =>
        documento.DocumentoId is { } documentoId
            ? _drawerGestion.AbrirEditarAsync(documentoId)
            : _drawerGestion.AbrirCrearParaFaltanteAsync(trabajadorId, documento.TipoDocumentoId);

    /// <summary>
    /// Mismo patrón que <see cref="GestionarAsync"/> — hoy siempre resuelve a
    /// la rama con DocumentoId (AgregarCausasDeEmpresaAsync no detecta falta
    /// total), la otra rama queda lista para cuando ese alcance se amplíe.
    /// </summary>
    private Task GestionarEmpresaAsync(IncidenciaCentroDto incidencia) =>
        incidencia.DocumentoId is { } documentoId
            ? _drawerGestion.AbrirEditarAsync(documentoId)
            : _drawerGestion.AbrirCrearParaFaltanteEmpresaAsync(EmpresaId, incidencia.TipoDocumentoId!.Value);

    /// <summary>
    /// El bloque Empresa (IncidenciasEmpresa) y el badge/estado de la fila de
    /// Centro llegan como Parameter desde Centros.razor — este componente no
    /// los puede refrescar por sí solo, así que además de recargar su propia
    /// lista de Trabajador avisa al padre (OnCambio) para que vuelva a pedir
    /// la fila del Centro. Misma reacción tras guardar un documento o una
    /// asignación: los dos cambian el estado calculado del Centro.
    /// </summary>
    private async Task ManejarDocumentoGuardadoAsync()
    {
        await CargarAsync();
        if (OnCambio.HasDelegate)
            await OnCambio.InvokeAsync();
    }

    private Task ManejarAsignacionGuardadaAsync() => ManejarDocumentoGuardadoAsync();

    private string TextoVigenciaEmpresa(IncidenciaCentroDto incidencia)
    {
        // Un rechazo en plataforma (Estado: null a propósito, ver
        // CalculoEstadoCentroService) no tiene vigencia documental que
        // describir: ni vence ni "no caduca", es una decisión activa de la
        // plataforma. "Sin caducidad" sería contradictorio junto al badge
        // "Rechazado" (hallazgo de Codex, oleada 3 sobre esta misma PR).
        if (incidencia.Estado is null)
            return Textos["VigenciaNoAplica"];

        return DetalleVigenciaDocumentoUi.DeFicha360(incidencia.Estado.Value, incidencia.FechaVencimiento, incidencia.Descripcion, deEmpresa: true);
    }

    private void AbrirConfirmarBajaLoteAsync()
    {
        _fechaBajaLote = DiaDeNegocio.Hoy().ToString("yyyy-MM-dd");
        _confirmarBajaLoteVisible = true;
        _instantaneaBajaLote.Fijar(_fechaBajaLote);
    }

    /// <summary>
    /// P1-E2b: la baja en lote solo pide la fecha, y llega puesta a hoy: dejarla así no es
    /// un cambio, pero haberla cambiado sí (es lo que se perdería al salir o al cerrar el
    /// modal con la X, Escape o el fondo).
    /// </summary>
    private bool HayCambiosSinGuardar => _confirmarBajaLoteVisible && _instantaneaBajaLote.Difiere(_fechaBajaLote);

    /// <summary>
    /// Baja en lote. Tres cosas que el botón no puede garantizar por sí solo:
    ///
    /// <para>
    /// La guarda de reentrada es de esta pantalla, no del diálogo. El lote va
    /// en un <c>Modal</c> y no en <c>DialogoConfirmacion</c> —necesita la fecha
    /// de baja—, así que la guarda de reentrada que ese diálogo trae de serie
    /// no cubre este camino; y <c>Boton</c> conserva su <c>@onclick</c>
    /// enganchado aunque esté <c>disabled</c>, de modo que el segundo clic ya
    /// viajaba cuando el primero puso «Cargando».
    /// </para>
    ///
    /// <para>
    /// El desenlace se lee del comando, no del hecho de que no fallara:
    /// <c>ResultadoBajaLoteDto</c> trae cuántas bajas hizo y qué errores
    /// parciales hubo. Cero bajas sin errores no es un logro —es que el
    /// comando no encontró nada que dar de baja— y se cuenta como aviso, no
    /// como éxito. Con errores parciales la modal se queda abierta: aún hay
    /// algo que decidir sobre lo que no se procesó.
    /// </para>
    ///
    /// <para>
    /// La selección se congela antes del await. Si mientras el comando está en
    /// vuelo cambia el centro en pantalla, el resultado ya no puede tocar esta
    /// lista: se avisa igual —la baja ocurrió— pero no se recarga ni se toca la
    /// modal del centro nuevo.
    /// </para>
    /// </summary>
    private async Task ConfirmarBajaLoteAsync()
    {
        if (_procesandoBajaLote)
            return;

        if (!DateOnly.TryParse(_fechaBajaLote, out var fechaBaja))
        {
            ToastService.Mostrar("Introduce una fecha de baja válida.", TonoToast.Error);
            return;
        }

        var seleccionados = _seleccionados.ToList();
        if (seleccionados.Count == 0)
        {
            ToastService.Mostrar("No hay ningún trabajador seleccionado.", TonoToast.Advertencia);
            return;
        }

        var generacion = _generacion;
        _procesandoBajaLote = true;

        try
        {
            var resultado = await Mediator.Send(new DarDeBajaAsignacionesCommand(seleccionados, fechaBaja));

            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            var dto = resultado.Valor;
            var (mensaje, tono) = DescribirBajaLote(dto, seleccionados.Count);
            ToastService.Mostrar(mensaje, tono);

            if (generacion != _generacion)
                return;

            // Con errores parciales la modal sigue abierta sobre lo que no se
            // procesó; cerrarla dejaría el fallo contado en un toast que se va.
            if (dto.Errores.Count == 0)
                _confirmarBajaLoteVisible = false;

            await CargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos procesar la baja. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            // Solo si esta sigue siendo la baja vigente. Al cambiar de centro,
            // ReiniciarParaNuevoCentro baja la bandera y el centro nuevo puede
            // haber arrancado ya la suya: sin esta condición, el final de la
            // baja del centro anterior la apagaba y el nuevo quedaba abierto a
            // un segundo envío.
            if (generacion == _generacion)
                _procesandoBajaLote = false;
        }
    }

    /// <summary>
    /// Los tres desenlaces del lote: no hizo nada, hizo parte, hizo todo. Solo
    /// el último es un éxito.
    /// </summary>
    private static (string Mensaje, TonoToast Tono) DescribirBajaLote(ResultadoBajaLoteDto dto, int pedidas)
    {
        if (dto.Errores.Count > 0)
            return ($"{dto.DadasDeBaja} de {pedidas} dado(s) de baja. {dto.Errores.Count} no se pudieron procesar: {string.Join(" ", dto.Errores)}",
                TonoToast.Advertencia);

        if (dto.DadasDeBaja == 0)
            return ($"No se dio de baja a nadie: ninguna de las {pedidas} asignaciones seleccionadas seguía activa.",
                TonoToast.Advertencia);

        // Menos bajas que asignaciones pedidas, y sin errores que lo expliquen.
        // El handler de hoy añade un error por cada una que no da de baja, así
        // que este DTO no llega a producirse — pero esa es una garantía SUYA,
        // no algo que el contrato de ResultadoBajaLoteDto prometa. Mientras el
        // recuento pueda quedarse corto, decir «hecho» afirma un efecto que no
        // consta. Es también el desenlace que un test daba por bueno pidiendo
        // dos bajas y recibiendo una.
        if (dto.DadasDeBaja < pedidas)
            return ($"{dto.DadasDeBaja} de {pedidas} dado(s) de baja. El resto no se procesó.", TonoToast.Advertencia);

        return ($"{dto.DadasDeBaja} trabajador(es) dado(s) de baja.", TonoToast.Exito);
    }

    /// <summary>
    /// Abre el Context Panel de la Empresa. Es el destino que declara la nota
    /// del bloque: aqui solo se listan incidencias, la documentacion completa
    /// de la empresa se consulta en su propia ficha (blueprint seccion 3.3).
    /// </summary>
    private Task AbrirDetalleEmpresa() =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Empresa, EmpresaId, EmpresaNombre, "documentacion");

    /// <summary>
    /// Columna "Vigencia" del tercer nivel (blueprint § 3.4). El verbo cambia
    /// segun el estado porque una fecha suelta no dice si ya paso o esta por
    /// llegar, y esa es justo la pregunta del gestor.
    /// </summary>
    private static string TextoVigencia(DocumentoRequeridoDto documento) =>
        DetalleVigenciaDocumentoUi.DeFicha360(documento.Estado, documento.FechaVencimiento, documento.TipoDocumentoNombre);

}
