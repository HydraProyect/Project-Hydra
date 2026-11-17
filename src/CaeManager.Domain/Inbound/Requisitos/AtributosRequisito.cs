using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;

namespace CaeManager.Domain.Inbound.Requisitos;

/// <summary>
/// Los atributos que el Gestor CAE del Tenant TCE fija en un requisito de su plantilla (tarjeta 9; plan backend Inbound
/// § 2.3, <c>RequisitoPlantilla</c>). Es un valor: <see cref="RequisitoPlantilla"/> lo valida contra su tipo de documento y
/// su plantilla al añadirse o editarse, y lo guarda en columnas propias.
/// </summary>
public sealed record AtributosRequisito(
    OrigenRequisito Origen,
    ModoVigenciaRequisito ModoVigencia,
    int? VigenciaMeses,
    int? ToleranciaDias,
    bool BloqueaAcceso,
    string? Criterio,
    // shortcut: SePideA queda sin leerse; retirar la columna en un incremento posterior (decisión 2026-11-17)
    DestinatarioRequisito SePideA,
    TiposContratacion TiposContratacion,
    IReadOnlyList<string> Equivalencias,
    AmbitoDocumentoRequisito AmbitoDocumento)
{
    /// <summary>Criterio de validación que lee el Gestor CAE y la IA, como en la tarjeta 9.</summary>
    public const int LongitudMaximaCriterio = 600;

    public const int VigenciaMaximaMeses = 120;

    public const int MaximoEquivalencias = 10;

    public const int LongitudMaximaEquivalencia = 200;

    public const int ToleranciaMaximaDias = TipoDocumentoCentro.ToleranciaMaximaDias;
}
