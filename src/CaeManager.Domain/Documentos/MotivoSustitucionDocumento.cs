namespace CaeManager.Domain.Documentos;

/// <summary>
/// Por qué un <see cref="Documento"/> dejó de ser operativo al ser sustituido por otro
/// (<see cref="Documento.SustituirPor"/>). El motivo es de auditoría: no cambia qué documento cuenta.
///
/// <para>
/// <b>Ordinales congelados</b>: se persisten en <c>Documentos.MotivoSustitucion</c> y los vigila una
/// restricción CHECK. Cero no existe a propósito: «sin motivo» es la columna nula, que solo ocurre en un
/// documento no sustituido.
/// </para>
/// </summary>
public enum MotivoSustitucionDocumento
{
    /// <summary>La renovación del mismo documento: el nuevo ocupa el lugar del anterior.</summary>
    Renovacion = 1,

    /// <summary>Se subió un documento nuevo y el Gestor CAE indicó que sustituye al que estaba en uso.</summary>
    SubidaNueva = 2,

    /// <summary>
    /// Clasificación de datos existentes (migración de duplicados): el anterior estaba vencido y tiene un
    /// sustituto posterior, la única regla determinista que se aplica sin intervención de una persona.
    /// </summary>
    ClasificacionDeDatosExistentes = 3
}
