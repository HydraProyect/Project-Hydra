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
using FluentValidation;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Centros.Components;

public partial class AcordeonAsignacionesCentro : ComponentBase
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

    private bool _cargando = true;
    private bool _errorCarga;
    private IReadOnlyList<TrabajadorAsignacionDocumentacionDto> _trabajadores = [];
    private readonly HashSet<Guid> _seleccionados = [];

    private IReadOnlyList<TrabajadorSinAsignacionDto> _trabajadoresVisitaSinAsignacion = [];
    private readonly HashSet<Guid> _asignandoDesdeVisita = [];

    private static readonly IReadOnlyList<PestanaDefinicion> _pestanasAlta =
    [
        new("Lista", "Lista"),
        new("Matriz", "Matriz")
    ];

    private bool _drawerAltaVisible;
    private string _vistaAlta = "Lista";
    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresDisponibles = [];
    private IReadOnlyList<CentroSelectorDto> _centrosDisponibles = [];
    private readonly HashSet<Guid> _trabajadorIdsSeleccionados = [];
    private readonly HashSet<Guid> _centroIdsSeleccionados = [];
    private readonly HashSet<(Guid TrabajadorId, Guid CentroId)> _celdasExcluidas = [];
    private string _fechaAlta = string.Empty;
    private bool _guardandoAlta;
    private string? _mensajeErrorAlta;
    private IReadOnlyList<DocumentoFaltanteDto> _documentosFaltantes = [];

    private bool _confirmarBajaLoteVisible;
    private string _fechaBajaLote = string.Empty;
    private bool _procesandoBajaLote;

    private IReadOnlyList<ElementoSeleccionable> _trabajadoresComoOpciones =>
        _trabajadoresDisponibles.Select(t => new ElementoSeleccionable(t.Id, $"{t.NombreCompleto} ({t.Dni})")).ToList();

    private IReadOnlyList<ElementoSeleccionable> _centrosComoOpciones =>
        _centrosDisponibles.Select(c => new ElementoSeleccionable(c.Id, $"{c.Nombre} ({c.ClienteRazonSocial})")).ToList();

    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresSeleccionadosOrdenados =>
        _trabajadoresDisponibles.Where(t => _trabajadorIdsSeleccionados.Contains(t.Id))
            .OrderBy(t => t.NombreCompleto).ToList();

    private IReadOnlyList<CentroSelectorDto> _centrosSeleccionadosOrdenados =>
        _centrosDisponibles.Where(c => _centroIdsSeleccionados.Contains(c.Id))
            .OrderBy(c => c.Nombre).ToList();

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            _trabajadores = await Mediator.Send(new ObtenerAsignacionesDocumentacionPorCentroQuery(CentroId, VisitaFechaFin));
            _seleccionados.Clear();

            _trabajadoresVisitaSinAsignacion = VisitaId is { } visitaId
                ? await Mediator.Send(new ObtenerTrabajadoresVisitaSinAsignacionQuery(visitaId, CentroId))
                : [];
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

    /// <summary>
    /// "Asignación rápida desde visita" (§ 0.3): el trabajador ya está
    /// identificado por <c>VisitaTrabajador</c> — no hace falta un selector,
    /// solo confirmar la fecha de alta (hoy) y avisar si le faltará algún
    /// documento obligatorio, mismo preflight no bloqueante que el drawer N×M.
    /// </summary>
    private async Task AsignarDesdeVisitaAsync(TrabajadorSinAsignacionDto trabajador)
    {
        _asignandoDesdeVisita.Add(trabajador.TrabajadorId);
        StateHasChanged();

        try
        {
            var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
            var faltantes = await Mediator.Send(new ObtenerDocumentosFaltantesParaAsignacionQuery([trabajador.TrabajadorId], [CentroId]));

            var resultado = await Mediator.Send(new CrearAsignacionCommand(trabajador.TrabajadorId, CentroId, hoy));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar(
                faltantes.Count == 0
                    ? $"{trabajador.TrabajadorNombre} asignado a {CentroNombre}."
                    : $"{trabajador.TrabajadorNombre} asignado a {CentroNombre} — le faltan {faltantes.Count} documento(s) obligatorio(s).",
                faltantes.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);

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

    /// <summary>Badge circular junto al "7/9" (PLAN-EJECUCION-UX.md § 0.8) — misma fracción, solo cambia la representación.</summary>
    private static int PorcentajeCumplimiento(TrabajadorAsignacionDocumentacionDto trabajador) =>
        (int)Math.Round(DocumentosAlDia(trabajador) * 100.0 / trabajador.Documentos.Count);

    private void AlternarSeleccion(Guid asignacionId, bool marcado)
    {
        if (marcado) _seleccionados.Add(asignacionId);
        else _seleccionados.Remove(asignacionId);
    }

    /// <summary>
    /// Un documento faltante no tiene DocumentoId todavía — lleva al drawer
    /// de creación con el propietario y el tipo ya elegidos, mismo patrón que
    /// "Gestionar" en Alertas.razor.cs.
    /// </summary>
    private void Gestionar(Guid trabajadorId, DocumentoRequeridoDto documento) => NavigationManager.NavigateTo(
        documento.DocumentoId is { } documentoId
            ? $"/documentos?documentoId={documentoId}"
            : $"/documentos?trabajadorId={trabajadorId}&tipoDocumentoId={documento.TipoDocumentoId}");

    private async Task AbrirDrawerAltaAsync()
    {
        _trabajadoresDisponibles = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery());
        _centrosDisponibles = await Mediator.Send(new ObtenerCentrosParaSelectorQuery());

        _vistaAlta = "Lista";
        _trabajadorIdsSeleccionados.Clear();
        _centroIdsSeleccionados.Clear();
        // El Centro de esta fila queda pre-marcado — el gestor puede seguir
        // añadiendo otros centros si quiere, la matriz no se recorta (PLAN-EJECUCION-UX.md § 0.1).
        if (_centrosDisponibles.Any(c => c.Id == CentroId))
            _centroIdsSeleccionados.Add(CentroId);
        _celdasExcluidas.Clear();
        _documentosFaltantes = [];
        _fechaAlta = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        _mensajeErrorAlta = null;
        _drawerAltaVisible = true;
    }

    private async Task AlternarTrabajadorAsync(Guid trabajadorId, bool marcado)
    {
        if (marcado)
            _trabajadorIdsSeleccionados.Add(trabajadorId);
        else
        {
            _trabajadorIdsSeleccionados.Remove(trabajadorId);
            _celdasExcluidas.RemoveWhere(c => c.TrabajadorId == trabajadorId);
        }

        await ActualizarPreflightAsync();
    }

    private async Task AlternarCentroAsync(Guid centroId, bool marcado)
    {
        if (marcado)
            _centroIdsSeleccionados.Add(centroId);
        else
        {
            _centroIdsSeleccionados.Remove(centroId);
            _celdasExcluidas.RemoveWhere(c => c.CentroId == centroId);
        }

        await ActualizarPreflightAsync();
    }

    private void AlternarCeldaMatriz(Guid trabajadorId, Guid centroId, bool incluida)
    {
        if (incluida)
            _celdasExcluidas.Remove((trabajadorId, centroId));
        else
            _celdasExcluidas.Add((trabajadorId, centroId));
    }

    private async Task ActualizarPreflightAsync()
    {
        if (_trabajadorIdsSeleccionados.Count == 0 || _centroIdsSeleccionados.Count == 0)
        {
            _documentosFaltantes = [];
            return;
        }

        _documentosFaltantes = await Mediator.Send(new ObtenerDocumentosFaltantesParaAsignacionQuery(
            _trabajadorIdsSeleccionados.ToList(), _centroIdsSeleccionados.ToList()));
    }

    private async Task GuardarAltaAsync()
    {
        _guardandoAlta = true;
        _mensajeErrorAlta = null;

        try
        {
            if (_trabajadorIdsSeleccionados.Count == 0)
            {
                _mensajeErrorAlta = "Selecciona al menos un trabajador.";
                return;
            }

            if (_centroIdsSeleccionados.Count == 0)
            {
                _mensajeErrorAlta = "Selecciona al menos un centro.";
                return;
            }

            if (!DateOnly.TryParse(_fechaAlta, out var fechaAlta))
            {
                _mensajeErrorAlta = "Introduce una fecha de alta válida.";
                return;
            }

            var creadas = 0;
            var yaActivas = 0;
            var errores = new List<string>();

            if (_celdasExcluidas.Count == 0)
            {
                var resultado = await Mediator.Send(new CrearAsignacionesCommand(
                    _trabajadorIdsSeleccionados.ToList(), _centroIdsSeleccionados.ToList(), fechaAlta));

                if (resultado.EsFallido)
                {
                    _mensajeErrorAlta = resultado.Error.Mensaje;
                    return;
                }

                creadas = resultado.Valor.Creadas;
                yaActivas = resultado.Valor.YaActivas;
                errores.AddRange(resultado.Valor.Errores);
            }
            else
            {
                foreach (var centroId in _centroIdsSeleccionados)
                {
                    var trabajadorIdsParaCentro = _trabajadorIdsSeleccionados
                        .Where(t => !_celdasExcluidas.Contains((t, centroId)))
                        .ToList();

                    if (trabajadorIdsParaCentro.Count == 0) continue;

                    var resultado = await Mediator.Send(new CrearAsignacionesCommand(trabajadorIdsParaCentro, [centroId], fechaAlta));

                    if (resultado.EsFallido)
                    {
                        _mensajeErrorAlta = resultado.Error.Mensaje;
                        return;
                    }

                    creadas += resultado.Valor.Creadas;
                    yaActivas += resultado.Valor.YaActivas;
                    errores.AddRange(resultado.Valor.Errores);
                }
            }

            var resumen = $"{creadas} asignación(es) creada(s)" + (yaActivas > 0 ? $", {yaActivas} ya estaban activas." : ".");
            ToastService.Mostrar(resumen, errores.Count > 0 ? TonoToast.Advertencia : TonoToast.Exito);
            foreach (var error in errores)
                ToastService.Mostrar(error, TonoToast.Advertencia);

            _drawerAltaVisible = false;
            await CargarAsync();
        }
        catch (ValidationException)
        {
            _mensajeErrorAlta = "Revisa los datos introducidos.";
        }
        catch (Exception)
        {
            _mensajeErrorAlta = "No pudimos guardar los cambios. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardandoAlta = false;
        }
    }

    private void AbrirConfirmarBajaLoteAsync()
    {
        _fechaBajaLote = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        _confirmarBajaLoteVisible = true;
    }

    private async Task ConfirmarBajaLoteAsync()
    {
        _procesandoBajaLote = true;

        try
        {
            if (!DateOnly.TryParse(_fechaBajaLote, out var fechaBaja))
            {
                ToastService.Mostrar("Introduce una fecha de baja válida.", TonoToast.Error);
                return;
            }

            var resultado = await Mediator.Send(new DarDeBajaAsignacionesCommand(_seleccionados.ToList(), fechaBaja));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            var dto = resultado.Valor;
            ToastService.Mostrar(
                dto.Errores.Count == 0
                    ? $"{dto.DadasDeBaja} trabajador(es) dado(s) de baja."
                    : $"{dto.DadasDeBaja} dado(s) de baja. {dto.Errores.Count} no se pudieron procesar: {string.Join(" ", dto.Errores)}",
                dto.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);

            _confirmarBajaLoteVisible = false;
            await CargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos procesar la baja. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _procesandoBajaLote = false;
        }
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
