using CaeManager.Application.Alertas;
using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Asignaciones.Commands.CrearAsignaciones;
using CaeManager.Application.Asignaciones.Commands.DarDeBajaAsignaciones;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Asignaciones.Queries.ObtenerDocumentosFaltantesParaAsignacion;
using CaeManager.Application.Asignaciones.Queries.ObtenerTrabajadoresVisitaSinAsignacion;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Web.Components.Workspace;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Features.Documentos.Components;
using FluentValidation;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Centros.Components;

public partial class AcordeonAsignacionesCentro : ComponentBase, IDisposable
{
    [Inject] private ContextWorkspaceService WorkspaceService { get; set; } = default!;

    [Parameter, EditorRequired] public Guid CentroId { get; set; }
    [Parameter, EditorRequired] public string CentroNombre { get; set; } = string.Empty;

    /// <summary>Próxima visita activa del centro (Centros.razor la resuelve en lote) — null si no tiene ninguna.</summary>
    [Parameter] public Guid? VisitaId { get; set; }
    [Parameter] public DateOnly? VisitaFechaFin { get; set; }

    /// <summary>Empresa titular del centro, sujeto del ámbito Empresa (OD-13).</summary>
    [Parameter] public Guid EmpresaId { get; set; }
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
    /// Si el acordeón pinta sus propios caminos a la ficha del centro
    /// (<c>/centros/{id}</c>): «Ver los N en Centro 360» en el estado «Todos al
    /// día» y «Ver Centro 360 →» junto a la nota de trabajadores al día
    /// ocultos. Por defecto sí, para que cualquier consumidor que no lo diga
    /// quede como estaba. La lista de /centros lo apaga porque ya pinta, bajo
    /// el acordeón, un enlace único y siempre presente a la misma ficha: con
    /// los dos, la misma fila ofrecía dos caminos al mismo destino. Apagado,
    /// el texto informativo (título «Todos al día», recuento de ocultos) se
    /// conserva; solo desaparece el botón.
    /// </summary>
    [Parameter] public bool MostrarEnlacesCentro360 { get; set; } = true;

    /// <summary>
    /// Búsqueda por nombre gobernada por el consumidor — la barra de trabajo de
    /// Centro 360 («Centro 360 TALVEG.dc.html», <c>data-workbar</c>), que la
    /// pinta arriba y la conserva en la URL. <c>null</c> significa que nadie la
    /// gobierna: entonces el acordeón pinta su propio buscador, como en la fila
    /// de <c>/centros</c>.
    /// </summary>
    [Parameter] public string? FiltroTexto { get; set; }

    /// <summary>
    /// Filtro de estado documental gobernado por el consumidor, con los valores
    /// de <see cref="OpcionesEstadoDocumental"/>. Vacío o desconocido no filtra
    /// nada: la lista completa es el desenlace honesto de un filtro que no se
    /// entiende, no una lista vacía.
    /// </summary>
    [Parameter] public string? FiltroEstado { get; set; }

    /// <summary>
    /// Pie con los totales documentales de la lista («Totales del centro» del
    /// mockup). Solo tiene sentido con la lista completa: en la vista previa de
    /// <c>/centros</c> lo que se ve está recortado a incidencias y un total
    /// junto a una lista recortada se leería como el total de lo que se ve.
    /// </summary>
    [Parameter] public bool MostrarTotales { get; set; }

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

    private IReadOnlyList<TrabajadorSinAsignacionDto> _trabajadoresVisitaSinAsignacion = [];
    private readonly HashSet<Guid> _asignandoDesdeVisita = [];

    private bool _confirmarBajaLoteVisible;
    private string _fechaBajaLote = string.Empty;
    private bool _procesandoBajaLote;

    private DrawerAsignacionMasiva _drawerAsignacion = default!;

    protected override void OnInitialized() => _cancelacion = _ciclo.Token;

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
            var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
            var centroId = CentroId;
            var centroNombre = CentroNombre;
            var faltantes = await Mediator.Send(
                new ObtenerDocumentosFaltantesParaAsignacionQuery([trabajador.TrabajadorId], [centroId]), _cancelacion);

            var resultado = await Mediator.Send(new CrearAsignacionCommand(trabajador.TrabajadorId, centroId, hoy));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
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

    /// <summary>"7/9" junto al nombre (PLAN-EJECUCION-UX.md § 0.5) — se deriva de los mismos <c>Documentos</c> ya cargados, sin consulta nueva.</summary>
    private static int DocumentosAlDia(TrabajadorAsignacionDocumentacionDto trabajador) =>
        trabajador.Documentos.Count(d => d.Estado == EstadoDocumento.Vigente);

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
    /// Búsqueda por nombre y filtro de estado documental sobre
    /// <see cref="TrabajadoresAMostrar"/> — client-side, sin consulta nueva:
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

