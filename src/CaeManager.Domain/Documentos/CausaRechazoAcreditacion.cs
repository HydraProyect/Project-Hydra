namespace CaeManager.Domain.Documentos;

/// <summary>
/// Causa tipificada de un rechazo en plataforma (docs/ux-audit/PLAN-EJECUCION-UX.md
/// § Parte 2 (b)) — obligatoria junto al motivo literal de la plataforma
/// siempre que <see cref="AcreditacionDocumentoPlataforma.Estado"/> pasa a
/// <see cref="EstadoAcreditacion.Rechazada"/>. Alimenta la futura query de
/// calidad documental (§ Parte 2 (g), "rechazos por causa × plataforma ×
/// Empresa") — de ahí que sea un catálogo cerrado y no texto libre.
/// </summary>
public enum CausaRechazoAcreditacion
{
    Ilegible = 0,
    DocumentoEquivocado = 1,
    DatosErroneos = 2,
    CaducadoAlPresentar = 3,
    FaltaFirmaOSello = 4,
    FormatoNoAdmitido = 5,
    Otro = 6
}
