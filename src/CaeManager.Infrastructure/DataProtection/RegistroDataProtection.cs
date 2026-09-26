using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using System.Xml.Linq;
using Amazon;
using Amazon.KeyManagementService;
using Amazon.S3;
using CaeManager.Infrastructure.Configuracion;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CaeManager.Infrastructure.DataProtection;

/// <summary>
/// Registro de ASP.NET Core Data Protection: dónde vive el llavero, con qué se
/// cifra en reposo y qué pasa si no se cifra. Separado de
/// <c>AddInfrastructure</c> para que las pruebas ejerciten exactamente el mismo
/// registro que el arranque, no una copia.
/// </summary>
public static class RegistroDataProtection
{
    /// <summary>
    /// Declaración explícita de que se acepta, en Production, un llavero con las
    /// claves en claro. Existe solo para la transición de P1-F3: el despliegue
    /// que introduce el cifrado llega antes que el certificado al servidor. Con
    /// el certificado (o KMS) configurado se ignora — se cifra igual.
    /// </summary>
    public const string ClavePermitirClavesSinCifrar = "DataProtection:PermitirClavesSinCifrar";

    public static IServiceCollection AgregarDataProtectionDeCaeManager(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment entorno)
    {
        // Sin persistir las claves, cada reinicio del proceso genera unas nuevas
        // y todo lo cifrado con las anteriores (credenciales de Empresa/Centro,
        // Fase 0/20) deja de poder descifrarse — silenciosamente, hasta que
        // alguien intenta abrir una credencial guardada. Ruta configurable para
        // apuntar a un volumen persistente en despliegues en contenedor (ver
        // Project-Hydra-Negocio/tecnico/DEPLOY.md); en desarrollo local, relativa al content root como el
        // resto de rutas de almacenamiento de la app.
        var rutaClavesDataProtection = configuration["DataProtection:RutaClaves"] ?? "App_Data/dataprotection-keys";
        var rutaClavesAbsoluta = Path.IsPathRooted(rutaClavesDataProtection)
            ? rutaClavesDataProtection
            : Path.Combine(entorno.ContentRootPath, rutaClavesDataProtection);

        var constructorDataProtection = services.AddDataProtection()
            .SetApplicationName("CaeManager")
            .PersistKeysToFileSystem(new DirectoryInfo(rutaClavesAbsoluta));

        // Cifrado en reposo de esas claves con AWS KMS. Ver
        // DataProtectionKmsOptions: sin esto, las claves viajan en claro en el
        // mismo backup que la base de datos que protegen.
        var opcionesKms = new DataProtectionKmsOptions();
        configuration.GetSection(DataProtectionKmsOptions.SeccionConfiguracion).Bind(opcionesKms);
        services.Configure<DataProtectionKmsOptions>(
            configuration.GetSection(DataProtectionKmsOptions.SeccionConfiguracion));

        services.AvisarSiConfiguracionAMedias(DataProtectionKmsOptions.SeccionConfiguracion, opcionesKms,
            "El cifrado de las claves de Data Protection con KMS NO se ha registrado: las claves se guardan SIN CIFRAR.");

        // Cifrado en reposo con un certificado montado en el contenedor (P1-F3).
        // Alternativa a KMS para el despliegue actual en un VPS sin AWS.
        var opcionesCertificado = new DataProtectionCertificadoOptions();
        configuration.GetSection(DataProtectionCertificadoOptions.SeccionConfiguracion).Bind(opcionesCertificado);

        services.AvisarSiConfiguracionAMedias(DataProtectionCertificadoOptions.SeccionConfiguracion, opcionesCertificado,
            "El cifrado de las claves de Data Protection con certificado NO se ha registrado: las claves se guardan SIN CIFRAR.");

        if (opcionesKms.EstaConfigurado && opcionesCertificado.EstaConfigurado)
            throw new InvalidOperationException(
                $"{DataProtectionKmsOptions.SeccionConfiguracion} y {DataProtectionCertificadoOptions.SeccionConfiguracion} " +
                "están configurados a la vez. Solo puede haber un cifrador del llavero de Data Protection: el último " +
                "registrado ganaría en silencio. Deja uno de los dos.");

        // Un certificado a medias (una ruta sin la otra, o solo Anteriores) no
        // puede caer en la rama «sin cifrar»: con la bandera de transición
        // puesta, una errata en el .env escribiría claves en claro creyendo que
        // las cifra. Se niega haya bandera o no.
        if (opcionesCertificado.AlgoInformado && !opcionesCertificado.EstaConfigurado)
            throw new InvalidOperationException(
                $"{DataProtectionCertificadoOptions.SeccionConfiguracion} está a medias: hacen falta CertificadoRuta y " +
                "ClavePrivadaRuta del certificado vigente (Anteriores solo descifra y no sustituye al vigente).");

        X509Certificate2[] certificadosConfigurados = [];

        if (opcionesKms.EstaConfigurado)
        {
            services.AddSingleton<IAmazonKeyManagementService>(_ => new AmazonKeyManagementServiceClient(
                opcionesKms.AccessKeyId, opcionesKms.SecretAccessKey, RegionEndpoint.GetBySystemName(opcionesKms.Region)));

            constructorDataProtection.Services.Configure<KeyManagementOptions>(opciones =>
                opciones.XmlEncryptor = new KmsXmlEncryptor(
                    new AmazonKeyManagementServiceClient(
                        opcionesKms.AccessKeyId, opcionesKms.SecretAccessKey, RegionEndpoint.GetBySystemName(opcionesKms.Region)),
                    opcionesKms.KeyId!));

            // Deja dicho en el arranque si el cifrado está realmente operativo:
            // una credencial mal copiada no se notaría hasta la siguiente
            // rotación de clave o al abrir una credencial guardada.
            services.AddHostedService<VerificacionKmsHostedService>();
        }
        else if (opcionesCertificado.EstaConfigurado)
        {
            certificadosConfigurados = ProtegerConCertificado(constructorDataProtection, opcionesCertificado);
        }
        else
        {
            // Fail-closed en Production: un llavero en claro no se ve en el día a
            // día —todo funciona— y sí se ve el día que alguien lee un backup.
            // Mismo criterio que ResolverCadenaDeTrafico con la identidad de RLS:
            // un arranque que falla se arregla en minutos; una protección apagada
            // en silencio no se ve hasta que hace daño.
            if (entorno.IsProduction())
            {
                if (!configuration.GetValue(ClavePermitirClavesSinCifrar, defaultValue: false))
                    throw new InvalidOperationException(
                        "Las claves de Data Protection se guardarían SIN CIFRAR en Production: no hay " +
                        $"{DataProtectionCertificadoOptions.SeccionConfiguracion} (CertificadoRuta y ClavePrivadaRuta) " +
                        $"ni {DataProtectionKmsOptions.SeccionConfiguracion} configurados. Monta el certificado PEM en " +
                        "/run/secretos e informa sus rutas, o —solo durante la transición— declara " +
                        $"{ClavePermitirClavesSinCifrar}=true.");

                // Con la bandera, cada arranque lo avisa por log y por alerta
                // operativa: la transición no puede quedarse en silencio.
                services.AddHostedService<AvisoLlaveroSinCifrarHostedService>();
            }

            // Ruidoso a propósito: un despliegue que cree estar cifrando y no
            // lo esté es peor que uno que sepa que no lo está. Se registra al
            // construir el contenedor, así que sale en el arranque.
            Console.WriteLine(
                "[AVISO] Ni DataProtection:Certificado ni DataProtection:Kms están configurados — las claves de Data Protection se guardan SIN CIFRAR. " +
                "El backup (scripts/backup-borg.sh) las incluye junto a la base de datos que protegen, así que viajan en claro también ahí (ver Project-Hydra-Negocio/tecnico/RUNBOOK-CLAVES.md).");
        }

        // Llavero compartido entre réplicas (P3-30 de Project-Hydra-Negocio/MATURITY_REVIEW.md):
        // reemplaza el XmlRepository de disco local configurado arriba por uno
        // en S3 — mismo patrón que el XmlEncryptor de KMS, la última
        // Configure<KeyManagementOptions> que se registra es la que gana.
        // Apagado por defecto: sin AWS provisionado, sigue en disco local
        // (correcto para una sola réplica, ver PersistKeysToFileSystem arriba).
        var opcionesDataProtectionS3 = new DataProtectionS3Options();
        configuration.GetSection(DataProtectionS3Options.SeccionConfiguracion).Bind(opcionesDataProtectionS3);
        services.Configure<DataProtectionS3Options>(
            configuration.GetSection(DataProtectionS3Options.SeccionConfiguracion));

        services.AvisarSiConfiguracionAMedias(DataProtectionS3Options.SeccionConfiguracion, opcionesDataProtectionS3,
            "El llavero de Data Protection NO se ha movido a S3 y sigue en el disco local de cada réplica: con más de una réplica, " +
            "una cookie o una credencial cifrada por una no la puede descifrar otra.");

        if (opcionesDataProtectionS3.EstaConfigurado)
        {
            constructorDataProtection.Services.Configure<KeyManagementOptions>(opciones =>
                opciones.XmlRepository = new S3XmlRepository(
                    new AmazonS3Client(
                        opcionesDataProtectionS3.AccessKeyId, opcionesDataProtectionS3.SecretAccessKey,
                        RegionEndpoint.GetBySystemName(opcionesDataProtectionS3.Region)),
                    opcionesDataProtectionS3));

            services.AddHostedService<VerificacionDataProtectionS3HostedService>();
        }
        else
        {
            ComprobarClavesCifradasConCertificadoLegibles(rutaClavesAbsoluta, certificadosConfigurados);
        }

        return services;
    }

