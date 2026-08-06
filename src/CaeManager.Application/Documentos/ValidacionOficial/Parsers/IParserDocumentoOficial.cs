using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos.ValidacionOficial.Parsers;

/// <summary>
/// Extracción determinista de los campos de un documento oficial a partir de
/// su texto digital — sin IA: el texto de un PDF firmado por la
/// Administración es capa digital exacta, y una regex sobre anclas conocidas
/// es gratis, reproducible y testeable (PLAN-FIRMA-DIGITAL-PDF.md).
/// </summary>
public interface IParserDocumentoOficial
{
    PerfilDocumentoOficial Perfil { get; }

    /// <param name="textoNormalizado">Texto completo del PDF con los espacios colapsados (ver <see cref="ParserDocumentoOficialBase.NormalizarTexto"/>).</param>
    DatosDocumentoOficialExtraidos Extraer(string textoNormalizado);
}

/// <summary>
/// <paramref name="ResultadoPositivo"/> solo aplica a los certificados de
/// estar al corriente (null en el resto de perfiles): la TGSS y la AEAT
/// también emiten el certificado <b>en negativo</b> ("NO se encuentra al
/// corriente"), y ese documento jamás debe auto-validarse.
/// <paramref name="CamposObligatoriosFaltantes"/> no vacío significa que el
/// texto no encajó con las anclas del perfil — cae a revisión humana, nunca
/// a un falso auto-validado.
/// </summary>
public record DatosDocumentoOficialExtraidos(
    string? CodigoVerificacion,
    string? Cif,
    string? RazonSocial,
    DateOnly? FechaEmision,
    string? Periodo,
    bool? ResultadoPositivo,
    IReadOnlyList<string> CamposObligatoriosFaltantes);
