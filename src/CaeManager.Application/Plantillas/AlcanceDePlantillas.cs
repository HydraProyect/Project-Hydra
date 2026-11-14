using System.Linq.Expressions;
using CaeManager.Application.Common;
using CaeManager.Domain.Plantillas;

namespace CaeManager.Application.Plantillas;

/// <summary>
/// Asignación de Cartera de una <see cref="PlantillaDocumento"/> existente: el mismo criterio con el que
/// <c>CrearPlantillaDocumentoCommandHandler</c> decide si se puede crear una plantilla atada a un Centro o a un
/// Cliente empresarial. Una plantilla sin Centro ni Cliente empresarial es general del Tenant y queda dentro;
/// si lleva uno de los dos, o los dos, cada uno tiene que estar en el alcance de quien mira.
/// </summary>
public static class AlcanceDePlantillas
{
    /// <summary>
    /// ¿Alcanza quien mira la plantilla <paramref name="plantilla"/>? Lo usan las escrituras sobre una plantilla
    /// existente (agregar versión, guardar elementos, confirmar versión, iniciar un lote de generación) y sus lecturas
    /// (lista de plantillas, versión en el editor, y la de documentos generados a través de
    /// <see cref="AlcanceDeDocumentosGenerados"/>); todas tratan el «no» igual que una plantilla o versión que no
    /// existe, para no delatarla, y la lista simplemente la omite. GenerarDocumentoIndividual no lo
    /// llama: exige que el Centro y el Cliente empresarial de la petición sean visibles y coincidan con los de la
    /// plantilla, lo que equivale a este mismo criterio.
    /// </summary>
    public static Task<bool> PlantillaDentroDeAlcanceAsync(
        this IAlcanceDatosService alcance, PlantillaDocumento plantilla, CancellationToken cancellationToken = default) =>
        alcance.PlantillaDentroDeAlcanceAsync(CoordenadasEnMemoria(plantilla), cancellationToken);

    /// <summary>
    /// La única lectura de las coordenadas de una plantilla, como expresión: EF la traduce a SQL en
    /// <see cref="CoordenadasDePlantillas"/> y, compilada (<see cref="CoordenadasEnMemoria"/>), decide sobre la entidad
    /// ya cargada. Una sola expresión para que la columna que da el Cliente empresarial (deuda terminológica congelada
    /// en <c>ClienteId-ubicaciones.txt</c>) se lea en un único sitio. Se proyecta con inicializador, no con
    /// constructor, para que EF pueda seguir filtrando sobre la proyección.
    /// </summary>
    private static readonly Expression<Func<PlantillaDocumento, CoordenadasPlantilla>> Coordenadas =
        p => new CoordenadasPlantilla { Id = p.Id, CentroId = p.CentroId, ClienteEmpresarialId = p.ClienteId };

    private static readonly Func<PlantillaDocumento, CoordenadasPlantilla> CoordenadasEnMemoria = Coordenadas.Compile();

    /// <summary>
    /// Las dos coordenadas que decide <see cref="PlantillaDentroDeAlcanceAsync(IAlcanceDatosService, PlantillaDocumento, CancellationToken)"/>,
    /// proyectadas en la base para no cargar la entidad (<see cref="AlcanceDeDocumentosGenerados"/>).
    /// </summary>
    public static IQueryable<CoordenadasPlantilla> CoordenadasDePlantillas(this IQueryable<PlantillaDocumento> plantillas) =>
        plantillas.Select(Coordenadas);

    public static Task<bool> PlantillaDentroDeAlcanceAsync(
        this IAlcanceDatosService alcance, CoordenadasPlantilla plantilla, CancellationToken cancellationToken = default) =>
        alcance.PlantillaDentroDeAlcanceAsync(plantilla.CentroId, plantilla.ClienteEmpresarialId, cancellationToken);

    /// <summary>
    /// La misma decisión sobre las dos coordenadas de una plantilla ya proyectadas, para quien no carga la entidad
    /// (<see cref="AlcanceDeDocumentosGenerados"/>): es la única definición del criterio, las de arriba delegan aquí.
    /// </summary>
    public static async Task<bool> PlantillaDentroDeAlcanceAsync(
        this IAlcanceDatosService alcance, Guid? centroId, Guid? clienteEmpresarialId, CancellationToken cancellationToken = default)
    {
        if (centroId is { } centro && !await alcance.CentroVisibleAsync(centro, cancellationToken))
            return false;

        if (clienteEmpresarialId is { } clienteEmpresarial && !await alcance.ClienteVisibleAsync(clienteEmpresarial, cancellationToken))
            return false;

        return true;
    }

    /// <summary>
    /// El Cliente empresarial al que está atada la plantilla (<c>null</c> si no lo está), sobre la entidad ya cargada.
    /// Las lecturas con nombre canónico (la lista de plantillas, el DTO) pasan por aquí y no extienden el uso del
    /// identificador antiguo; la columna heredada se lee solo en <see cref="Coordenadas"/>.
    /// </summary>
    public static Guid? ClienteEmpresarialDe(this PlantillaDocumento plantilla) =>
        CoordenadasEnMemoria(plantilla).ClienteEmpresarialId;
}

/// <summary>Las coordenadas de una <see cref="PlantillaDocumento"/> que deciden su Asignación de Cartera.</summary>
public sealed class CoordenadasPlantilla
{
    public Guid Id { get; init; }
    public Guid? CentroId { get; init; }
    public Guid? ClienteEmpresarialId { get; init; }
}
