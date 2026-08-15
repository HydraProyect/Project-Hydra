using CaeManager.Domain.Common;

namespace CaeManager.Domain.Plantillas;

/// <summary>
/// Versión concreta y congelada del contenido/configuración de una
/// <see cref="PlantillaDocumento"/> — ADR-010 § 1.3, § 2.9: si el centro
/// sustituye el PDF, es una versión nueva, no una edición silenciosa de la
/// anterior. Solo <see cref="EstadoConfiguracionPlantilla.Confirmada"/> es
/// una fuente de verdad operacional; mientras se edita, los elementos
/// pueden reemplazarse por completo sin dejar la versión anterior a medias
/// (nunca hay una versión parcialmente confirmada).
/// </summary>
public class PlantillaDocumentoVersion : EntidadConTenant
{
    public const int LongitudMaximaArchivoOriginalUrl = 500;
    public const int LongitudHashSha256 = 64;

    public Guid PlantillaDocumentoId { get; private set; }
    public int NumeroVersion { get; private set; }

    /// <summary>Solo <see cref="OrigenPlantilla.Externa"/> — el PDF que subió el gestor.</summary>
    public string? ArchivoOriginalUrl { get; private set; }

    /// <summary>Detecta si el archivo cambió desde que se confirmó esta versión (ADR-010, Plantillas PR 10).</summary>
    public string? HashSha256ArchivoOriginal { get; private set; }

    public EstadoConfiguracionPlantilla EstadoConfiguracion { get; private set; }
    public Guid? ConfirmadaPorUsuarioId { get; private set; }
    public DateTime? ConfirmadaEnUtc { get; private set; }

    /// <summary>Solo <see cref="OrigenPlantilla.TalvegPropio"/> — sin uso en este MVP.</summary>
    public DateOnly? VigenciaLegalHasta { get; private set; }

    private readonly List<PlantillaElemento> _elementos = [];
    public IReadOnlyList<PlantillaElemento> Elementos => _elementos.AsReadOnly();

    private PlantillaDocumentoVersion()
    {
    }

    public PlantillaDocumentoVersion(
        Guid plantillaDocumentoId, int numeroVersion, string archivoOriginalUrl, string hashSha256ArchivoOriginal)
    {
        if (plantillaDocumentoId == Guid.Empty)
            throw new ArgumentException("La versión debe pertenecer a una plantilla.", nameof(plantillaDocumentoId));
        if (numeroVersion < 1)
            throw new ArgumentException("El número de versión debe ser 1 o mayor.", nameof(numeroVersion));
        if (string.IsNullOrWhiteSpace(archivoOriginalUrl))
            throw new ArgumentException("El archivo original es obligatorio.", nameof(archivoOriginalUrl));
        if (archivoOriginalUrl.Length > LongitudMaximaArchivoOriginalUrl)
            throw new ArgumentException($"La URL del archivo no puede superar {LongitudMaximaArchivoOriginalUrl} caracteres.", nameof(archivoOriginalUrl));
        if (hashSha256ArchivoOriginal?.Length != LongitudHashSha256)
            throw new ArgumentException($"El hash SHA-256 debe tener exactamente {LongitudHashSha256} caracteres.", nameof(hashSha256ArchivoOriginal));

        PlantillaDocumentoId = plantillaDocumentoId;
        NumeroVersion = numeroVersion;
        ArchivoOriginalUrl = archivoOriginalUrl;
        HashSha256ArchivoOriginal = hashSha256ArchivoOriginal;
        EstadoConfiguracion = EstadoConfiguracionPlantilla.Borrador;
    }

    /// <summary>
    /// Reemplaza el conjunto completo de elementos — mismo criterio que
    /// <c>ContactoAgenda.EstablecerTiposDocumento</c>: la pantalla del
    /// editor visual guarda el conjunto entero, nunca "añade uno suelto".
    /// </summary>
    public void EstablecerElementos(IEnumerable<PlantillaElemento> elementos)
    {
        RequerirNoConfirmada();

        _elementos.Clear();
        _elementos.AddRange(elementos);
    }

    /// <summary>Se llama tras ejecutar la detección inicial (Plantillas PR 4), antes de que el gestor la revise.</summary>
    public void MarcarPendienteRevision()
    {
        RequerirNoConfirmada();
        EstadoConfiguracion = EstadoConfiguracionPlantilla.PendienteRevision;
    }

    public void Confirmar(Guid usuarioId, DateTime ahoraUtc)
    {
        RequerirNoConfirmada();
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("Quien confirma debe ser un usuario válido.", nameof(usuarioId));
        if (_elementos.Count == 0)
            throw new InvalidOperationException("No se puede confirmar una plantilla sin ningún elemento configurado.");

        EstadoConfiguracion = EstadoConfiguracionPlantilla.Confirmada;
        ConfirmadaPorUsuarioId = usuarioId;
        ConfirmadaEnUtc = ahoraUtc;
    }

    private void RequerirNoConfirmada()
    {
        if (EstadoConfiguracion == EstadoConfiguracionPlantilla.Confirmada)
            throw new InvalidOperationException("Esta versión ya está confirmada — crea una versión nueva para modificarla.");
    }
}
