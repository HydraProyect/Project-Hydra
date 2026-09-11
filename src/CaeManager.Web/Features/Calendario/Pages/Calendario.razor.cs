using System.Globalization;
using CaeManager.Application.Calendario.Queries;
using CaeManager.Application.Visitas.Queries.ObtenerVisitasParaCalendario;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Documentos;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Calendario.Pages;

public partial class Calendario : ComponentBase
{
    private static readonly string[] NombresDiasSemana = ["Lun", "Mar", "Mié", "Jue", "Vie", "Sáb", "Dom"];
    private static readonly CultureInfo Espanol = new("es-ES");

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    /// <summary>
    /// Fecha que la pantalla toma por «hoy»: el mes con el que arranca, al que
    /// vuelve «Hoy» y el día que resalta. Sin informar es
    /// <see cref="DateTime.Today"/>; el router nunca la rellena (la ruta no
    /// tiene segmentos ni lleva <c>[SupplyParameterFromQuery]</c>). Existe para
    /// que los tests fijen el reloj y no dependan del día en que se ejecutan (un
    /// test que cruza medianoche a fin de mes cambiaría de mes a mitad). Es un
    /// parámetro y no un <see cref="TimeProvider"/> inyectado porque la Web no
    /// registra ninguno en DI, ni tiene <c>InternalsVisibleTo</c> para una
    /// propiedad interna.
    /// </summary>
    [Parameter] public DateOnly? Hoy { get; set; }

    private DateOnly HoyEfectivo => Hoy ?? DateOnly.FromDateTime(DateTime.Today);

    private DateOnly _mesActual;
    private IReadOnlyList<VencimientoCalendarioDto> _vencimientos = [];
    private IReadOnlyList<VisitaCalendarioDto> _visitas = [];
    private bool _cargando = true;
    private bool _error;
    private DateOnly? _diaSeleccionado;

    /// <summary>
    /// Número de la carga vigente. Cada <see cref="CargarAsync"/> se queda con
    /// uno nuevo; una respuesta que vuelve cuando ya hay otro no es la del mes
    /// que se ve y se descarta entera (datos, error y fin de carga).
    /// </summary>
    private int _versionCarga;

    /// <summary>Una celda de la rejilla: los días de relleno de la primera y última semana llevan <see cref="DelMes"/> a false.</summary>
    private readonly record struct CeldaCalendario(DateOnly Fecha, bool DelMes);

    private string TituloMes => Capitalizar(_mesActual.ToString("MMMM yyyy", Espanol));

    protected override Task OnInitializedAsync()
    {
        _mesActual = PrimeroDeMes(HoyEfectivo);
        return CargarAsync();
    }

    // Navegar de mes mientras otra carga sigue en vuelo deja dos cargas
    // abiertas que pueden volver en cualquier orden. Por eso: el mes se captura
    // antes del primer await (las dos consultas piden el MISMO mes aunque
    // _mesActual cambie entre ellas), y lo que vuelve de una carga que ya no es
    // la vigente no toca el estado — ni datos, ni error, ni _cargando.
    private async Task CargarAsync()
    {
        var version = ++_versionCarga;
        var mes = _mesActual;

        _cargando = true;
        _error = false;
        StateHasChanged();

        IReadOnlyList<VencimientoCalendarioDto> vencimientos;
        IReadOnlyList<VisitaCalendarioDto> visitas;
        try
        {
            vencimientos = await Mediator.Send(new ObtenerVencimientosMesQuery(mes.Year, mes.Month));
            visitas = await Mediator.Send(new ObtenerVisitasParaCalendarioQuery(mes.Year, mes.Month));
        }
        catch (Exception)
        {
            if (version != _versionCarga) return;
            _error = true;
            _cargando = false;
            return;
        }

        if (version != _versionCarga) return;
        _vencimientos = vencimientos;
        _visitas = visitas;
        _cargando = false;
    }

