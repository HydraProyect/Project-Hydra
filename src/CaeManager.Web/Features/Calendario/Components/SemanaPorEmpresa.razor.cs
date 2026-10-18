using System.Globalization;
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
        Textos["DocumentosVencen", celda.Documentos.Count, empresa.EmpresaNombre, celda.Fecha.ToString("d", Cultura)];
    private sealed record CeldaSemana(DateOnly Fecha, IReadOnlyList<DocumentoSemanaDto> Documentos);
    private SemanaEmpresaDto? _empresa;
    private DateOnly? _dia;
    private void AbrirDia(SemanaEmpresaDto empresa, DateOnly dia) { _empresa = empresa; _dia = dia; }
}
