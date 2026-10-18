using System.Globalization;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Application.Visitas.Queries.ObtenerVisitasParaCalendario;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace CaeManager.Web.Features.Calendario.Components;

/// <summary>Un vencimiento del día tal como lo pinta «Detalle del día»: Estado y Tono ya resueltos por quien lo abre.</summary>
/// <param name="Reclamable">Solo las filas reclamables llevan «Reclamar» y cuentan en «Reclamar los N».</param>
public sealed record FilaDetalleDia(Guid DocumentoId, string Sujeto, string Documento, string Estado, TonoBadge Tono, bool Reclamable);

public partial class DetalleDia
{
    private static readonly string[] RolesQueReclaman =
        [Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae, Roles.GestorCae];

    // Cascada y no inyección: sin estado de autenticación (una página que no lo cascadea) no hay Reclamar, y nada falla.
    [CascadingParameter] private Task<AuthenticationState>? EstadoAutenticacion { get; set; }

    /// <summary>El día abierto; null = cerrado.</summary>
    [Parameter] public DateOnly? Fecha { get; set; }

    /// <summary>Quién es el sujeto de la vista (la Empresa de la celda): va tras el día de la semana, en el antetítulo.</summary>
    [Parameter] public string? Contexto { get; set; }

    [Parameter] public IReadOnlyList<FilaDetalleDia> Vencimientos { get; set; } = [];
    [Parameter] public IReadOnlyList<VisitaCalendarioDto> Visitas { get; set; } = [];
    [Parameter] public EventCallback OnCerrar { get; set; }

    /// <summary>Documentos a reclamar (uno, o todos los del día). Sin manejador no hay Reclamar ni pie.</summary>
    [Parameter] public EventCallback<IReadOnlyList<Guid>> OnReclamar { get; set; }

    private static CultureInfo Cultura => CultureInfo.CurrentCulture;
    private bool _puedeReclamar;

    private bool PuedeReclamar => OnReclamar.HasDelegate && _puedeReclamar;
    private IReadOnlyList<Guid> ReclamablesIds => [.. Vencimientos.Where(v => v.Reclamable).Select(v => v.DocumentoId)];
    private bool MostrarPie => PuedeReclamar && ReclamablesIds.Count > 0;

    protected override async Task OnParametersSetAsync()
    {
        var usuario = EstadoAutenticacion is null ? null : (await EstadoAutenticacion).User;
        _puedeReclamar = usuario is not null && RolesQueReclaman.Any(usuario.IsInRole);
    }

    private string Antetitulo => Fecha is not { } dia
        ? string.Empty
        : Contexto is { Length: > 0 } contexto
            ? $"{Capitalizar(dia.ToString("dddd", Cultura))} · {contexto}"
            : Capitalizar(dia.ToString("dddd", Cultura));

    private string TituloFecha => Fecha is { } dia ? dia.ToString(Textos["FormatoFechaLarga"], Cultura) : string.Empty;

    private string Resumen
    {
        get
        {
            var partes = new List<string>();
            if (Vencimientos.Count > 0)
                partes.Add(Contar(Vencimientos.Count, "VencimientoUno", "VencimientoVarios"));
            if (Visitas.Count > 0)
                partes.Add(Contar(Visitas.Count, "VisitaUna", "VisitaVarias"));
            return string.Join(" · ", partes);
        }
    }

    private string DetalleVisita(VisitaCalendarioDto visita)
    {
        var fechas = visita.FechaInicio == visita.FechaFin
            ? visita.FechaInicio.ToString("dd/MM", Cultura)
            : $"{visita.FechaInicio.ToString("dd/MM", Cultura)}–{visita.FechaFin.ToString("dd/MM", Cultura)}";
        return $"{visita.ClienteRazonSocial} · {fechas} · {Contar(visita.TotalTrabajadores, "TrabajadorUno", "TrabajadorVarios")}";
    }

    private string Contar(int cantidad, string claveSingular, string clavePlural) =>
        Textos[cantidad == 1 ? claveSingular : clavePlural, cantidad];

    private static string Capitalizar(string texto) =>
        string.IsNullOrEmpty(texto) ? texto : char.ToUpper(texto[0], Cultura) + texto[1..];

    private Task AlCambiarVisible(bool visible) => visible ? Task.CompletedTask : OnCerrar.InvokeAsync();

    private Task Reclamar(IReadOnlyList<Guid> documentoIds) => OnReclamar.InvokeAsync(documentoIds);
}
