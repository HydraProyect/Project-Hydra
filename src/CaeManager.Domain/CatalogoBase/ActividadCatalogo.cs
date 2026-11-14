using CaeManager.Domain.Common;

namespace CaeManager.Domain.CatalogoBase;

/// <summary>
/// Una Actividad del catálogo base de Plataforma (CA-2): el oficio o trabajo que una Empresa contratista hace en un
/// ContratoAcceso, propio de TALVEG y ligado a varios códigos oficiales (<see cref="CodigosOficiales"/>), porque la
/// CNAE describe sectores y no oficios.
///
/// <para>
/// <b>Catálogo global, sin Tenant</b>: extiende <see cref="Entity"/> y no <see cref="EntidadConTenant"/>, como
/// <c>FestivoOficial</c>. Es plano de Plataforma; cada Tenant TCE lo adopta y ajusta en su plantilla (CA-3), que es
/// dato suyo y vive en otra tabla. La base deja a <c>cae_app_runtime</c> solo con SELECT; quién lo mantiene (un
/// Actor de Plataforma TALVEG) llega en otro incremento, y su contenido solo se carga tras la revisión de un técnico
/// habilitado (CA-7).
/// </para>
///
/// <para>
/// Plano a propósito: el catálogo no tiene jerarquía decidida. Añadir una Actividad padre después es una columna
/// nullable, aditiva mientras el catálogo esté vacío.
/// </para>
///
/// <para>
/// El ciclo de vida se modela con <see cref="Activa"/>, no con borrado: una Actividad retirada sigue existiendo para
/// no romper las plantillas y los contratos que la citan.
/// </para>
/// </summary>
public sealed class ActividadCatalogo : Entity
{
    private readonly List<CodigoOficialActividad> _codigosOficiales = [];

    /// <summary>Slug estable (<see cref="ReglasCatalogoBase.PatronCodigo"/>), único en el catálogo.</summary>
    public string Codigo { get; private set; } = string.Empty;

    public string Nombre { get; private set; } = string.Empty;

    public bool Activa { get; private set; } = true;

    public IReadOnlyCollection<CodigoOficialActividad> CodigosOficiales => _codigosOficiales.AsReadOnly();

    private ActividadCatalogo()
    {
    }

    public ActividadCatalogo(string codigo, string nombre)
    {
        Codigo = ReglasCatalogoBase.ValidarCodigo(codigo, nameof(codigo));
        Nombre = ReglasCatalogoBase.ValidarTexto(nombre, ReglasCatalogoBase.LongitudMaximaNombre, nameof(nombre));
    }

    /// <summary>
    /// Liga la Actividad a un código oficial. Una Actividad puede tener varios códigos del mismo sistema (un oficio
    /// cabe en varias clases de la CNAE), pero no el mismo dos veces.
    /// </summary>
    public CodigoOficialActividad LigarCodigoOficial(SistemaCodigoOficial sistema, string codigo)
    {
        var nuevo = new CodigoOficialActividad(Id, sistema, codigo);
        if (_codigosOficiales.Any(c => c.Sistema == nuevo.Sistema && c.Codigo == nuevo.Codigo))
            throw new InvalidOperationException($"La Actividad ya está ligada al código {nuevo.Sistema} {nuevo.Codigo}.");
        _codigosOficiales.Add(nuevo);
        return nuevo;
    }

    public void Retirar() => Activa = false;

    public void Reactivar() => Activa = true;
}

/// <summary>
/// Un código oficial (<see cref="SistemaCodigoOficial"/>) al que se liga una <see cref="ActividadCatalogo"/>. Es parte
/// del catálogo base: global, sin Tenant y de solo lectura para la aplicación.
/// </summary>
public sealed class CodigoOficialActividad : Entity
{
    public Guid ActividadId { get; private set; }

    public SistemaCodigoOficial Sistema { get; private set; }

    public string Codigo { get; private set; } = string.Empty;

    private CodigoOficialActividad()
    {
    }

    internal CodigoOficialActividad(Guid actividadId, SistemaCodigoOficial sistema, string codigo)
    {
        ActividadId = actividadId;
        Sistema = ReglasCatalogoBase.ValidarEnum(sistema, nameof(sistema));
        Codigo = ReglasCatalogoBase.ValidarCodigoOficial(codigo, nameof(codigo));
    }
}