    /// <summary>
    /// Las claves NUEVAS se cifran con el certificado vigente; las ya escritas
    /// se descifran con el vigente o con cualquiera de los anteriores. Las
    /// claves que se escribieron en claro antes de configurar el certificado se
    /// siguen leyendo sin más: Data Protection solo descifra el elemento que
    /// lleva <c>encryptedSecret</c>, así que activar el cifrado no invalida
    /// sesiones ni secretos existentes. Tampoco los cifra: siguen en claro en
    /// el disco hasta que se retiren (ver el runbook de Data Protection).
    /// </summary>
    private static X509Certificate2[] ProtegerConCertificado(
        IDataProtectionBuilder constructor, DataProtectionCertificadoOptions opciones)
    {
        var vigente = CargarCertificado(
            opciones.CertificadoRuta!, opciones.ClavePrivadaRuta!, DataProtectionCertificadoOptions.SeccionConfiguracion);

        var anteriores = opciones.Anteriores
            .Select((anterior, indice) => CargarCertificado(
                anterior.CertificadoRuta ?? "", anterior.ClavePrivadaRuta ?? "",
                $"{DataProtectionCertificadoOptions.SeccionConfiguracion}:Anteriores:{indice}"))
            .ToArray();

        constructor.ProtectKeysWithCertificate(vigente);
        // El vigente también va aquí: descifrar no puede depender de que la
        // sobrecarga de arriba lo añada por su cuenta, ni del almacén de
        // certificados del sistema, que en el contenedor está vacío.
        constructor.UnprotectKeysWithAnyCertificate([vigente, .. anteriores]);

        Console.WriteLine(
            $"[INFO] Claves de Data Protection cifradas en reposo con el certificado {vigente.Thumbprint} " +
            $"(caduca {vigente.NotAfter:yyyy-MM-dd}; {anteriores.Length} anterior(es) solo para descifrar).");

        return [vigente, .. anteriores];
    }

