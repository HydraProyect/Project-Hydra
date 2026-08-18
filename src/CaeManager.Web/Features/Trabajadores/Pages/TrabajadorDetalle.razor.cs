using CaeManager.Application.Common;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Application.Documentos;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Asignaciones.Commands.DarDeBajaAsignaciones;
using CaeManager.Application.Gestiones.Commands.CompletarGestion;
using CaeManager.Application.Gestiones.Commands.CrearGestionesParaTrabajador;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Commands.EliminarTrabajador;
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
/// "Reclamar faltantes" del menú ⋯ del mockup: <c>EnviarReclamacionCommand</c>
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
public partial class TrabajadorDetalle : ComponentBase
{
    private static readonly IReadOnlyDictionary<EstadoDocumento, int> OrdenSeveridad = new Dictionary<EstadoDocumento, int>
    {
        [EstadoDocumento.Faltante] = 0,
        [EstadoDocumento.Vencido] = 1,
        [EstadoDocumento.Urgente] = 2,
        [EstadoDocumento.Proximo] = 3,
        [EstadoDocumento.Vigente] = 4
    };

    private static readonly IReadOnlyList<PestanaDefinicion> _pestanas =
    [
        new("operacion", "Operación"),
        new("historial", "Historial"),
        new("contactos", "Contactos")
    ];

    [Parameter] public Guid TrabajadorId { get; set; }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ContextWorkspaceService WorkspaceService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ICurrentUserService CurrentUserService { get; set; } = default!;

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

    private bool _confirmarBajaTrabajadorVisible;
    private bool _dandoDeBajaTrabajador;

    private bool _reclamarFaltantesVisible;
    private bool _reclamandoFaltantes;
    private IReadOnlyList<ClienteReclamableDto> _clientesReclamables = [];
    private readonly HashSet<Guid> _clientesSeleccionadosReclamar = [];

    private record ClienteReclamableDto(Guid ClienteId, string ClienteRazonSocial, IReadOnlyList<Guid> DocumentoIds);

    private IReadOnlyList<GestionListaDto> _gestionesPendientes = [];
    private bool _cargandoGestiones = true;
    private readonly HashSet<Guid> _completandoGestion = [];

    private string? NombreCompleto => _detalle is null ? null : $"{_detalle.Nombre} {_detalle.Apellidos}";

    private IReadOnlyList<BreadcrumbElemento> Miguero =>
        new[] { new BreadcrumbElemento("Trabajadores"), new BreadcrumbElemento(NombreCompleto ?? "…") };

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
        _cargando = true;
        _error = false;

        try
        {
            _detalle = await Mediator.Send(new ObtenerTrabajadorPorIdQuery(TrabajadorId));
            if (_detalle is null)
            {
                _error = true;
                return;
            }

            _centros = await Mediator.Send(new ObtenerDocumentacionPorCentroDeTrabajadorQuery(TrabajadorId));

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
                _ = CargarGestionesAsync();
        }
        catch (Exception)
        {
            _error = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    private async Task CargarGestionesAsync()
    {
        _cargandoGestiones = true;
        try
        {
            var resultado = await Mediator.Send(new ObtenerGestionesQuery(
                Busqueda: null, Estado: EstadoGestion.Pendiente, TrabajadorId: TrabajadorId, TamanoPagina: 50));
            _gestionesPendientes = resultado.Elementos;
        }
        catch (Exception)
        {
            _gestionesPendientes = [];
        }
        finally
        {
            _cargandoGestiones = false;
            StateHasChanged();
        }
    }

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

    private async Task DarDeBajaAsignacionAsync(Guid asignacionId)
    {
        var resultado = await Mediator.Send(new DarDeBajaAsignacionesCommand([asignacionId], DateOnly.FromDateTime(DateTime.UtcNow)));
        if (resultado.EsFallido)
        {
            ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            return;
        }

        ToastService.Mostrar("Asignación dada de baja.", TonoToast.Exito);
        await CargarAsync();
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
            ToastService.Mostrar("No hay documentos pendientes que reclamar.", TonoToast.Info);
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
                ToastService.Mostrar($"Reclamación enviada a {enviadosA[0]}.", TonoToast.Exito);
            else if (enviadosA.Count > 1)
                ToastService.Mostrar($"Reclamación enviada a {enviadosA.Count} clientes: {string.Join(", ", enviadosA)}.", TonoToast.Exito);

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
        _tiposDocumentoDisponibles = await Mediator.Send(new ObtenerTiposDocumentoQuery(AmbitoAplicacion: AmbitoAplicacion.Trabajador));
        _tipoDocumentoParaGestion = string.Empty;
        _crearGestionVisible = true;
    }

    private async Task ConfirmarCrearGestionAsync()
    {
        if (!Guid.TryParse(_tipoDocumentoParaGestion, out var tipoDocumentoId)) return;

        _creandoGestion = true;
        try
        {
            var resultado = await Mediator.Send(new CrearGestionesParaTrabajadorCommand(TrabajadorId, tipoDocumentoId));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar($"Se creó la gestión en {resultado.Valor.Creadas} centro(s).", TonoToast.Exito);
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
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            await CargarGestionesAsync();
        }
        finally
        {
            _completandoGestion.Remove(gestionId);
        }
    }

    private async Task ConfirmarDarDeBajaTrabajadorAsync()
    {
        _dandoDeBajaTrabajador = true;
        try
        {
            var usuarioId = await CurrentUserService.ObtenerUsuarioActualIdAsync();
            var resultado = await Mediator.Send(new EliminarTrabajadorCommand(TrabajadorId, usuarioId ?? Guid.Empty));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Trabajador dado de baja.", TonoToast.Exito);
            NavigationManager.NavigateTo("/trabajadores");
        }
        finally
        {
            _dandoDeBajaTrabajador = false;
            _confirmarBajaTrabajadorVisible = false;
        }
    }
}
