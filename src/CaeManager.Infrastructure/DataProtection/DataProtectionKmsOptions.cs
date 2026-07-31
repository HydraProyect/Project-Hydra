namespace CaeManager.Infrastructure.DataProtection;

/// <summary>
/// Cifrado en reposo de las claves de Data Protection con AWS KMS.
///
/// Sin esto, las claves viven en claro en el volumen y —lo que de verdad
/// importa— viajan en claro dentro del mismo backup que la base de datos
/// (RUNBOOK-CLAVES.md: BD y claves se suben siempre juntas a S3). Ese backup
/// contiene además las credenciales de portales externos de Empresas,
/// Subcontratas y Centros, cifradas justamente con esas claves: quien
/// consiguiera el archivo tenía a la vez el candado y la llave.
///
/// Con KMS, la clave maestra nunca sale de AWS. El backup deja de ser
/// suficiente por sí solo para descifrar nada.
///
/// Apagado por defecto, mismo patrón que <c>Backups:Activo</c> y
/// <c>DatosPrueba:Activo</c>: sin cuenta de AWS provisionada la aplicación
/// tiene que arrancar igual. Pero el arranque lo advierte por log — un
/// despliegue que cree estar cifrando y no lo esté es peor que uno que sepa
/// que no lo está.
/// </summary>
public class DataProtectionKmsOptions
{
    public const string SeccionConfiguracion = "DataProtection:Kms";

    public bool Activo { get; set; }

    /// <summary>
    /// ARN o alias de la clave KMS (p. ej. <c>alias/caemanager-dataprotection</c>).
    /// Debe vivir en la misma región que el bucket de backups para no sacar
    /// datos de España — ver RGPD-TRATAMIENTO-DATOS.md § 6.
    /// </summary>
    public string? KeyId { get; set; }

    public string? AccessKeyId { get; set; }

    public string? SecretAccessKey { get; set; }

    public string? Region { get; set; }

    public bool EstaConfigurado =>
        Activo &&
        !string.IsNullOrWhiteSpace(KeyId) &&
        !string.IsNullOrWhiteSpace(AccessKeyId) &&
        !string.IsNullOrWhiteSpace(SecretAccessKey) &&
        !string.IsNullOrWhiteSpace(Region);
}
