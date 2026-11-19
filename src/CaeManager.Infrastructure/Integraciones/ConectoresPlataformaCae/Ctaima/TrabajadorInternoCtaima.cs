using System.Text.Json.Serialization;

namespace CaeManager.Infrastructure.Integraciones.ConectoresPlataformaCae.Ctaima;

/// <summary>
/// Cuerpo de <c>POST resource-management/api/internal-employees</c>: el alta de un Trabajador en la
/// gestión interna del contratista identificado por la cabecera <c>tenantId</c>. Solo los campos que
/// TALVEG rellena; el resto del esquema del fabricante se omite.
/// </summary>
public sealed record TrabajadorInternoCtaimaPeticion(
    [property: JsonPropertyName("identityCardNumber")] string IdentityCardNumber,
    [property: JsonPropertyName("identityCardType")] string IdentityCardType,
    [property: JsonPropertyName("firstName")] string FirstName,
    [property: JsonPropertyName("firstSurname")] string FirstSurname,
    [property: JsonPropertyName("secondSurname")] string? SecondSurname,
    [property: JsonPropertyName("enabled")] bool Enabled)
{
    /// <summary>Los datos del Trabajador no se escriben en un log (OWASP ASVS 4.0.3 V7.1.2).</summary>
    public override string ToString() => nameof(TrabajadorInternoCtaimaPeticion);
}

/// <summary>Lo único que TALVEG lee de la respuesta 201: el identificador del Trabajador en la plataforma.</summary>
public sealed record TrabajadorInternoCtaimaRespuesta(
    [property: JsonPropertyName("guid")] string? Guid,
    [property: JsonPropertyName("employeeId")] int EmployeeId);
