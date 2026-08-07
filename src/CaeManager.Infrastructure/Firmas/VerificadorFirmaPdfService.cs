using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using Microsoft.Extensions.Logging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace CaeManager.Infrastructure.Firmas;

/// <summary>
/// Verificación de firmas PAdES/PKCS#7 con la BCL de .NET — SignedCms para
/// el CMS detached, Rfc3161TimestampToken para el sello de tiempo y
/// X509Chain con almacén propio (<see cref="AlmacenConfianzaFirmas"/>) para
/// la confianza. Cero librerías de terceros (PLAN-FIRMA-DIGITAL-PDF.md § 3).
///
/// PDFsharp se usa solo para localizar el diccionario de firma y leer el
/// /ByteRange (enteros, sin ambigüedad — spike de LecturaFirmaPdfSpikeTests);
/// el CMS se extrae de los bytes crudos del hueco entre los dos tramos del
/// rango, para no depender de cómo decodifique PdfString un literal
/// hexadecimal binario.
///
/// Revocación (el riesgo nº1 del plan, § 4): OCSP/CRL en línea con timeout
/// duro; si no se puede comprobar, se degrada a "no disponible" — nunca una
/// excepción, que consumiría un intento de la cola por un problema de red
/// ajeno al documento. La lectura de las respuestas LTV embebidas en el PDF
/// (diccionario DSS) queda para la épica 2: la BCL no permite inyectar una
/// respuesta OCSP externa en X509Chain, así que soportarlo exige un parser
/// propio que hoy no compensa (los sellos de FNMT publican OCSP estable).
/// </summary>
public class VerificadorFirmaPdfService(
    AlmacenConfianzaFirmas almacenConfianza,
    ILogger<VerificadorFirmaPdfService> logger) : IVerificadorFirmaPdfService
{
    private static readonly TimeSpan TimeoutRevocacion = TimeSpan.FromSeconds(5);

    private const string OidSelloDeTiempo = "1.2.840.113549.1.9.16.2.14";
    private const string OidFechaFirmaDeclarada = "1.2.840.113549.1.9.5";
    private const string OidNumeroSerieSubject = "2.5.4.5";
    private const string OidIdentificadorOrganizacion = "2.5.4.97";
    private const string OidUnidadOrganizativa = "2.5.4.11";

    private static readonly HashSet<string> SubFiltersSoportados = new(StringComparer.Ordinal)
    {
        "/adbe.pkcs7.detached",
        "/ETSI.CAdES.detached",
    };

    /// <summary>DNI/NIE de persona física en el serialNumber del subject (con o sin prefijo eIDAS "IDCES-").</summary>
    private static readonly Regex PatronNifPersonaFisica = new(
        @"^(IDCES-)?([XYZ]?\d{7,8}[A-Z])$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>CIF de entidad (los organismos públicos empiezan por Q, S o P).</summary>
    private static readonly Regex PatronCifEntidad = new(
        @"^(VATES-)?([A-HJNP-SUVW]\d{7}[0-9A-J])$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task<Result<ResultadoVerificacionFirmasPdf>> VerificarAsync(
        byte[] contenidoPdf, CancellationToken cancellationToken = default)
    {
        List<FirmaLocalizada> localizadas;
        try
        {
            localizadas = LocalizarFirmas(contenidoPdf);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo leer el PDF para verificar firmas ({Bytes} bytes).", contenidoPdf.Length);
            return Task.FromResult(Result.Fallo<ResultadoVerificacionFirmasPdf>(Error.Crear(
                "VerificadorFirmaPdf.ArchivoInvalido", "No pudimos leer este archivo como PDF.")));
        }

        if (localizadas.Count == 0)
        {
            return Task.FromResult(Result.Exito(
                new ResultadoVerificacionFirmasPdf(
                    NivelConfianzaDocumental.SoloLectura, [], AparentaReimpresion(contenidoPdf))));
        }

        var firmas = localizadas
            .Select((firma, indice) => VerificarFirma(contenidoPdf, firma, indice, cancellationToken))
            .ToList();

        return Task.FromResult(Result.Exito(
            new ResultadoVerificacionFirmasPdf(CalcularNivel(contenidoPdf, localizadas, firmas), firmas)));
    }

    /// <summary>
    /// Agregación de PLAN-FIRMA-DIGITAL-PDF.md § 1: cualquier firma inválida
    /// es manipulación; la confianza se evalúa sobre la firma válida que
    /// cubre el final del archivo — contenido añadido después de la última
    /// firma no está firmado por nadie, así que impide superar
    /// "íntegra pero no confiable".
    /// </summary>
    private static NivelConfianzaDocumental CalcularNivel(
        byte[] pdf, List<FirmaLocalizada> localizadas, List<FirmaPdfVerificada> firmas)
    {
        if (firmas.Any(f => f.Estado == EstadoFirmaPdf.Invalida))
            return NivelConfianzaDocumental.Manipulado;

        if (firmas.All(f => f.Estado == EstadoFirmaPdf.NoSoportada))
            return NivelConfianzaDocumental.SoloLectura;

        var indiceUltima = Enumerable.Range(0, localizadas.Count)
            .Where(i => firmas[i].Estado == EstadoFirmaPdf.Valida)
            .OrderByDescending(i => localizadas[i].ByteRange[2] + localizadas[i].ByteRange[3])
            .First();
        var ultima = firmas[indiceUltima];

        if (!ultima.CubreDocumentoCompleto || !ultima.CadenaConfiable)
            return NivelConfianzaDocumental.FirmaIntegraEmisorNoConfiable;

        return ultima.Revocacion == ComprobacionRevocacion.NoDisponible
            ? NivelConfianzaDocumental.FirmaValidaSinRevocacion
            : NivelConfianzaDocumental.FirmaValida;
    }

    private FirmaPdfVerificada VerificarFirma(
        byte[] pdf, FirmaLocalizada firma, int indice, CancellationToken cancellationToken)
    {
        if (!SubFiltersSoportados.Contains(firma.SubFilter))
        {
            return new FirmaPdfVerificada(
                indice, EstadoFirmaPdf.NoSoportada, CadenaConfiable: false, ComprobacionRevocacion.NoDisponible,
                EmisorCertificado: string.Empty, NumeroSerieCertificado: string.Empty, EsSelloDeOrgano: false,
                FirmanteNombre: null, FirmanteNif: null, FechaFirmaUtc: null, TieneSelloDeTiempo: false,
                CubreDocumentoCompleto: false,
                MotivoInvalidez: $"Formato de firma no soportado ({firma.SubFilter.TrimStart('/')}).");
        }

        SignedCms cms;
        try
        {
            cms = new SignedCms(new ContentInfo(ExtraerRangoFirmado(pdf, firma.ByteRange)), detached: true);
            cms.Decode(firma.ContenidoCms);
            cms.CheckSignature(verifySignatureOnly: true);
        }
        catch (CryptographicException ex)
        {
            logger.LogInformation(
                "Firma {Indice} inválida en verificación criptográfica: {Motivo}", indice, ex.Message);
            return new FirmaPdfVerificada(
                indice, EstadoFirmaPdf.Invalida, CadenaConfiable: false, ComprobacionRevocacion.NoDisponible,
                EmisorCertificado: string.Empty, NumeroSerieCertificado: string.Empty, EsSelloDeOrgano: false,
                FirmanteNombre: null, FirmanteNif: null, FechaFirmaUtc: null, TieneSelloDeTiempo: false,
                CubreDocumentoCompleto: CubreDocumentoCompleto(pdf, firma),
                MotivoInvalidez: "El contenido firmado fue alterado después de la firma, o la firma es incorrecta.");
        }

        var firmante = cms.SignerInfos[0];
        var certificado = firmante.Certificate;
        if (certificado is null)
        {
            return new FirmaPdfVerificada(
                indice, EstadoFirmaPdf.Invalida, CadenaConfiable: false, ComprobacionRevocacion.NoDisponible,
                EmisorCertificado: string.Empty, NumeroSerieCertificado: string.Empty, EsSelloDeOrgano: false,
                FirmanteNombre: null, FirmanteNif: null, FechaFirmaUtc: null, TieneSelloDeTiempo: false,
                CubreDocumentoCompleto: CubreDocumentoCompleto(pdf, firma),
                MotivoInvalidez: "La firma no incluye el certificado del firmante.");
        }

        var (fechaFirmaUtc, tieneSelloDeTiempo) = ExtraerFechaFirma(firmante);
        var (cadenaConfiable, revocacion, motivoCadena) =
            EvaluarCadena(certificado, cms.Certificates, fechaFirmaUtc, cancellationToken);
        var (esSello, nombre, nif) = InterpretarFirmante(certificado);

        return new FirmaPdfVerificada(
            indice,
            EstadoFirmaPdf.Valida,
            cadenaConfiable,
            revocacion,
            certificado.GetNameInfo(X509NameType.SimpleName, forIssuer: true),
            certificado.SerialNumber,
            esSello,
            nombre,
            nif,
            fechaFirmaUtc,
            tieneSelloDeTiempo,
            CubreDocumentoCompleto(pdf, firma),
            motivoCadena);
    }

    /// <summary>
    /// Cadena en dos pasadas: primero con revocación en línea; si el único
    /// problema es no poder consultarla (OCSP/CRL caídos o sin publicar), se
    /// reevalúa sin revocación para distinguir "emisor no confiable" de
    /// "confiable pero sin poder comprobar revocación" — la degradación del
    /// plan § 4. La validez del certificado se evalúa en el momento de la
    /// firma cuando hay sello de tiempo (un certificado hoy caducado pudo
    /// firmar válidamente en su día — PAdES).
    /// </summary>
    private (bool CadenaConfiable, ComprobacionRevocacion Revocacion, string? Motivo) EvaluarCadena(
        X509Certificate2 certificado,
        X509Certificate2Collection certificadosEmbebidos,
        DateTime? fechaFirmaUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Un certificado que no publica dónde comprobar su revocación (sin
        // CRL Distribution Points ni OCSP en Authority Info Access) no puede
        // dar "comprobada" jamás — X509Chain lo dejaría pasar en silencio
        // (p. ej. una raíz autofirmada con ExcludeRoot) y parecería más
        // garantía de la que hay.
        var publicaRevocacion = PublicaPuntosDeRevocacion(certificado);

        using var cadenaEnLinea = CrearCadena(
            certificadosEmbebidos, fechaFirmaUtc,
            publicaRevocacion ? X509RevocationMode.Online : X509RevocationMode.NoCheck);
        if (cadenaEnLinea.Build(certificado))
        {
            return (true,
                publicaRevocacion ? ComprobacionRevocacion.ComprobadaEnLinea : ComprobacionRevocacion.NoDisponible,
                null);
        }

        var estados = cadenaEnLinea.ChainStatus.Select(s => s.Status).ToList();
        var soloFallaRevocacion = estados.All(s =>
            s is X509ChainStatusFlags.RevocationStatusUnknown or X509ChainStatusFlags.OfflineRevocation);

        if (soloFallaRevocacion)
        {
            using var cadenaSinRevocacion = CrearCadena(certificadosEmbebidos, fechaFirmaUtc, X509RevocationMode.NoCheck);
            if (cadenaSinRevocacion.Build(certificado))
                return (true, ComprobacionRevocacion.NoDisponible, null);
            estados = cadenaSinRevocacion.ChainStatus.Select(s => s.Status).ToList();
        }

        var motivo = estados.Contains(X509ChainStatusFlags.Revoked)
            ? "El certificado del firmante está revocado."
            : "La cadena del certificado no llega a ninguna CA del almacén de confianza.";
        logger.LogInformation(
            "Cadena no confiable para el certificado {Serie}: {Estados}",
            certificado.SerialNumber, string.Join(", ", estados));
        return (false, ComprobacionRevocacion.NoDisponible, motivo);
    }

    private X509Chain CrearCadena(
        X509Certificate2Collection certificadosEmbebidos, DateTime? fechaFirmaUtc, X509RevocationMode modo)
    {
        var cadena = new X509Chain();
        cadena.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        cadena.ChainPolicy.CustomTrustStore.AddRange(almacenConfianza.RaicesConfiables);
        cadena.ChainPolicy.ExtraStore.AddRange(certificadosEmbebidos);
        cadena.ChainPolicy.RevocationMode = modo;
        cadena.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        cadena.ChainPolicy.UrlRetrievalTimeout = TimeoutRevocacion;
        if (fechaFirmaUtc is { } fecha)
            cadena.ChainPolicy.VerificationTime = fecha.ToLocalTime();
        return cadena;
    }

    /// <summary>
    /// La fecha certificada sale del sello de tiempo RFC 3161 (atributo no
    /// firmado) verificado contra la propia firma; a falta de sello, la
    /// fecha declarada por el firmante (signingTime) — que vale lo que valga
    /// la honestidad de quien firmó, por eso el flag va aparte.
    /// </summary>
    private static (DateTime? FechaFirmaUtc, bool TieneSelloDeTiempo) ExtraerFechaFirma(SignerInfo firmante)
    {
        foreach (var atributo in firmante.UnsignedAttributes)
        {
            if (atributo.Oid.Value != OidSelloDeTiempo) continue;
            foreach (var valor in atributo.Values)
            {
                if (!Rfc3161TimestampToken.TryDecode(valor.RawData, out var token, out _)) continue;
                if (token.VerifySignatureForSignerInfo(firmante, out _))
                    return (token.TokenInfo.Timestamp.UtcDateTime, true);
            }
        }

        // signingTime como atributo firmado (PAdES) o, en su defecto, no
        // firmado (lo emiten así algunos firmantes, PdfSharpDefaultSigner
        // incluido) — en ambos casos es fecha declarada, no certificada.
        foreach (var atributo in firmante.SignedAttributes.Cast<CryptographicAttributeObject>()
                     .Concat(firmante.UnsignedAttributes.Cast<CryptographicAttributeObject>()))
        {
            if (atributo.Oid.Value != OidFechaFirmaDeclarada) continue;
            foreach (var valor in atributo.Values)
            {
                if (valor is Pkcs9SigningTime declarada)
                    return (declarada.SigningTime.ToUniversalTime(), false);
            }
        }

        return (null, false);
    }

    /// <summary>
    /// Heurística sobre el subject del certificado, calibrable con
    /// certificados reales (plan, PR-6): un sello de órgano/entidad lleva
    /// CIF en el serialNumber u organizationIdentifier (eIDAS "VATES-"), o
    /// una OU que se declara sello; una persona física lleva DNI/NIE
    /// ("IDCES-"). El NIF de persona física NO se descarta aquí — la
    /// restricción RGPD (plan, PR-4) la aplica quien persiste, no quien
    /// verifica.
    /// </summary>
    private static (bool EsSelloDeOrgano, string? Nombre, string? Nif) InterpretarFirmante(X509Certificate2 certificado)
    {
        string? numeroSerie = null, identificadorOrganizacion = null;
        var unidades = new List<string>();
        foreach (var rdn in certificado.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            var oid = rdn.GetSingleElementType().Value;
            var valor = rdn.GetSingleElementValue();
            if (valor is null) continue;
            if (oid == OidNumeroSerieSubject) numeroSerie = valor;
            else if (oid == OidIdentificadorOrganizacion) identificadorOrganizacion = valor;
            else if (oid == OidUnidadOrganizativa) unidades.Add(valor);
        }

        var nombre = certificado.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

        if (numeroSerie is not null && PatronNifPersonaFisica.Match(numeroSerie) is { Success: true } persona)
            return (false, nombre, persona.Groups[2].Value.ToUpperInvariant());

        var candidatoCif = numeroSerie ?? identificadorOrganizacion;
        if (candidatoCif is not null && PatronCifEntidad.Match(candidatoCif) is { Success: true } entidad)
            return (true, nombre, entidad.Groups[2].Value.ToUpperInvariant());

        if (identificadorOrganizacion is not null
            && PatronCifEntidad.Match(identificadorOrganizacion) is { Success: true } entidadPorOrganizacion)
            return (true, nombre, entidadPorOrganizacion.Groups[2].Value.ToUpperInvariant());

        var declaraSello = unidades.Any(u => u.Contains("sello", StringComparison.OrdinalIgnoreCase));
        return (declaraSello, nombre, null);
    }

    private static bool CubreDocumentoCompleto(byte[] pdf, FirmaLocalizada firma) =>
        firma.ByteRange[2] + firma.ByteRange[3] == pdf.Length;

    private const string OidCrlDistributionPoints = "2.5.29.31";
    private const string OidAuthorityInfoAccess = "1.3.6.1.5.5.7.1.1";

    private static bool PublicaPuntosDeRevocacion(X509Certificate2 certificado) =>
        certificado.Extensions.Cast<X509Extension>().Any(e =>
            e.Oid?.Value is OidCrlDistributionPoints or OidAuthorityInfoAccess);

    private static readonly byte[][] ProducersDeImpresion =
    [
        "Microsoft: Print To PDF"u8.ToArray(),
        "Microsoft Print to PDF"u8.ToArray(),
    ];

    /// <summary>
    /// Heurística sobre bytes crudos, solo para PDFs sin firma: un Producer
    /// de imprimir-a-PDF, o ninguna fuente embebida (páginas rasterizadas
    /// puras), delatan una reimpresión/escaneo — el original de la Sede
    /// llega firmado y con capa de texto. Calibrado con muestras reales.
    /// </summary>
    private static bool AparentaReimpresion(byte[] pdf)
    {
        if (ProducersDeImpresion.Any(p => Contiene(pdf, p))) return true;
        return !Contiene(pdf, "/Font"u8.ToArray());
    }

    private static bool Contiene(byte[] contenido, byte[] patron)
    {
        for (var i = 0; i <= contenido.Length - patron.Length; i++)
            if (contenido.AsSpan(i, patron.Length).SequenceEqual(patron)) return true;
        return false;
    }

    private sealed record FirmaLocalizada(int[] ByteRange, byte[] ContenidoCms, string SubFilter);

    /// <summary>
    /// AcroForm → Fields → campos /FT /Sig → /V. El /ByteRange se lee del
    /// modelo de objetos; el /Contents, de los bytes crudos del hueco que el
    /// propio rango define (literal hexadecimal, con relleno de ceros que
    /// SignedCms ignora porque el DER declara su longitud).
    /// </summary>
    private static List<FirmaLocalizada> LocalizarFirmas(byte[] pdf)
    {
        using var flujo = new MemoryStream(pdf);
        using var documento = PdfReader.Open(flujo, PdfDocumentOpenMode.Import);

        var campos = documento.Internals.Catalog.Elements
            .GetDictionary("/AcroForm")?.Elements.GetArray("/Fields");
        if (campos is null) return [];

        var firmas = new List<FirmaLocalizada>();
        foreach (var elemento in campos)
        {
            var campo = elemento is PdfReference referencia ? referencia.Value as PdfDictionary : elemento as PdfDictionary;
            if (campo?.Elements.GetName("/FT") != "/Sig") continue;

            var valorFirma = campo.Elements.GetDictionary("/V");
            var byteRangeArray = valorFirma?.Elements.GetArray("/ByteRange");
            if (valorFirma is null || byteRangeArray is null) continue;

            var byteRange = byteRangeArray.Elements.Items
                .Select(item => ((PdfInteger)item).Value)
                .ToArray();
            if (byteRange.Length != 4) continue;

            firmas.Add(new FirmaLocalizada(
                byteRange,
                ExtraerContenidoCmsDelHueco(pdf, byteRange),
                valorFirma.Elements.GetName("/SubFilter")));
        }

        return firmas;
    }

    private static byte[] ExtraerContenidoCmsDelHueco(byte[] pdf, int[] byteRange)
    {
        var inicioHueco = byteRange[0] + byteRange[1];
        var hueco = Encoding.ASCII.GetString(pdf, inicioHueco, byteRange[2] - inicioHueco).Trim();
        if (!hueco.StartsWith('<') || !hueco.EndsWith('>'))
            throw new InvalidOperationException("El /Contents de la firma no es un literal hexadecimal.");
        return Convert.FromHexString(hueco[1..^1]);
    }

    private static byte[] ExtraerRangoFirmado(byte[] pdf, int[] byteRange)
    {
        var rango = new byte[byteRange[1] + byteRange[3]];
        Array.Copy(pdf, byteRange[0], rango, 0, byteRange[1]);
        Array.Copy(pdf, byteRange[2], rango, byteRange[1], byteRange[3]);
        return rango;
    }
}
