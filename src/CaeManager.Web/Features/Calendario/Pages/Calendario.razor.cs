using CaeManager.Application.Calendario.Queries;
using CaeManager.Application.Visitas.Queries.ObtenerVisitasParaCalendario;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Calendario.Pages;

public partial class Calendario : ComponentBase
{
    private static readonly string[] NombresDiasSemana = ["L", "M", "X", "J", "V", "S", "D"];

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private DateOnly _mesActual = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private IReadOnlyList<VencimientoCalendarioDto> _vencimientos = [];
    private IReadOnlyList<VisitaCalendarioDto> _visitas = [];
    private bool _cargando = true;
    private bool _error;
    private DateOnly? _diaSeleccionado;

    private string TituloMes => _mesActual.ToString("MMMM yyyy", new System.Globalization.CultureInfo("es-ES"));

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _error = false;
        StateHasChanged();

        try
        {
            _vencimientos = await Mediator.Send(new ObtenerVencimientosMesQuery(_mesActual.Year, _mesActual.Month));
            _visitas = await Mediator.Send(new ObtenerVisitasParaCalendarioQuery(_mesActual.Year, _mesActual.Month));
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

    private Task MesAnteriorAsync()
    {
        _mesActual = _mesActual.AddMonths(-1);
        _diaSeleccionado = null;
        return CargarAsync();
    }

    private Task MesSiguienteAsync()
    {
        _mesActual = _mesActual.AddMonths(1);
        _diaSeleccionado = null;
        return CargarAsync();
    }

    private Task MesActualAsync()
    {
        _mesActual = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        _diaSeleccionado = null;
        return CargarAsync();
    }

    private void SeleccionarDia(DateOnly dia)
    {
        if (VencimientosDelDia(dia).Count > 0 || VisitasDelDia(dia).Count > 0)
            _diaSeleccionado = dia;
    }

    private void CerrarDetalle() => _diaSeleccionado = null;

    private void GestionarDocumento(Guid documentoId) =>
        NavigationManager.NavigateTo($"/documentos?documentoId={documentoId}");

    private void GestionarVisita(Guid visitaId) =>
        NavigationManager.NavigateTo("/visitas");

    private List<VencimientoCalendarioDto> VencimientosDelDia(DateOnly dia) =>
        _vencimientos.Where(v => v.FechaVencimiento == dia).ToList();

    // Una Visita cubre un rango, no un solo día — a diferencia de
    // VencimientosDelDia, aquí se comprueba que el día caiga dentro de
    // [FechaInicio, FechaFin], no una igualdad exacta.
    private List<VisitaCalendarioDto> VisitasDelDia(DateOnly dia) =>
        _visitas.Where(v => dia >= v.FechaInicio && dia <= v.FechaFin).ToList();

    private static EstadoDocumento? PeorEstado(IReadOnlyList<VencimientoCalendarioDto> vencimientosDelDia)
    {
        if (vencimientosDelDia.Count == 0) return null;

        if (vencimientosDelDia.Any(v => v.Estado == EstadoDocumento.Vencido)) return EstadoDocumento.Vencido;
        if (vencimientosDelDia.Any(v => v.Estado == EstadoDocumento.Urgente)) return EstadoDocumento.Urgente;
        if (vencimientosDelDia.Any(v => v.Estado == EstadoDocumento.Proximo)) return EstadoDocumento.Proximo;
        return EstadoDocumento.Vigente;
    }

    // Rejilla de semana en lunes (convención española), rellenando con celdas
    // vacías los días del mes anterior/siguiente que completan la primera y
    // última semana.
    private List<DateOnly?> CeldasDelMes()
    {
        var primerDia = _mesActual;
        var ultimoDia = primerDia.AddMonths(1).AddDays(-1);
        var diaSemanaInicio = primerDia.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)primerDia.DayOfWeek;

        var celdas = new List<DateOnly?>();
        for (var i = 1; i < diaSemanaInicio; i++)
            celdas.Add(null);
        for (var dia = primerDia; dia <= ultimoDia; dia = dia.AddDays(1))
            celdas.Add(dia);
        while (celdas.Count % 7 != 0)
            celdas.Add(null);

        return celdas;
    }
}