    /// <summary>
    /// Si el llavero ya tiene claves cifradas con certificado, alguno de los
    /// configurados tiene que ser el suyo. Sin esto, arrancar con otro
    /// certificado (el de staging en una restauración de producción) o sin
    /// ninguno (rutas borradas del .env con la bandera de transición puesta)
    /// sale verde: Data Protection da esa clave por inutilizable, genera otra en
    /// silencio y todo lo protegido con la anterior —cookies, antiforgery,
    /// credenciales de portal— deja de leerse. Se niega aunque haya bandera.
    ///
    /// Solo mira el llavero en disco: con el llavero en S3 no hay ficheros que
    /// leer aquí. Las claves cifradas con KMS no se miran (las verifica
    /// <see cref="VerificacionKmsHostedService"/>). El certificado de cada clave
    /// sale del propio XML: EncryptedXml escribe el certificado público
    /// (KeyInfo/X509Data/X509Certificate) junto al texto cifrado.
    /// </summary>
    internal static void ComprobarClavesCifradasConCertificadoLegibles(
        string rutaLlavero, IReadOnlyCollection<X509Certificate2> certificados)
    {
        if (!Directory.Exists(rutaLlavero))
            return;

        var huellasDisponibles = certificados
            .Select(c => c.Thumbprint)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ilegibles = new List<string>();

        foreach (var fichero in Directory.EnumerateFiles(rutaLlavero, "*.xml"))
        {
            XDocument documento;
            try
            {
                documento = XDocument.Load(fichero);
            }
            catch (XmlException)
            {
                continue; // Data Protection tampoco la leería; no es asunto de esta comprobación.
            }

            // Data Protection escribe el elemento en su propio espacio de nombres XML.
            foreach (var secreto in documento.Descendants().Where(e => e.Name.LocalName == "encryptedSecret"))
            {
                var tipo = (string?)secreto.Attribute("decryptorType") ?? "";
                if (!tipo.Contains("EncryptedXmlDecryptor", StringComparison.Ordinal))
                    continue;

                var huellas = secreto.Descendants()
                    .Where(e => e.Name.LocalName == "X509Certificate")
                    .Select(e => HuellaDe(e.Value))
                    .ToList();

                if (!huellas.Any(h => h is not null && huellasDisponibles.Contains(h)))
                    ilegibles.Add($"{Path.GetFileName(fichero)} (certificado {string.Join("/", huellas.Select(h => h ?? "ilegible"))})");
            }
        }

        if (ilegibles.Count > 0)
            throw new InvalidOperationException(
                "El llavero de Data Protection tiene claves cifradas con un certificado que no está configurado: " +
                $"{string.Join(", ", ilegibles)}. Sin él no se descifran las cookies ni los secretos protegidos con " +
                $"esas claves. Configura ese certificado en {DataProtectionCertificadoOptions.SeccionConfiguracion} " +
                "(como vigente o en Anteriores); la bandera de transición no lo sustituye.");
    }

