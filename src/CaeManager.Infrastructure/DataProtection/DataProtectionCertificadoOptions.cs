using CaeManager.Infrastructure.Configuracion;

namespace CaeManager.Infrastructure.DataProtection;

/// <summary>
/// Cifrado en reposo de las claves de Data Protection con un certificado X.509
/// montado en el contenedor (P1-F3 del plan de madurez).
///
/// Las claves del llavero protegen las cookies de autenticación, los tokens
/// antiforgery y los secretos que la aplicación persiste cifrados (credenciales
/// de portales externos, entre otros). Sin cifrado en reposo viven en claro en
/// el volumen y viajan en claro dentro del mismo backup que la base de datos
/// que protegen (scripts/backup-borg.sh copia ambos al mismo archivo). Con el
/// certificado, el material de cada clave nueva se escribe cifrado con su clave
/// pública, y solo quien tenga la clave privada —que no está en el volumen ni
/// en el backup, sino en /run/secretos del host— puede leerlo.
///
/// Alternativa a <see cref="DataProtectionKmsOptions"/>, no complemento: las dos
/// a la vez son una configuración ambigua y el arranque se niega (ver
/// <see cref="RegistroDataProtection"/>).
///
/// Formato PEM, igual que el certificado del conector de Microsoft 365, para que
/// la operación en el servidor sea la misma: un certificado y su clave privada
/// RSA en dos ficheros. Sin interruptor <c>Activo</c>: informar las dos rutas es
/// la decisión, y una sola informada se avisa como configuración a medias.
/// </summary>
public class DataProtectionCertificadoOptions : IOpcionesConGate
{
    public const string SeccionConfiguracion = "DataProtection:Certificado";

    /// <summary>Certificado vigente (PEM). Cifra las claves nuevas y descifra las que cifró.</summary>
    public string? CertificadoRuta { get; set; }

    /// <summary>Clave privada RSA del certificado vigente (PEM, sin contraseña).</summary>
    public string? ClavePrivadaRuta { get; set; }

    /// <summary>
    /// Certificados retirados que todavía hacen falta para DESCIFRAR claves del
    /// llavero escritas con ellos. Rotar es: el nuevo pasa a
    /// <see cref="CertificadoRuta"/>/<see cref="ClavePrivadaRuta"/> y el anterior
    /// entra aquí. Solo se puede quitar de esta lista cuando ninguna clave del
    /// llavero que siga haciendo falta esté cifrada con él.
    /// </summary>
    public List<CertificadoAnteriorDataProtection> Anteriores { get; set; } = [];

    public bool EstaConfigurado => Evaluar().Completo;

    public IReadOnlyList<string> ProblemasDeConfiguracion() => Evaluar().Problemas;

    private EvaluacionGate Evaluar() => GateDeConfiguracion.Evaluar(null,
        (nameof(CertificadoRuta), CertificadoRuta), (nameof(ClavePrivadaRuta), ClavePrivadaRuta));
}

/// <summary>Un certificado retirado de <see cref="DataProtectionCertificadoOptions.Anteriores"/>.</summary>
public class CertificadoAnteriorDataProtection
{
    public string? CertificadoRuta { get; set; }

    public string? ClavePrivadaRuta { get; set; }
}
