using CaeManager.Domain.Common;

namespace CaeManager.Domain.Proyectos;

/// <summary>
/// Conjunto con nombre de Tipos de Documento que se comparte entre eventos (Proyectos de tipo
/// <see cref="TipoProyecto.Evento"/>): al crear un evento, sus requisitos se copian de una plantilla o de otro
/// evento en vez de reintroducirse. La copia es una foto: cambiar o borrar la plantilla después no altera los
/// eventos ya creados.
///
/// Pertenece a un Cliente empresarial (<see cref="ClienteId"/>), igual que el Proyecto: la autoridad para
/// verla o usarla es la misma que sobre los Proyectos de ese Cliente (<c>ProyectoAutorizacion</c>), sin una
/// regla de visibilidad nueva. El nombre es único por Cliente.
/// </summary>
public class PlantillaRequisitosEvento : EntidadConTenant
{
    public const int LongitudMaximaNombre = 200;

    private readonly List<PlantillaRequisitosEventoTipo> _tipos = [];

    public Guid ClienteId { get; private set; }
    public string Nombre { get; private set; } = string.Empty;
    public IReadOnlyCollection<PlantillaRequisitosEventoTipo> Tipos => _tipos;

    private PlantillaRequisitosEvento()
    {
    }

    public static PlantillaRequisitosEvento Crear(Guid clienteId, string nombre, IEnumerable<Guid> tipoDocumentoIds)
    {
        if (clienteId == Guid.Empty)
            throw new ArgumentException("La plantilla debe pertenecer a un cliente.", nameof(clienteId));
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El nombre de la plantilla es obligatorio.", nameof(nombre));

        var normalizado = nombre.Trim();
        if (normalizado.Length > LongitudMaximaNombre)
            throw new ArgumentException($"El nombre no puede superar {LongitudMaximaNombre} caracteres.", nameof(nombre));

        var tipos = tipoDocumentoIds.Distinct().ToList();
        if (tipos.Count == 0)
            throw new ArgumentException("La plantilla debe tener al menos un requisito.", nameof(tipoDocumentoIds));

        var plantilla = new PlantillaRequisitosEvento { ClienteId = clienteId, Nombre = normalizado };
        plantilla._tipos.AddRange(tipos.Select(tipo => new PlantillaRequisitosEventoTipo(plantilla.Id, tipo)));
        return plantilla;
    }
}

/// <summary>Un Tipo de Documento de una <see cref="PlantillaRequisitosEvento"/>.</summary>
public class PlantillaRequisitosEventoTipo : EntidadConTenant
{
    public Guid PlantillaId { get; private set; }
    public Guid TipoDocumentoId { get; private set; }

    private PlantillaRequisitosEventoTipo()
    {
    }

    internal PlantillaRequisitosEventoTipo(Guid plantillaId, Guid tipoDocumentoId)
    {
        if (tipoDocumentoId == Guid.Empty)
            throw new ArgumentException("El requisito debe tener un tipo de documento.", nameof(tipoDocumentoId));

        PlantillaId = plantillaId;
        TipoDocumentoId = tipoDocumentoId;
    }
}
