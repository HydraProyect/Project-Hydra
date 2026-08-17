using CaeManager.Domain.Common;

namespace CaeManager.Domain.Importacion;

/// <summary>
/// Registro append-only de una ejecución de importación (Configuración →
/// Importar datos). Se escribe una fila por cada intento de confirmar una
/// importación (éxito o fallo) — nunca por un simple análisis, que no
/// escribe nada todavía. <see cref="Plantilla"/> es el label libre del
/// wizard ("CAE completa", "Clientes", "Combinada", "Documentos"), no un
/// enum: son 4 plantillas hoy y el nombre solo alimenta el historial, no
/// ninguna decisión de negocio.
/// </summary>
public class HistorialImportacion : EntidadConTenant
{
    public string Plantilla { get; private set; } = string.Empty;
    public string NombreArchivo { get; private set; } = string.Empty;
    public DateTime EjecutadaEnUtc { get; private set; }
    public Guid EjecutadaPorUsuarioId { get; private set; }
    public bool Exitosa { get; private set; }
    public int TotalCreados { get; private set; }
    public int TotalAdvertencias { get; private set; }
    public int TotalOmitidos { get; private set; }
    public string? MensajeError { get; private set; }

    private HistorialImportacion()
    {
    }

    public static HistorialImportacion Exito(
        string plantilla, string nombreArchivo, Guid ejecutadaPorUsuarioId,
        int totalCreados, int totalAdvertencias, int totalOmitidos) =>
        new()
        {
            Plantilla = RequerirPlantilla(plantilla),
            NombreArchivo = nombreArchivo,
            EjecutadaEnUtc = DateTime.UtcNow,
            EjecutadaPorUsuarioId = ejecutadaPorUsuarioId,
            Exitosa = true,
            TotalCreados = totalCreados,
            TotalAdvertencias = totalAdvertencias,
            TotalOmitidos = totalOmitidos
        };

    public static HistorialImportacion Fallo(
        string plantilla, string nombreArchivo, Guid ejecutadaPorUsuarioId, string mensajeError) =>
        new()
        {
            Plantilla = RequerirPlantilla(plantilla),
            NombreArchivo = nombreArchivo,
            EjecutadaEnUtc = DateTime.UtcNow,
            EjecutadaPorUsuarioId = ejecutadaPorUsuarioId,
            Exitosa = false,
            MensajeError = mensajeError
        };

    private static string RequerirPlantilla(string plantilla) =>
        string.IsNullOrWhiteSpace(plantilla)
            ? throw new ArgumentException("La plantilla no puede estar vacía.", nameof(plantilla))
            : plantilla;
}
