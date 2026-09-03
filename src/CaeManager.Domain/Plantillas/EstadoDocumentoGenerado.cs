namespace CaeManager.Domain.Plantillas;

public enum EstadoDocumentoGenerado
{
    Generado,

    /// <summary>
    /// El documento se generó, pero algo quedó señalado — dos motivos posibles,
    /// no excluyentes: DEC-5 (propietario, 2026-09-02), al menos un
    /// <see cref="PlantillaElemento"/> marcado <c>Obligatorio</c> resolvió a un
    /// valor vacío; o DEC-32/REC-115, un valor SÍ presente que el campo (radio o
    /// checkbox) no reconoció. Ninguno de los dos es un fallo — bloquear
    /// rompería lotes enteros por un campo— pero sí queda marcado para que un
    /// lote procesado de noche se pueda revisar por la mañana. Qué pasó en cada
    /// caso vive en <see cref="ItemGeneracionDocumento.Error"/> para los ítems
    /// de lote; la generación individual lo devuelve en el resultado del comando.
    /// </summary>
    GeneradoConAvisos
}
