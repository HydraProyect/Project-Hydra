namespace CaeManager.Domain.Documentos;

/// <summary>
/// Qué datos, además de las fechas y el archivo, admite un Documento de un <see cref="TipoDocumento"/>
/// (catálogo aprobado el 2026-10-17). Es una máscara de bits persistida como entero, igual que
/// <see cref="Trabajadores.TiposContratacion"/>: un tipo puede admitir varios a la vez. Un dato que el tipo no
/// admite no se puede guardar en el Documento (lo comprueba <see cref="Documento"/>).
/// </summary>
[Flags]
public enum DatosAdicionalesDocumento
{
    Ninguno = 0,

    /// <summary>Horas de la formación, entero de 1 a 999 (<see cref="Documento.Horas"/>). Tipos de formación.</summary>
    Horas = 1,

    /// <summary>Nivel de la formación PRL, básico o intermedio (<see cref="Documento.Nivel"/>).</summary>
    Nivel = 2,

    /// <summary>
    /// Recibo de pago como adjunto opcional del mismo documento. El Documento aún no tiene dónde guardarlo: hoy es solo
    /// la marca del catálogo. El estado del documento no depende del recibo.
    /// </summary>
    ReciboAdjunto = 4,

    /// <summary>
    /// Estado «Renuncia» del Certificado de aptitud médica (<see cref="Documento.EsRenuncia"/>): registra solo la
    /// conclusión, nunca datos de salud, y no sustituye al apto donde la vigilancia de la salud es obligatoria.
    /// </summary>
    Renuncia = 8,

    Todos = Horas | Nivel | ReciboAdjunto | Renuncia
}

/// <summary>Nivel de una formación PRL (catálogo aprobado el 2026-10-17): sustituye a un tipo propio de recurso preventivo.</summary>
public enum NivelFormacionPrl
{
    Basico = 1,
    Intermedio = 2
}
