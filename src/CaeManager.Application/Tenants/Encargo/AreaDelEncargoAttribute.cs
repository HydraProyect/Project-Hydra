namespace CaeManager.Application.Tenants.Encargo;

/// <summary>
/// El área de <see cref="ActosExcluidosDelEncargo"/> a la que pertenece una petición, declarada en el propio tipo
/// (ADR-013, paso M0c). Viaja con el tipo: mover la petición de carpeta o de espacio de nombres (por ejemplo a
/// <c>CaeManager.Application.Inbound.*</c>) no cambia su área, y por tanto no cambia si queda excluida del
/// Encargo de administración no pleno.
///
/// <para>
/// La llevan, por obligación de <c>AreaDelEncargoPorTipoTests</c>, las peticiones de las áreas excluidas enteras y
/// las de los módulos Inbound y Facturación del registro de módulos: las que más cuesta abrir por accidente y las
/// que ADR-013 prevé mover. Una petición sin la marca toma el área de su entrada en
/// <see cref="ActosExcluidosDelEncargo.AreasDePeticionesSinMarca"/> y, si no la tiene, la de su base; sin ninguna,
/// se queda sin área y se excluye (falla cerrado). El espacio de nombres no cuenta. La marca no se hereda sola
/// (<c>Inherited = false</c>): la cadena la recorre <see cref="ActosExcluidosDelEncargo.AreaDe(Type)"/>, y la marca
/// excluida de una base prevalece. La lista congelada de <c>AreaDelEncargoPorTipoTests</c> debe coincidir con la
/// marca o la entrada de cada petición de <c>src</c>.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class AreaDelEncargoAttribute(string area) : Attribute
{
    public string Area { get; } = area;
}