    private static string? HuellaDe(string certificadoBase64)
    {
        try
        {
            using var certificado = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certificadoBase64.Trim()));
            return certificado.Thumbprint;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Falla en el arranque si el certificado configurado no se puede usar: un
    /// certificado ilegible que degradara a «sin cifrar» escribiría la próxima
    /// clave en claro creyendo que la cifra, y uno que no descifra deja la app
    /// sin poder leer ninguna cookie ni secreto. Nombra la opción, nunca vuelca
    /// el contenido.
    /// </summary>
    internal static X509Certificate2 CargarCertificado(string certificadoRuta, string clavePrivadaRuta, string opcion)
    {
        if (string.IsNullOrWhiteSpace(certificadoRuta) || string.IsNullOrWhiteSpace(clavePrivadaRuta))
            throw new InvalidOperationException(
                $"{opcion}: faltan CertificadoRuta o ClavePrivadaRuta.");

        X509Certificate2 certificado;
        try
        {
            certificado = X509Certificate2.CreateFromPemFile(certificadoRuta, clavePrivadaRuta);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"{opcion}: no se puede leer el certificado ({certificadoRuta}) o su clave privada ({clavePrivadaRuta}) " +
                $"— {ex.GetType().Name}. Comprueba que existen y que el usuario de la app (APP_UID 1654) puede leerlos.", ex);
        }

        // EncryptedXml (lo que usa ProtectKeysWithCertificate) solo cifra con RSA.
        using var rsa = certificado.GetRSAPublicKey();
        if (rsa is null || !certificado.HasPrivateKey)
            throw new InvalidOperationException(
                $"{opcion}: el certificado ({certificadoRuta}) tiene que ser RSA y venir con su clave privada.");

        return certificado;
    }
}
