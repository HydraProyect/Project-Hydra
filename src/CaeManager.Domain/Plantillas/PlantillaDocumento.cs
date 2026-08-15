using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;

namespace CaeManager.Domain.Plantillas;

/// <summary>
/// Formulario reutilizable a partir del cual se generan documentos rellenos
/// con datos de Hydra — un centro entrega un PDF obligatorio
/// (<see cref="OrigenPlantilla.Externa"/>) que se cumplimenta una vez por
/// Trabajador/Empresa en vez de copiarse a mano (ADR-010).
///
/// El contenido y la configuración de campos viven en
/// <see cref="PlantillaDocumentoVersion"/>, no aquí — esta entidad es el
/// encabezado estable (nombre, ámbito, a qué Centro/Cliente pertenece) que
/// sobrevive a que el centro sustituya el PDF por una versión nueva.
/// </summary>
public class PlantillaDocumento : EntidadConTenant
{
    public const int LongitudMaximaNombre = 200;
    public const int LongitudMaximaDescripcion = 1000;
    public const int LongitudMaximaCodigoFormato = 20;

    public OrigenPlantilla Origen { get; private set; }
    public string Nombre { get; private set; } = string.Empty;
    public string? Descripcion { get; private set; }
    public AmbitoAplicacion AmbitoAplicacion { get; private set; }

    /// <summary>
    /// Bajo qué TipoDocumento cuenta cada documento generado a partir de esta
    /// plantilla — un <see cref="Documentos.Documento"/> siempre exige uno
    /// (ver <c>Documento</c>, campo obligatorio). Su
    /// <c>AmbitoAplicacion</c> debe coincidir con <see cref="AmbitoAplicacion"/>
    /// (validado al crear la plantilla, mismo criterio que
    /// <c>CrearDocumentoCommandHandler</c>).
    /// </summary>
    public Guid TipoDocumentoId { get; private set; }

    /// <summary>Alcance opcional — a qué Centro o Cliente pertenece esta plantilla (ADR-010 § 2.9: "Centro-specific"). Ninguno de los dos informado = disponible para todo el tenant.</summary>
    public Guid? CentroId { get; private set; }
    public Guid? ClienteId { get; private set; }

    /// <summary>Código F-xx — solo <see cref="OrigenPlantilla.TalvegPropio"/>, sin uso en este MVP (§ 2.2).</summary>
    public string? CodigoFormato { get; private set; }

    public FormatoOrigenPlantilla FormatoOrigen { get; private set; }
    public EstadoPlantillaDocumento Estado { get; private set; }

    /// <summary>Versión confirmada vigente — null hasta que exista al menos una <see cref="PlantillaDocumentoVersion"/> confirmada.</summary>
    public Guid? VersionActualId { get; private set; }

    private PlantillaDocumento()
    {
    }

    public PlantillaDocumento(
        OrigenPlantilla origen,
        string nombre,
        AmbitoAplicacion ambitoAplicacion,
        FormatoOrigenPlantilla formatoOrigen,
        Guid tipoDocumentoId,
        string? descripcion = null,
        Guid? centroId = null,
        Guid? clienteId = null,
        string? codigoFormato = null)
    {
        if (tipoDocumentoId == Guid.Empty)
            throw new ArgumentException("La plantilla debe indicar bajo qué tipo de documento cuentan sus generaciones.", nameof(tipoDocumentoId));

        EstablecerNombre(nombre);
        Origen = origen;
        AmbitoAplicacion = ambitoAplicacion;
        FormatoOrigen = formatoOrigen;
        TipoDocumentoId = tipoDocumentoId;
        EstablecerDescripcion(descripcion);
        CentroId = centroId;
        ClienteId = clienteId;
        EstablecerCodigoFormato(codigoFormato);
        Estado = EstadoPlantillaDocumento.Activa;
    }

    /// <summary>Se llama al confirmar una <see cref="PlantillaDocumentoVersion"/> — esa versión pasa a ser la fuente de verdad para generar documentos.</summary>
    public void EstablecerVersionActual(Guid versionId)
    {
        if (versionId == Guid.Empty)
            throw new ArgumentException("La versión activa no puede estar vacía.", nameof(versionId));

        VersionActualId = versionId;
    }

    public void Archivar() => Estado = EstadoPlantillaDocumento.Archivada;

    public void Reactivar() => Estado = EstadoPlantillaDocumento.Activa;

    private void EstablecerNombre(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El nombre de la plantilla es obligatorio.", nameof(nombre));

        var normalizado = nombre.Trim();
        if (normalizado.Length > LongitudMaximaNombre)
            throw new ArgumentException($"El nombre no puede superar {LongitudMaximaNombre} caracteres.", nameof(nombre));

        Nombre = normalizado;
    }

    private void EstablecerDescripcion(string? descripcion)
    {
        if (descripcion is not null && descripcion.Length > LongitudMaximaDescripcion)
            throw new ArgumentException($"La descripción no puede superar {LongitudMaximaDescripcion} caracteres.", nameof(descripcion));

        Descripcion = string.IsNullOrWhiteSpace(descripcion) ? null : descripcion.Trim();
    }

    private void EstablecerCodigoFormato(string? codigoFormato)
    {
        if (codigoFormato is not null && codigoFormato.Length > LongitudMaximaCodigoFormato)
            throw new ArgumentException($"El código de formato no puede superar {LongitudMaximaCodigoFormato} caracteres.", nameof(codigoFormato));

        CodigoFormato = string.IsNullOrWhiteSpace(codigoFormato) ? null : codigoFormato.Trim();
    }
}
