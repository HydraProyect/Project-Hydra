namespace CaeManager.Application.Tenants.Encargo;

/// <summary>
/// El área de <see cref="ActosExcluidosDelEncargo"/> a la que pertenece una petición, declarada en el propio tipo
/// (ADR-013, paso M0c). Viaja con el tipo: mover la petición de carpeta o de espacio de nombres (por ejemplo a
/// <c>CaeManager.Application.Inbound.*</c>) no cambia su área, y por tanto no cambia si queda excluida del
/// Encargo de administración no pleno.
///
/// <para>
/// La lleva toda petición de <c>src</c>, por obligación de <c>AreaDelEncargoPorTipoTests</c> (cada petición no genérica
/// abierta la lleva directa o la hereda de una base marcada). Una petición sin marca toma el área de su base; sin
/// ninguna, se queda sin área y se excluye (falla cerrado). El espacio de nombres no cuenta, y tampoco hay una tabla
/// central <c>typeof</c>: referenciaba tipos de Outbound desde el Núcleo y rompía el ratchet de M0b. La marca no se
/// hereda sola (<c>Inherited = false</c>): la cadena la recorre <see cref="ActosExcluidosDelEncargo.AreaDe(Type)"/>, y
/// la marca excluida de una base prevalece. La lista congelada de <c>AreaDelEncargoPorTipoTests</c> debe coincidir
/// con la marca de cada petición de <c>src</c>.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class AreaDelEncargoAttribute(string area) : Attribute
{
    public string Area { get; } = area;
}
