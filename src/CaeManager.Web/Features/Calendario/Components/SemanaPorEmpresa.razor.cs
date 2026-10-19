using System.Globalization;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Application.Calendario.Queries;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Calendario.Components;

public partial class SemanaPorEmpresa
{
    [Parameter] public DateOnly Lunes { get; set; }
    [Parameter] public DateOnly Hoy { get; set; }
    [Parameter] public IReadOnlyList<SemanaEmpresaDto> Empresas { get; set; } = [];
    [Parameter] public EventCallback<SemanaEmpresaDto> OnReclamar { get; set; }
    private static readonly string[] Dias = ["DiaLunes", "DiaMartes", "DiaMiercoles", "DiaJueves", "DiaViernes"];
    private static CultureInfo Cultura => CultureInfo.CurrentCulture;
    private IEnumerable<DateOnly> DiasSemana => Enumerable.Range(0, 5).Select(Lunes.AddDays);
    private string InicioSemana => Lunes.ToString(Lunes.Month == Lunes.AddDays(4).Month ? "%d" : Textos["FormatoDiaMes"], Cultura);
    private string TituloSemana => Textos["EmpresaSemana", InicioSemana, Lunes.AddDays(4).ToString(Textos["FormatoDiaMes"], Cultura)];
    private string NombreDia(DateOnly fecha) => $"{Textos[Dias[fecha.DayNumber - Lunes.DayNumber]]} {fecha.Day}";
    private static int Bloqueantes(SemanaEmpresaDto empresa) => empresa.Documentos.Count(d => d.BloqueaAcceso);
    private static IReadOnlyList<DocumentoSemanaDto> DocumentosDelDia(SemanaEmpresaDto empresa, DateOnly fecha) =>
        empresa.Documentos.Where(d => d.Fecha == fecha).ToArray();
    private IEnumerable<CeldaSemana> Celdas(SemanaEmpresaDto empresa) =>
        DiasSemana.Select(dia => new CeldaSemana(dia, DocumentosDelDia(empresa, dia)));
    private string EtiquetaCelda(SemanaEmpresaDto empresa, CeldaSemana celda) =>
        Textos[celda.Documentos.Count == 1 ? "DocumentosVencenUno" : "DocumentosVencen", celda.Documentos.Count, empresa.EmpresaNombre, celda.Fecha.ToString("d", Cultura)];

    // Solo lo que «Reclamar» puede pedir a los contactos de ESTA Empresa (ver DocumentoSemanaDto.Reclamable).
    private static IReadOnlyList<DocumentoSemanaDto> Reclamables(SemanaEmpresaDto empresa) =>
        empresa.Documentos.Where(d => d.Reclamable).ToArray();

    // Una Empresa que solo emplea Trabajadores de subcontrata tiene su ficha en Subcontrata 360.
    private static string RutaFicha(SemanaEmpresaDto empresa) =>
        empresa.EsSubcontrata ? $"/subcontratas/{empresa.EmpresaId}" : $"/empresas/{empresa.EmpresaId}";

    // Un solo criterio para el color de la celda y el estado de la fila del detalle: rojo si ya venció (antes de hoy),
    // ámbar si vence hoy o después, como en la maqueta («Vence hoy» es ámbar).
    private bool EsVencido(DateOnly fecha) => fecha < Hoy;
    private sealed record CeldaSemana(DateOnly Fecha, IReadOnlyList<DocumentoSemanaDto> Documentos);
    private SemanaEmpresaDto? _empresa;
    private DateOnly? _dia;
    private void AbrirDia(SemanaEmpresaDto empresa, DateOnly dia) { _empresa = empresa; _dia = dia; }
    private void CerrarDia() => _dia = null;

    private IReadOnlyList<FilaDetalleDia> FilasDelDia => _empresa is { } empresa && _dia is { } dia
        ? DocumentosDelDia(empresa, dia).Select(d => new FilaDetalleDia(
            d.DocumentoId, d.SujetoNombre, d.TipoDocumentoNombre, TextoEstado(d.Fecha), TonoEstado(d.Fecha), d.Reclamable)).ToArray()
        : [];

    // El estado de la fila sale de la fecha frente a hoy, igual que el color de la celda (EsVencido); no hay un segundo
    // semáforo: la Semana por Empresa no calcula umbrales.
    private string TextoEstado(DateOnly fecha) =>
        EsVencido(fecha) ? Textos["EstadoVencido"]
        : fecha == Hoy ? Textos["EstadoVenceHoy"]
        : Textos["EstadoVenceEl", fecha.ToString(Textos["FormatoDiaMes"], Cultura)];

    private TonoBadge TonoEstado(DateOnly fecha) => EsVencido(fecha) ? TonoBadge.Peligro : TonoBadge.Advertencia;

    /// <summary>Sin manejador en quien aloja la vista no hay Reclamar en el detalle del día.</summary>
    private EventCallback<IReadOnlyList<Guid>> ReclamarDelDiaCallback =>
        OnReclamar.HasDelegate ? EventCallback.Factory.Create<IReadOnlyList<Guid>>(this, ReclamarDelDiaAsync) : default;

    // Reclamar desde el día acota la revisión a esos Documentos de ESTA Empresa; el cajón no ofrece otro titular.
    private async Task ReclamarDelDiaAsync(IReadOnlyList<Guid> documentoIds)
    {
        if (_empresa is not { } empresa) return;
        // Solo lo reclamable: nunca se pasa al cajón un documento que no se puede pedir a esta Empresa.
        var acotada = empresa with { Documentos = [.. empresa.Documentos.Where(d => d.Reclamable && documentoIds.Contains(d.DocumentoId))] };
        _dia = null;
        if (acotada.Documentos.Count == 0) return;
        await OnReclamar.InvokeAsync(acotada);
    }
}