    private static DateOnly PrimeroDeMes(DateOnly fecha) => new(fecha.Year, fecha.Month, 1);

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
        _mesActual = PrimeroDeMes(HoyEfectivo);
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

    // /visitas no admite hoy un parámetro que abra una visita concreta, así
    // que el destino es la lista; por eso el botón dice «Ir a Visitas» y no
    // el «Abrir visita» del mockup, que prometería algo que no ocurre.
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

    /// <summary>
    /// Nombre accesible del día: el color del recuento es su peor estado, y el
    /// color solo no puede ser la información (WCAG 1.4.1) — aquí va dicho.
    /// </summary>
    private static string EtiquetaDia(
        DateOnly dia,
        IReadOnlyList<VencimientoCalendarioDto> vencimientosDelDia,
        IReadOnlyList<VisitaCalendarioDto> visitasDelDia,
        EstadoDocumento? peorEstado)
    {
        var partes = new List<string>();
        if (peorEstado is not null)
            partes.Add($"{Contar(vencimientosDelDia.Count, "vencimiento", "vencimientos")} (peor estado: {EstadoDocumentoUi.Texto(peorEstado.Value).ToLowerInvariant()})");
        if (visitasDelDia.Count > 0)
            partes.Add(Contar(visitasDelDia.Count, "visita programada", "visitas programadas"));

        return $"{dia.ToString("d 'de' MMMM", Espanol)}: {string.Join(" y ", partes)}";
    }

    private static string FechaLarga(DateOnly dia) => dia.ToString("d 'de' MMMM 'de' yyyy", Espanol);

    private static string ResumenDia(DateOnly dia, int vencimientos, int visitas)
    {
        var partes = new List<string> { Capitalizar(dia.ToString("dddd", Espanol)) };
        if (vencimientos > 0)
            partes.Add(Contar(vencimientos, "vencimiento", "vencimientos"));
        if (visitas > 0)
            partes.Add(Contar(visitas, "visita", "visitas"));
        return string.Join(" · ", partes);
    }

    private static string DetalleVisita(VisitaCalendarioDto visita)
    {
        var fechas = visita.FechaInicio == visita.FechaFin
            ? visita.FechaInicio.ToString("dd/MM", Espanol)
            : $"{visita.FechaInicio.ToString("dd/MM", Espanol)}–{visita.FechaFin.ToString("dd/MM", Espanol)}";
        return $"{visita.ClienteRazonSocial} · {fechas} · {Contar(visita.TotalTrabajadores, "trabajador", "trabajadores")}";
    }

    private static string Contar(int cantidad, string singular, string plural) =>
        $"{cantidad} {(cantidad == 1 ? singular : plural)}";

    private static string Capitalizar(string texto) =>
        string.IsNullOrEmpty(texto) ? texto : char.ToUpper(texto[0], Espanol) + texto[1..];

    // Rejilla de semana en lunes (convención española). La primera y la
    // última semana se completan con los días del mes anterior/siguiente,
    // marcados como fuera del mes: se pintan atenuados y no son interactivos.
    private List<CeldaCalendario> CeldasDelMes()
    {
        var primerDia = _mesActual;
        var ultimoDia = primerDia.AddMonths(1).AddDays(-1);
        var diaSemanaInicio = primerDia.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)primerDia.DayOfWeek;

        var celdas = new List<CeldaCalendario>();
        for (var i = diaSemanaInicio - 1; i >= 1; i--)
            celdas.Add(new CeldaCalendario(primerDia.AddDays(-i), DelMes: false));
        for (var dia = primerDia; dia <= ultimoDia; dia = dia.AddDays(1))
            celdas.Add(new CeldaCalendario(dia, DelMes: true));
        var siguiente = ultimoDia.AddDays(1);
        while (celdas.Count % 7 != 0)
        {
            celdas.Add(new CeldaCalendario(siguiente, DelMes: false));
            siguiente = siguiente.AddDays(1);
        }

        return celdas;
    }
}
