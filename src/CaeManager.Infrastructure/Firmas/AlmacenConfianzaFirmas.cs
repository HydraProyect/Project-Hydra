using System.Security.Cryptography.X509Certificates;

namespace CaeManager.Infrastructure.Firmas;

/// <summary>
/// Almacén de confianza propio para la verificación de firmas de PDF —
/// nunca el del sistema operativo: la pregunta no es "¿es un certificado
/// válido de internet?" sino "¿lo emitió una CA de la Administración
/// española?" (PLAN-FIRMA-DIGITAL-PDF.md § 3, opción B).
///
/// Las CA van fijadas (pinned) en código como PEM — texto, no binarios,
/// conservando la convención del repo. Mantenimiento: las raíces FNMT
/// rotan en ciclos de ~10-15 años; si FNMT estrena una CA, los documentos
/// firmados con ella caen a "emisor no reconocido" (revisión humana, nunca
/// un falso auto-validado) y el arreglo es añadir aquí el PEM nuevo,
/// descargado de la web pública de FNMT. El job TSL/LOTL completo es de la
/// épica 2.
///
/// La cadena real de los sellos de TGSS/AEAT se confirma en la calibración
/// con PDFs reales (plan, PR-6): los PDFs firmados embeben sus intermedias,
/// así que con la raíz anclada X509Chain puede construir la cadena entera.
/// </summary>
public class AlmacenConfianzaFirmas
{
    public X509Certificate2Collection RaicesConfiables { get; }

    public AlmacenConfianzaFirmas(X509Certificate2Collection raicesConfiables)
    {
        RaicesConfiables = raicesConfiables;
    }

    public static AlmacenConfianzaFirmas AdministracionEspanola()
    {
        var raices = new X509Certificate2Collection();
        raices.Add(X509CertificateLoader.LoadCertificate(System.Text.Encoding.ASCII.GetBytes(RaizFnmtRcmPem)));
        return new AlmacenConfianzaFirmas(raices);
    }

    /// <summary>
    /// "AC RAIZ FNMT-RCM" (C=ES, O=FNMT-RCM), válida hasta 2030-01-01.
    /// Huella SHA-256: EBC5570C29018C4D67B1AA127BAF12F703B4611EBC17B7DAB5573894179B93FA.
    /// Es la raíz de "AC Administración Pública" y "AC Sector Público",
    /// las intermedias que emiten los sellos de órgano de TGSS y AEAT.
    /// </summary>
    private const string RaizFnmtRcmPem = """
        -----BEGIN CERTIFICATE-----
        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
        uu8wd+RU4riEmViAqhOLUTpPSPaLtrM=
        -----END CERTIFICATE-----
        """;
}
