namespace CaeManager.Domain.Plantillas;

public enum EstadoDocumentoGenerado
{
    Generado,

    /// <summary>
    /// DEC-5 (propietario, 2026-09-02): el documento se generó, pero al menos un
    /// <see cref="PlantillaElemento"/> marcado <c>Obligatorio</c> resolvió a un
    /// valor vacío. No es un fallo — bloquear rompería lotes enteros por un campo—
    /// pero sí queda marcado para que un lote procesado de noche se pueda revisar
    /// por la mañana. Qué campos fueron se reconstruye cruzando
    /// <see cref="DocumentoGenerado.DatosUtilizadosJson"/> con los elementos
    /// <c>Obligatorio</c> de la versión de plantilla.
    /// </summary>
    GeneradoConAvisos
}
