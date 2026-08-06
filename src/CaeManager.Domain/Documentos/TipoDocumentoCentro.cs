using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Posición explícita de un Centro sobre un Tipo de Documento — <see cref="Incluido"/>
/// manda sobre el criterio global de <see cref="TipoDocumento.EsObligatorio"/> cuando
/// existe una fila para el par; sin fila, el par sigue el criterio global
/// (PLAN-EJECUCION-UX.md § 0.4, redacción 2026-08-06 — antes de esta fecha la mera
/// presencia de CUALQUIER fila para un Tipo restringía ese Tipo a solo esos Centros
/// en todo el tenant, sin permitir excluir un único Centro de un tipo obligatorio;
/// ver <see cref="Documentos.ResolucionTipoDocumentoCentro"/> para la lectura
/// centralizada de esta semántica). Sin ciclo de vida propio (igual que
/// EmpresaCliente): desvincular es una baja física.
///
/// Absorbe también lo que antes vivía en RequisitoDocumental (retirado en el mismo
/// lote): <see cref="PeriodicidadEspecial"/> (override de la vigencia del Tipo solo
/// para este Centro, null = no vence) y <see cref="BloqueaAcceso"/> (si ningún
/// Trabajador con Asignación activa tiene un Documento Vigente de este Tipo en este
/// Centro, fuerza el Centro a EstadoCentro.Bloqueado — ver
/// CalculoEstadoCentroService.AgregarCausasDeRequisitosBloqueantesAsync). El adjunto
/// (<see cref="ArchivoUrl"/>) es la plantilla en blanco a rellenar, no un
/// justificante con caducidad — mismo criterio que tenía RequisitoDocumental.
/// </summary>
public class TipoDocumentoCentro : EntidadConTenant
{
    public const int LongitudMaximaArchivoUrl = 500;
    public const int LongitudMaximaNombreArchivo = 260;

    public Guid TipoDocumentoId { get; private set; }
    public Guid CentroId { get; private set; }
    public bool Incluido { get; private set; } = true;
    public int? PeriodicidadEspecialMeses { get; private set; }
    public bool BloqueaAcceso { get; private set; }
    public string? ArchivoUrl { get; private set; }
    public string? NombreArchivoOriginal { get; private set; }

    private TipoDocumentoCentro()
    {
    }

    public TipoDocumentoCentro(
        Guid tipoDocumentoId, Guid centroId, bool incluido = true,
        int? periodicidadEspecialMeses = null, bool bloqueaAcceso = false,
        string? archivoUrl = null, string? nombreArchivoOriginal = null)
    {
        if (tipoDocumentoId == Guid.Empty)
            throw new ArgumentException("La asociación debe tener un tipo de documento.", nameof(tipoDocumentoId));
        if (centroId == Guid.Empty)
            throw new ArgumentException("La asociación debe tener un centro.", nameof(centroId));

        TipoDocumentoId = tipoDocumentoId;
        CentroId = centroId;
        Incluido = incluido;
        EstablecerPeriodicidadEspecial(periodicidadEspecialMeses);
        BloqueaAcceso = bloqueaAcceso;
        EstablecerArchivo(archivoUrl, nombreArchivoOriginal);
    }

    public void Actualizar(
        bool incluido, int? periodicidadEspecialMeses, bool bloqueaAcceso,
        string? archivoUrl, string? nombreArchivoOriginal)
    {
        Incluido = incluido;
        EstablecerPeriodicidadEspecial(periodicidadEspecialMeses);
        BloqueaAcceso = bloqueaAcceso;
        EstablecerArchivo(archivoUrl, nombreArchivoOriginal);
    }

    private void EstablecerPeriodicidadEspecial(int? meses)
    {
        if (meses is <= 0)
            throw new ArgumentException("La periodicidad especial debe ser un número entero de meses mayor que cero, o vacío si no vence.", nameof(meses));

        PeriodicidadEspecialMeses = meses;
    }

    private void EstablecerArchivo(string? archivoUrl, string? nombreArchivoOriginal)
    {
        if (archivoUrl?.Length > LongitudMaximaArchivoUrl)
            throw new ArgumentException($"La URL del archivo no puede superar {LongitudMaximaArchivoUrl} caracteres.", nameof(archivoUrl));
        if (nombreArchivoOriginal?.Length > LongitudMaximaNombreArchivo)
            throw new ArgumentException($"El nombre del archivo no puede superar {LongitudMaximaNombreArchivo} caracteres.", nameof(nombreArchivoOriginal));

        ArchivoUrl = archivoUrl;
        NombreArchivoOriginal = nombreArchivoOriginal;
    }
}
