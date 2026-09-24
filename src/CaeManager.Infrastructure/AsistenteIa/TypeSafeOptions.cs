namespace CaeManager.Infrastructure.AsistenteIa;

/// <summary>
/// Configuración de <see cref="TypeSafeDecisionesCerradasService"/>. Inerte por
/// defecto, y con dos llaves en vez de una: sin <see cref="Activo"/> a true el
/// adaptador no llama aunque haya <see cref="ApiKey"/>.
/// <para>
/// La segunda llave existe porque tener la clave no basta para poder usarla:
/// enviar órdenes de un Tenant a este proveedor exige un DPA firmado y
/// registrarlo como subencargado, y esa decisión del propietario no está
/// tomada. Una clave puesta para medir no debe bastar para que el adaptador
/// empiece a enviar datos reales.
/// </para>
/// <para>
/// El modelo no es configurable: está fijado en
/// <see cref="TypeSafeDecisionesCerradasService.Modelo"/>.
/// </para>
/// </summary>
public class TypeSafeOptions
{
    public const string SeccionConfiguracion = "TypeSafe";

    public bool Activo { get; set; }

    public string? ApiKey { get; set; }
}
