using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Posición explícita de un Centro sobre un Tipo de Documento — <see cref="Incluido"/>
/// manda sobre el criterio global de <see cref="TipoDocumento.EsObligatorio"/> cuando
/// existe una fila para el par; sin fila, el par sigue el criterio global
/// (Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.4, redacción 2026-08-06 — antes de esta fecha la mera
/// presencia de CUALQUIER fila para un Tipo restringía ese Tipo a solo esos Centros
/// en todo el tenant, sin permitir excluir un único Centro de un tipo obligatorio;
/// ver <see cref="Documentos.ResolucionTipoDocumentoCentro"/> para la lectura
/// centralizada de esta semántica). Sin ciclo de vida propio (igual que
/// EmpresaCliente): desvincular es una baja física.
///
/// Absorbe también lo que antes vivía en RequisitoDocumental (retirado en el mismo
/// lote): <see cref="PeriodicidadEspecialMeses"/> (vigencia de ESTE Centro para el Tipo,
/// contada desde la fecha de emisión del Documento; null = el Centro no impone una propia),
/// <see cref="BloqueaAcceso"/> (el Tipo es un requisito bloqueante de ESTE Centro: su falta,
/// o que ya no valga con las condiciones de este Centro, impide el acceso a este Centro) y
/// <see cref="ToleranciaDias"/> (personalización de ESTE Centro de los días que el documento sigue
/// valiendo para el acceso tras su vencimiento efectivo; <c>null</c> = hereda la tolerancia del Cliente
/// empresarial titular del Centro, <see cref="ToleranciaDocumentoClienteEmpresarial"/>, y sin ella 0). La regla única, sus sujetos —el Trabajador o su
/// Empresa— y su evaluación POR CENTRO están en <see cref="ReglaBloqueoDeAcceso"/>. «Bloqueado» es
/// un estado del Trabajador, no del Centro (corrección del propietario, 2026-10-03). El adjunto
/// (<see cref="ArchivoUrl"/>) es la plantilla en blanco a rellenar, no un
/// justificante con caducidad — mismo criterio que tenía RequisitoDocumental.
/// </summary>
public class TipoDocumentoCentro : EntidadConTenant
{
    public const int LongitudMaximaArchivoUrl = 500;
    public const int LongitudMaximaNombreArchivo = 260;

    /// <summary>
    /// Cota técnica de la tolerancia (un año): evita desbordar el calendario al sumarla a una fecha, no es una
    /// regla de negocio. El propietario habla de 0, 5, 10, 15, 20 días.
    /// </summary>
    public const int ToleranciaMaximaDias = 365;

    public Guid TipoDocumentoId { get; private set; }
    public Guid CentroId { get; private set; }
    public bool Incluido { get; private set; } = true;
    public int? PeriodicidadEspecialMeses { get; private set; }
    public bool BloqueaAcceso { get; private set; }

    /// <summary>
    /// Personalización de este Centro de la tolerancia (días que el Documento sigue valiendo para ACCEDER a este
    /// Centro tras su vencimiento efectivo en él, <see cref="ReglaBloqueoDeAcceso.VencimientoEfectivo"/>).
    /// <c>null</c> = hereda la del Cliente empresarial titular (<see cref="ReglaBloqueoDeAcceso.ResolverToleranciaDias"/>).
    /// Solo afecta al acceso: el estado de vigencia que se muestra (Vencido) no cambia.
    /// </summary>
    public int? ToleranciaDias { get; private set; }

    public string? ArchivoUrl { get; private set; }
    public string? NombreArchivoOriginal { get; private set; }

    private TipoDocumentoCentro()
    {
    }

    public TipoDocumentoCentro(
        Guid tipoDocumentoId, Guid centroId, bool incluido = true,
        int? periodicidadEspecialMeses = null, bool bloqueaAcceso = false,
        string? archivoUrl = null, string? nombreArchivoOriginal = null, int? toleranciaDias = null)
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
        EstablecerTolerancia(toleranciaDias);
        EstablecerArchivo(archivoUrl, nombreArchivoOriginal);
    }

    /// <param name="toleranciaDias">
    /// Obligatorio a propósito: quien actualiza la fila tiene que decir qué personalización deja (<c>null</c> = hereda),
    /// para que un guardado que no la conoce no la borre en silencio.
    /// </param>
    public void Actualizar(
        bool incluido, int? periodicidadEspecialMeses, bool bloqueaAcceso,
        string? archivoUrl, string? nombreArchivoOriginal, int? toleranciaDias)
    {
        Incluido = incluido;
        EstablecerPeriodicidadEspecial(periodicidadEspecialMeses);
        BloqueaAcceso = bloqueaAcceso;
        EstablecerTolerancia(toleranciaDias);
        EstablecerArchivo(archivoUrl, nombreArchivoOriginal);
    }

    private void EstablecerTolerancia(int? dias)
    {
        if (dias is < 0 or > ToleranciaMaximaDias)
            throw new ArgumentException(
                $"La tolerancia debe ser un número entero de días entre 0 y {ToleranciaMaximaDias}, o vacía para heredar la del Cliente empresarial.",
                nameof(dias));

        ToleranciaDias = dias;
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