            if (EstadoFiltrado is { } estado)
                lista = lista.Where(t => t.Documentos.Any(d => d.Estado == estado)).ToList();

            return lista;
        }
    }

    /// <summary>
    /// Estado documental por el que se filtra, leído del mismo léxico cerrado
    /// que el resto de la aplicación (<c>nameof(EstadoDocumento.X)</c>, igual
    /// que <see cref="EstadoDocumentoUi.OpcionesDocumentales"/>). Un valor
    /// desconocido no filtra: la lista completa es el desenlace honesto de un
    /// filtro que no se entiende, no una lista vacía.
    /// </summary>
    private EstadoDocumento? EstadoFiltrado =>
        Enum.TryParse<EstadoDocumento>(FiltroEstado, out var estado) ? estado : null;

    private bool HayFiltroAplicado => !string.IsNullOrWhiteSpace(TextoBusqueda) || EstadoFiltrado is not null;

    /// <summary>
    /// Opciones del filtro de estado de la barra de trabajo de Centro 360. Un
    /// trabajador coincide cuando alguno de sus documentos exigidos está en ese
    /// estado — la fila es del trabajador, la pregunta es sobre sus documentos.
    /// Se ordenan de peor a mejor por el mismo motivo que
    /// <see cref="EstadoDocumentoUi.OpcionesDocumentales"/>: al filtrar, lo que
    /// el gestor busca es lo que le urge.
    /// </summary>
    public static IReadOnlyList<OpcionEstado> OpcionesEstadoDocumental { get; } =
    [
        new(nameof(EstadoDocumento.Vencido), EstadoDocumentoUi.Texto(EstadoDocumento.Vencido)),
        new(nameof(EstadoDocumento.Faltante), EstadoDocumentoUi.Texto(EstadoDocumento.Faltante)),
        new(nameof(EstadoDocumento.Urgente), EstadoDocumentoUi.Texto(EstadoDocumento.Urgente)),
        new(nameof(EstadoDocumento.Proximo), EstadoDocumentoUi.Texto(EstadoDocumento.Proximo)),
        new(nameof(EstadoDocumento.Vigente), EstadoDocumentoUi.Texto(EstadoDocumento.Vigente))
    ];

    /// <summary>
    /// Por qué no se ve ningún trabajador. El vacío por filtro y el vacío por
    /// ausencia de registros no son el mismo hecho y no pueden contarse igual.
    /// </summary>
    private string DescripcionSinCoincidencias
    {
        get
        {
            var porTexto = string.IsNullOrWhiteSpace(TextoBusqueda) ? null : $"«{TextoBusqueda}»";
            var porEstado = EstadoFiltrado is { } estado ? EstadoDocumentoUi.Texto(estado).ToLowerInvariant() : null;

            return (porTexto, porEstado) switch
            {
                (not null, not null) => $"Ningún trabajador coincide con {porTexto} y tiene algún documento en estado «{porEstado}».",
                (not null, null) => $"Ningún trabajador coincide con {porTexto}.",
                (null, not null) => $"Ningún trabajador de este centro tiene algún documento en estado «{porEstado}».",
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
    private (int Exigidos, int AlDia, int Vencidos, int Faltantes) TotalesDocumentales
    {
        get
        {
            var documentos = _trabajadores.SelectMany(t => t.Documentos).ToList();
            return (
                documentos.Count,
                documentos.Count(d => d.Estado is EstadoDocumento.Vigente or EstadoDocumento.SinCaducidad),
                documentos.Count(d => d.Estado == EstadoDocumento.Vencido),
                documentos.Count(d => d.Estado == EstadoDocumento.Faltante));
        }
    }

    private void VerCentroCompleto() => NavigationManager.NavigateTo($"/centros/{CentroId}");

    private void AlternarExpansionTrabajador(Guid asignacionId)
    {
        if (!_expandidosTrabajador.Add(asignacionId))
            _expandidosTrabajador.Remove(asignacionId);
    }

    /// <summary>
    /// Documentos vencidos del trabajador para la 1ª ranura de recuento
    /// (contrato de fidelidad 2026-08-09 § 1.1/1.4). "Faltante cuenta como
    /// vencido" — mismo criterio que <c>ObtenerCentrosQuery.Desglosar</c> a
    /// nivel de Centro, para que el léxico cerrado no necesite una tercera
    /// casilla también aquí.
    /// </summary>
    private static IReadOnlyList<DocumentoRequeridoDto> DocumentosVencidos(TrabajadorAsignacionDocumentacionDto trabajador) =>
        trabajador.Documentos.Where(d => d.Estado is EstadoDocumento.Vencido or EstadoDocumento.Faltante).ToList();

    /// <summary>Documentos próximos a vencer del trabajador, para la 2ª ranura de recuento.</summary>
    private static IReadOnlyList<DocumentoRequeridoDto> DocumentosProximos(TrabajadorAsignacionDocumentacionDto trabajador) =>
        trabajador.Documentos.Where(d => d.Estado == EstadoDocumento.Proximo).ToList();

    /// <summary>
    /// Documentos en ventana urgente del trabajador, para la 3ª ranura.
    /// "Urgente" es la única severidad que ni la 1ª ni la 2ª ranura recogen
    /// (mismo criterio que <c>ObtenerCentrosQuery.Desglosar</c>, que tampoco
    /// la cuenta en ninguno de los dos recuentos del Centro) — por eso es lo
    /// único que le queda por decir a esta ranura sin repetir lo que ya dicen
    /// las dos anteriores. Mostrar aquí <c>trabajador.PeorEstado</c> siempre
    /// (como hacía la cabecera de <c>SeccionColapsable</c>) duplicaba el
    /// recuento de vencidos/próximos con otro badge al lado — justo el efecto
    /// que la lámina evita dejando esta ranura vacía en la mayoría de filas.
    /// </summary>
    private static IReadOnlyList<DocumentoRequeridoDto> DocumentosUrgentes(TrabajadorAsignacionDocumentacionDto trabajador) =>
        trabajador.Documentos.Where(d => d.Estado == EstadoDocumento.Urgente).ToList();

    /// <summary>Nombre accesible del badge de solo recuento — mismo criterio que <c>Centros.razor.cs.DescribirRecuento</c>, sin el desglose por ámbito porque aquí el sujeto ya es un único trabajador.</summary>
    private static string DescribirRecuentoTrabajador(IReadOnlyList<DocumentoRequeridoDto> documentos, string calificativo) =>
        documentos.Count == 1
            ? $"1 documento {calificativo}"
            : $"{documentos.Count} documentos {(calificativo.EndsWith('o') ? calificativo + "s" : calificativo)}";

    private static string DescribirDocumentoIncidencia(DocumentoRequeridoDto documento) => documento.Estado switch
    {
        EstadoDocumento.Faltante => $"{documento.TipoDocumentoNombre} — falta",
        EstadoDocumento.Vencido when documento.FechaVencimiento is { } vencio => $"{documento.TipoDocumentoNombre} — venció {vencio:dd/MM/yyyy}",
        EstadoDocumento.Proximo when documento.FechaVencimiento is { } caduca => $"{documento.TipoDocumentoNombre} — caduca {caduca:dd/MM/yyyy}",
        _ => documento.TipoDocumentoNombre
    };

    /// <summary>"Detalles" de la fila de Trabajador (blueprint § 3.3, contrato § 1.4) — mismo patrón que Trabajadores.razor/Incidencias.razor/Gestiones.razor, no una ruta nueva.</summary>
    private Task AbrirDetalleTrabajador(TrabajadorAsignacionDocumentacionDto trabajador) =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Trabajador, trabajador.TrabajadorId, trabajador.TrabajadorNombre, "informacion");

    private void AlternarSeleccion(Guid asignacionId, bool marcado)
    {
        if (marcado) _seleccionados.Add(asignacionId);
        else _seleccionados.Remove(asignacionId);
    }

    private DrawerGestionDocumento _drawerGestion = default!;

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

    private static string TextoVigenciaEmpresa(IncidenciaCentroDto incidencia)
    {
        if (incidencia.FechaVencimiento is not { } fecha)
            return "Sin caducidad";

        var texto = fecha.ToString("dd/MM/yyyy");
        return incidencia.Estado == EstadoDocumento.Vencido ? $"Vencio {texto}" : $"Caduca {texto}";
    }

    private void AbrirConfirmarBajaLoteAsync()
    {
        _fechaBajaLote = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        _confirmarBajaLoteVisible = true;
    }

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
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
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
    private static string TextoVigencia(DocumentoRequeridoDto documento)
    {
        if (documento.DocumentoId is null)
        {
            return "—";
        }

        if (documento.FechaVencimiento is not { } fecha)
        {
            // Documento sin caducidad: no es un hueco de datos, es una
            // propiedad del tipo documental. Se declara en vez de dejar "—",
            // que se leeria como "falta el dato".
            return "Sin caducidad";
        }

        var texto = fecha.ToString("dd/MM/yyyy");
        return documento.Estado == EstadoDocumento.Vencido ? $"Vencio {texto}" : $"Caduca {texto}";
    }

    /// <summary>
    /// Si la accion necesita peso visual. Un documento al dia conserva
    /// "Gestionar" pero atenuado (04 section 2.5): sigue disponible, deja de
    /// competir por la atencion con las filas que si piden intervencion.
    /// </summary>
    private static bool RequiereIntervencion(DocumentoRequeridoDto documento) =>
        documento.DocumentoId is null
        || documento.CaducaEnVentanaVisita
        || documento.Estado is EstadoDocumento.Vencido or EstadoDocumento.Proximo or EstadoDocumento.Faltante;
}
