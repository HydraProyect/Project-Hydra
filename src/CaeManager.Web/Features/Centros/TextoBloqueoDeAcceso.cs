using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Domain.Documentos;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Centros;

/// <summary>
/// Cómo se dice, en las superficies del Centro (Centro 360 y su panel lateral), por qué un Trabajador está bloqueado en ese
/// Centro. Vive en un solo sitio para que las dos superficies lo digan igual. «Bloqueado» es un estado del Trabajador, nunca
/// del Centro (decisión del propietario, 2026-10-03): el Centro enseña el detalle por Trabajador.
/// </summary>
public static class TextoBloqueoDeAcceso
{
    /// <summary>Cuántos Trabajadores distintos hay en estos bloqueos.</summary>
    public static int TrabajadoresBloqueados(IReadOnlyList<DocumentacionBloqueantePendienteDto> bloqueos) =>
        bloqueos.Select(b => b.TrabajadorId).Distinct().Count();

    public static string Titulo(IStringLocalizer textos, int trabajadores) =>
        trabajadores == 1 ? textos["BloqueadosTrabajadoresUno"].Value : textos["BloqueadosTrabajadoresVarios", trabajadores].Value;

    /// <summary>«Trabajador · Tipo (Empresa): situación», con la tolerancia que rige en el Centro cuando la hay.</summary>
    public static string Linea(IStringLocalizer textos, DocumentacionBloqueantePendienteDto bloqueo) =>
        $"{bloqueo.TrabajadorNombre} · {LineaDeDocumento(textos, bloqueo)}";

    /// <summary>«Tipo (Empresa): situación», sin el Trabajador: para donde el Trabajador ya está dicho (su propia fila).</summary>
    public static string LineaDeDocumento(IStringLocalizer textos, DocumentacionBloqueantePendienteDto bloqueo)
    {
        var situacion = bloqueo.Situacion switch
        {
            SituacionDeRequisitoBloqueante.Ausente => textos["BloqueoSituacionAusente"].Value,
            // El veredicto de la plataforma del Cliente empresarial no concede tolerancia (D-7): se dice tal cual.
            SituacionDeRequisitoBloqueante.RechazadoPorPlataforma => textos["BloqueoSituacionRechazadoPlataforma"].Value,
            SituacionDeRequisitoBloqueante.VencidoEnPlataforma when bloqueo.VencimientoEfectivo is { } vencimientoPlataforma =>
                textos["BloqueoSituacionVencidoPlataforma", vencimientoPlataforma.ToString("dd/MM/yyyy")].Value,
            SituacionDeRequisitoBloqueante.VencidoEnPlataforma => textos["BloqueoSituacionVencidoPlataformaSinFecha"].Value,
            _ when bloqueo.VencimientoEfectivo is { } vencimiento && bloqueo.ToleranciaDias > 0 =>
                textos["BloqueoSituacionFinTolerancia", vencimiento.ToString("dd/MM/yyyy"), bloqueo.ToleranciaDias].Value,
            _ when bloqueo.VencimientoEfectivo is { } vencimiento =>
                textos["BloqueoSituacionVencido", vencimiento.ToString("dd/MM/yyyy")].Value,
            _ => textos["BloqueoSituacionVencidoSinFecha"].Value
        };
        var tipo = bloqueo.Ambito == AmbitoAplicacion.Empresa
            ? textos["BloqueoTipoDeEmpresa", bloqueo.TipoDocumentoNombre].Value
            : bloqueo.TipoDocumentoNombre;
        return $"{tipo}: {situacion}";
    }
}
