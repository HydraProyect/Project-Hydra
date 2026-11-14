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
/// que ADR-013 prevé mover. Una petición sin la marca sigue tomando el área de su espacio de nombres; si ese
/// segmento no es un área clasificada, se queda sin área y se excluye (falla cerrado).
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class AreaDelEncargoAttribute(string area) : Attribute
{
    public string Area { get; } = area;
}
