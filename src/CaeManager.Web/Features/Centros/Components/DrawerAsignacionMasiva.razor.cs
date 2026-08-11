using CaeManager.Application.Alertas;
using CaeManager.Application.Asignaciones.Commands.CrearAsignaciones;
using CaeManager.Application.Asignaciones.Queries.ObtenerDocumentosFaltantesParaAsignacion;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Centros.Components;

public partial class DrawerAsignacionMasiva : ComponentBase
{
    /// <summary>Se dispara tras guardar con éxito — el host decide qué refrescar (una fila, la lista completa).</summary>
    [Parameter] public EventCallback OnGuardado { get; set; }

    private static readonly IReadOnlyList<PestanaDefinicion> _pestanas =
    [
        new("Lista", "Lista"),
        new("Matriz", "Matriz")
    ];

    private bool _visible;
    private string _vista = "Lista";
    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresDisponibles = [];
    private IReadOnlyList<CentroSelectorDto> _centrosDisponibles = [];
    private readonly HashSet<Guid> _trabajadorIdsSeleccionados = [];
    private readonly HashSet<Guid> _centroIdsSeleccionados = [];
    private readonly HashSet<(Guid TrabajadorId, Guid CentroId)> _celdasExcluidas = [];
    private string _fechaAlta = string.Empty;
    private bool _guardando;
    private string? _mensajeError;
    private IReadOnlyList<DocumentoFaltanteDto> _documentosFaltantes = [];

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

    /// <summary>
    /// <paramref name="centroIdsPreseleccionados"/> cubre las dos entradas:
    /// un único CentroId desde "+ Asignar trabajador" dentro de un Centro, o
    /// varios desde "Asignar a varios centros" con Selección múltiple en la
    /// lista — la matriz no se recorta después: el gestor puede seguir
    /// añadiendo trabajadores o centros libremente (PLAN-EJECUCION-UX.md § 0.1).
    /// </summary>
    public async Task AbrirAsync(IReadOnlyCollection<Guid>? centroIdsPreseleccionados = null)
    {
        _trabajadoresDisponibles = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery());
        _centrosDisponibles = await Mediator.Send(new ObtenerCentrosParaSelectorQuery());

        _vista = "Lista";
        _trabajadorIdsSeleccionados.Clear();
        _centroIdsSeleccionados.Clear();
        if (centroIdsPreseleccionados is not null)
        {
            var idsDisponibles = _centrosDisponibles.Select(c => c.Id).ToHashSet();
            foreach (var centroId in centroIdsPreseleccionados.Where(idsDisponibles.Contains))
                _centroIdsSeleccionados.Add(centroId);
        }
        _celdasExcluidas.Clear();
        _documentosFaltantes = [];
        _fechaAlta = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        _mensajeError = null;
        _visible = true;
        StateHasChanged();
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

    private async Task GuardarAsync()
    {
        _guardando = true;
        _mensajeError = null;

        try
        {
            if (_trabajadorIdsSeleccionados.Count == 0)
            {
                _mensajeError = "Selecciona al menos un trabajador.";
                return;
            }

            if (_centroIdsSeleccionados.Count == 0)
            {
                _mensajeError = "Selecciona al menos un centro.";
                return;
            }

            if (!DateOnly.TryParse(_fechaAlta, out var fechaAlta))
            {
                _mensajeError = "Introduce una fecha de alta válida.";
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
                    _mensajeError = resultado.Error.Mensaje;
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
                        _mensajeError = resultado.Error.Mensaje;
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

            _visible = false;
            if (OnGuardado.HasDelegate)
                await OnGuardado.InvokeAsync();
        }
        catch (ValidationException)
        {
            _mensajeError = "Revisa los datos introducidos.";
        }
        catch (Exception)
        {
            _mensajeError = "No pudimos guardar los cambios. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardando = false;
            StateHasChanged();
        }
    }
}
