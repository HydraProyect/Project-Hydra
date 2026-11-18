namespace CaeManager.Application.Inbound.CredencialesAcceso;

/// <summary>
/// Firma y verifica el token (QR) de la credencial de acceso físico de Inbound (AF-03, decisión D-8). Asimétrica a
/// propósito: el lector sin conexión verifica con la clave pública y no puede fabricar credenciales.
///
/// Formato <c>v1.kid.credencialId.serie.centros.firma</c>: <c>centros</c> es una lista separada por comas de
/// <c>centroId:fechaMáxima</c> (<c>yyyyMMdd</c>) y la firma es ECDSA P-256 con SHA-256 en formato r‖s (IEEE P1363, el
/// que acepta WebCrypto) sobre todo lo anterior a ella. Sin ningún dato personal ni de requisitos: la identidad y el
/// veredicto se resuelven en servidor.
///
/// Fallo cerrado: <see cref="Verificar"/> devuelve <c>null</c> ante cualquier defecto y quien llama no puede
/// distinguir cuál (alterado, otra clave, <c>kid</c> desconocido, mal formado), de modo que el motivo nunca llega al
/// lector. La serie vigente y la fecha máxima se contrastan después con <see cref="CredencialVerificada.Admite"/>.
/// </summary>
public interface IFirmanteCredencialAcceso
{
    /// <summary>
    /// Un token por cada grupo de hasta <c>MaxCentrosPorToken</c> Centros; si caben todos, uno solo; por encima del tope,
    /// un token por Centro (D-8).
    /// </summary>
    IReadOnlyList<string> Firmar(CredencialParaFirmar credencial);

    /// <summary>La credencial si la firma es válida con una clave vigente (activa o anterior); <c>null</c> en otro caso.</summary>
    CredencialVerificada? Verificar(string? token);

    /// <summary>Claves públicas con las que verificar hoy (SPKI en base64), para la instantánea de AF-12.</summary>
    IReadOnlyList<ClavePublicaCredencial> ClavesPublicasVigentes();
}

/// <summary>Rotación del llavero. La de emergencia invalida todos los tokens emitidos hasta ese momento.</summary>
public interface IAdministradorLlaveroCredencialAcceso
{
    /// <summary>
    /// Nace una activa nueva y la activa pasa a anterior (solo verifica). Las anteriores se conservan mientras hayan firmado
    /// alguna fecha máxima no vencida (plan AF-03: conviven «hasta la mayor fecha máxima emitida») y se retiran después.
    /// </summary>
    void Rotar();

    /// <summary>Retira todas las claves y crea una nueva: ningún token emitido hasta ahora vuelve a verificar.</summary>
    void RotarDeEmergencia();
}

public sealed record CentroConFechaMaxima(Guid CentroId, DateOnly FechaMaxima);

public sealed record CredencialParaFirmar(Guid CredencialId, int Serie, IReadOnlyList<CentroConFechaMaxima> Centros);

public sealed record ClavePublicaCredencial(string Kid, string SpkiBase64, bool Activa);

/// <summary>Contenido de un token cuya firma ya se ha comprobado. No dice nada de la persona.</summary>
public sealed record CredencialVerificada(
    string Kid, Guid CredencialId, int Serie, IReadOnlyList<CentroConFechaMaxima> Centros)
{
    /// <summary>
    /// <c>true</c> solo si la serie es la vigente de la credencial y el token cubre ese Centro con una fecha máxima que
    /// no ha vencido. Una serie distinta (antigua o inventada) o un Centro ausente nunca se admiten.
    /// </summary>
    public bool Admite(Guid centroId, DateOnly hoy, int serieVigente) =>
        Serie == serieVigente
        && Centros.Any(c => c.CentroId == centroId && hoy <= c.FechaMaxima);
}
