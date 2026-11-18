using System.Security.Cryptography;
using CaeManager.Application.Inbound.CredencialesAcceso;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Inbound;

/// <summary>Opciones de la firma de la credencial de acceso (sección <c>Inbound:CredencialAcceso</c>).</summary>
public sealed class OpcionesFirmaCredencialAcceso
{
    public const string Seccion = "Inbound:CredencialAcceso";

    /// <summary>
    /// <b>Valor PROVISIONAL</b> (D-8 deja el tope pendiente de decisión). Cada Centro añade ~41 caracteres al QR; con 8
    /// el token ronda los 500 y sigue leyéndose bien con un lector de mano. Por encima, un token por Centro.
    /// </summary>
    public int MaxCentrosPorToken { get; set; } = 8;

    /// <summary>Techo del tope: 40 Centros (~1.800 caracteres) caben con holgura en los 4.096 que acepta la verificación.</summary>
    public const int TopeMaximoAbsoluto = 40;
}

/// <summary>
/// ECDSA P-256 (SHA-256, firma r‖s) sobre el cuerpo del token. Ver <see cref="IFirmanteCredencialAcceso"/> para el
/// formato y el contrato de fallo cerrado.
/// </summary>
public sealed class FirmanteCredencialAcceso(
    LlaveroCredencialAcceso llavero, IOptions<OpcionesFirmaCredencialAcceso> opciones, TimeProvider? reloj = null)
    : IFirmanteCredencialAcceso, IAdministradorLlaveroCredencialAcceso
{
    public IReadOnlyList<string> Firmar(CredencialParaFirmar credencial)
    {
        ArgumentNullException.ThrowIfNull(credencial);
        if (credencial.CredencialId == Guid.Empty || credencial.Serie < 1)
            throw new ArgumentException("Credencial o serie no válidas.", nameof(credencial));
        if (credencial.Centros is null || credencial.Centros.Count == 0
            || credencial.Centros.Any(c => c.CentroId == Guid.Empty)
            || credencial.Centros.Select(c => c.CentroId).Distinct().Count() != credencial.Centros.Count)
            throw new ArgumentException("Los Centros deben ser al menos uno, sin repetir.", nameof(credencial));

        // Cada Centro suma ~41 caracteres y Analizar rechaza más de 4096: el tope configurado nunca pasa de TopeMaximoAbsoluto.
        var tope = Math.Clamp(opciones.Value.MaxCentrosPorToken, 1, OpcionesFirmaCredencialAcceso.TopeMaximoAbsoluto);
        var grupos = credencial.Centros.Count <= tope
            ? [credencial.Centros]
            : credencial.Centros.Select(c => (IReadOnlyList<CentroConFechaMaxima>)[c]).ToList();

        var (kid, privada) = llavero.ActivaParaFirmar(credencial.Centros.Max(c => c.FechaMaxima));
        using (privada)
        {
            return grupos
                .Select(centros =>
                {
                    var cuerpo = FormatoTokenCredencial.Cuerpo(kid, credencial.CredencialId, credencial.Serie, centros);
                    var firma = FormatoTokenCredencial.NormalizarSBaja(privada.SignData(
                        FormatoTokenCredencial.Bytes(cuerpo), HashAlgorithmName.SHA256,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
                    return FormatoTokenCredencial.Componer(cuerpo, firma);
                })
                .ToList();
        }
    }

    public CredencialVerificada? Verificar(string? token)
    {
        try
        {
            var analizado = FormatoTokenCredencial.Analizar(token);
            if (analizado is null)
                return null;

            var (contenido, cuerpo, firma) = analizado.Value;
            var clave = llavero.Vigentes().FirstOrDefault(c => c.Kid == contenido.Kid);
            if (clave is null)
                return null;

            using var publica = ECDsa.Create();
            publica.ImportSubjectPublicKeyInfo(Convert.FromBase64String(clave.SpkiBase64), out _);
            return publica.VerifyData(
                FormatoTokenCredencial.Bytes(cuerpo), firma, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                ? contenido
                : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidOperationException
                                       or ArgumentException or IOException or NullReferenceException)
        {
            return null; // Fallo cerrado: un llavero ilegible o una clave corrupta nunca autoriza.
        }
    }

    public IReadOnlyList<ClavePublicaCredencial> ClavesPublicasVigentes() =>
        llavero.Vigentes().Select(c => new ClavePublicaCredencial(c.Kid, c.SpkiBase64, c.Activa)).ToList();

    public void Rotar() => llavero.Rotar(DateOnly.FromDateTime((reloj ?? TimeProvider.System).GetUtcNow().UtcDateTime));

    public void RotarDeEmergencia() => llavero.RotarDeEmergencia();
}
