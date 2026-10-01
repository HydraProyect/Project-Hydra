using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;

namespace CaeManager.Application.Documentos.DocumentacionBase;

/// <summary>
/// Los cuatro documentos que se muestran primero de un Trabajador. Es una vista de
/// «¿está al día en lo básico?», NO una exigencia: ningún tipo de aquí es obligatorio por
/// oficio, y estar al día en lo básico no equivale a ser apto para un Centro (eso es
/// <c>TipoDocumentoCentro</c>, otro plano).
/// </summary>
public enum TipoDocumentoBase
{
    AptitudMedica = 1,
    FormacionArt19 = 2,
    InformacionArt18 = 3,
    EntregaEpi = 4
}

/// <summary>
/// Estado de un indicador. Parte de <see cref="EstadoDocumento"/> (la única fuente de vigencia):
/// Vigente y SinCaducidad → <see cref="Vigente"/> (Información Art. 18 «presente» al no vencer);
/// Proximo y Urgente → <see cref="ProximoAVencer"/>; Vencido; SinConfirmar queda aparte porque
/// «no lo sé» no es «vigente» (aunque cuenta como al día, con aviso: ver
/// <see cref="DocumentacionBaseTrabajadorDto.AlDia"/>); sin documento → <see cref="Falta"/>.
/// </summary>
public enum EstadoIndicadorBase
{
    Vigente = 1,
    ProximoAVencer = 2,
    Vencido = 3,
    Falta = 4,
    SinConfirmar = 5
}

public record IndicadorDocumentacionBase(
    TipoDocumentoBase Tipo,
    EstadoIndicadorBase Estado,
    Guid? DocumentoId,
    DateOnly? FechaVencimiento);

public record DocumentacionBaseTrabajadorDto(IReadOnlyList<IndicadorDocumentacionBase> Indicadores)
{
    /// <summary>
    /// Al día = ningún indicador Falta ni Vencido. «Próximo a vencer» sigue siendo válido hoy
    /// (como en <see cref="PreferenciaDocumentoPorTipo"/>) y «Vigencia sin confirmar» también
    /// cuenta (decisión del propietario, 2026-10-01): el documento está y lo pendiente es
    /// confirmar su fecha. Los dos se avisan aparte, en el indicador y, el segundo, también en
    /// el resumen (<see cref="TieneVigenciaSinConfirmar"/>).
    /// </summary>
    public bool AlDia => Indicadores.All(i => i.Estado is EstadoIndicadorBase.Vigente
        or EstadoIndicadorBase.ProximoAVencer or EstadoIndicadorBase.SinConfirmar);

    /// <summary>Algún indicador tiene la vigencia sin confirmar: el panel lo avisa aunque esté al día.</summary>
    public bool TieneVigenciaSinConfirmar => Indicadores.Any(i => i.Estado == EstadoIndicadorBase.SinConfirmar);
}

public record DocumentoParaDocumentacionBase(
    Guid Id,
    string TipoDocumentoNombre,
    EstadoVigenciaDocumento EstadoVigencia,
    DateOnly? FechaVencimiento,
    DateOnly FechaEmision);

public static class DocumentacionBaseTrabajador
{
    /// <summary>
    /// Reconoce el tipo básico por el nombre del catálogo (el catálogo no marca «básico»):
    /// «Certificado de aptitud médica» y «Reconocimiento médico» son el mismo tipo.
    /// Un tenant que renombre el tipo lo saca de aquí.
    /// </summary>
    public static TipoDocumentoBase? Clasificar(string? nombreTipo) =>
        NormalizadorEtiquetaCampo.Normalizar(nombreTipo) switch
        {
            "certificado de aptitud medica" or "reconocimiento medico" or "aptitud medica" => TipoDocumentoBase.AptitudMedica,
            "formacion art 19" => TipoDocumentoBase.FormacionArt19,
            "informacion art 18" => TipoDocumentoBase.InformacionArt18,
            "entrega de epi" => TipoDocumentoBase.EntregaEpi,
            _ => null
        };

    public static DocumentacionBaseTrabajadorDto Calcular(
        IEnumerable<DocumentoParaDocumentacionBase> documentos, DateOnly hoy, int umbralAmbarDias, int umbralRojoDias)
    {
        var porTipo = documentos
            .Select(d => (Tipo: Clasificar(d.TipoDocumentoNombre), Documento: d))
            .Where(x => x.Tipo is not null)
            .GroupBy(x => x.Tipo!.Value)
            .ToDictionary(g => g.Key, g => PreferenciaDocumentoPorTipo.Ordenar(
                g.Select(x => x.Documento), d => d.EstadoVigencia, d => d.FechaVencimiento, d => d.FechaEmision, hoy).First());

        var indicadores = Enum.GetValues<TipoDocumentoBase>()
            .Select(tipo => porTipo.TryGetValue(tipo, out var d)
                ? new IndicadorDocumentacionBase(tipo, Traducir(
                    CalculadoraEstadoDocumento.Calcular(d.EstadoVigencia, d.FechaVencimiento, hoy, umbralAmbarDias, umbralRojoDias)), d.Id, d.FechaVencimiento)
                : new IndicadorDocumentacionBase(tipo, EstadoIndicadorBase.Falta, null, null))
            .ToList();

        return new DocumentacionBaseTrabajadorDto(indicadores);
    }

    /// <summary>
    /// Si un documento requerido cuenta como incidencia en Trabajador 360 (D-22 del recorrido de
    /// 2026-10-01). Una sola regla con el panel Documentación base: lo que allí es
    /// <see cref="EstadoIndicadorBase.SinConfirmar"/> cuenta como al día, con aviso, y no como
    /// incidencia; Faltante, Vencido, Urgente y Próximo siguen siéndolo.
    /// </summary>
    public static bool CuentaComoIncidencia(EstadoDocumento estado) =>
        Traducir(estado) is not (EstadoIndicadorBase.Vigente or EstadoIndicadorBase.SinConfirmar);

    public static EstadoIndicadorBase Traducir(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vigente or EstadoDocumento.SinCaducidad => EstadoIndicadorBase.Vigente,
        EstadoDocumento.Proximo or EstadoDocumento.Urgente => EstadoIndicadorBase.ProximoAVencer,
        EstadoDocumento.Vencido => EstadoIndicadorBase.Vencido,
        EstadoDocumento.SinConfirmar => EstadoIndicadorBase.SinConfirmar,
        // Faltante no sale de la calculadora; ante un valor nuevo no se promete vigencia.
        _ => EstadoIndicadorBase.Falta
    };
}
