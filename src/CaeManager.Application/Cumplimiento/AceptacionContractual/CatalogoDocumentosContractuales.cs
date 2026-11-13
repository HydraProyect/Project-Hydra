using System.Security.Cryptography;
using System.Text;

namespace CaeManager.Application.Cumplimiento.AceptacionContractual;

/// <summary>
/// Una versión publicada de un documento contractual que el alta puede pedir que se acepte: su clave
/// estable (<see cref="Documento"/>), el título que se muestra, la versión, desde cuándo rige, si el
/// alta no puede completarse sin aceptarla y el texto exacto que se acepta. El hash es el SHA-256 del
/// texto en UTF-8, y es el que se compara con el aceptado: si el texto cambia, cambia el hash.
/// </summary>
public sealed record DocumentoContractualPublicado(
    string Documento, string Titulo, string Version, DateTime VigenteDesdeUtc, bool ObligatorioEnAlta, string Texto)
{
    public string HashSha256 { get; } = CalcularHash(Texto);

    public static string CalcularHash(string texto)
    {
        ArgumentNullException.ThrowIfNull(texto);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(texto)));
    }
}

/// <summary>
/// Las versiones publicadas de los documentos contractuales del alta (#2009). Qué documentos son, su
/// texto y su versión los fija Chris con el abogado; no se deciden en el código. Mientras el catálogo
/// no publique nada, ninguna aceptación se puede registrar: el alta falla cerrada.
/// </summary>
public interface ICatalogoDocumentosContractuales
{
    IReadOnlyList<DocumentoContractualPublicado> Publicados { get; }
}

public static class ResolucionDocumentosContractuales
{
    /// <summary>La versión de <paramref name="documento"/> que regía en <paramref name="instanteUtc"/>, o <c>null</c>.</summary>
    public static DocumentoContractualPublicado? VigenteEn(
        IEnumerable<DocumentoContractualPublicado> publicados, string documento, DateTime instanteUtc) =>
        publicados
            .Where(d => d.Documento == documento && d.VigenteDesdeUtc <= instanteUtc)
            .MaxBy(d => d.VigenteDesdeUtc);

    /// <summary>La versión que regía en <paramref name="instanteUtc"/> de cada documento publicado.</summary>
    public static IReadOnlyList<DocumentoContractualPublicado> VigentesEn(
        IEnumerable<DocumentoContractualPublicado> publicados, DateTime instanteUtc) =>
        publicados
            .Where(d => d.VigenteDesdeUtc <= instanteUtc)
            .GroupBy(d => d.Documento)
            .Select(g => g.MaxBy(d => d.VigenteDesdeUtc)!)
            .ToList();

    /// <summary>
    /// Falla si el catálogo es ambiguo: la misma versión dos veces, o dos versiones del mismo documento
    /// que empiezan a regir en el mismo instante, porque entonces no se sabría cuál se aceptó.
    /// </summary>
    public static void ExigirCoherente(IReadOnlyCollection<DocumentoContractualPublicado> publicados)
    {
        ArgumentNullException.ThrowIfNull(publicados);
        foreach (var d in publicados)
        {
            if (string.IsNullOrWhiteSpace(d.Documento) || string.IsNullOrWhiteSpace(d.Version) || string.IsNullOrWhiteSpace(d.Texto))
                throw new InvalidOperationException("Cada documento publicado necesita clave, versión y texto.");
            if (d.VigenteDesdeUtc.Kind != DateTimeKind.Utc)
                throw new InvalidOperationException($"La vigencia de {d.Documento} {d.Version} debe estar en UTC.");
        }

        if (publicados.GroupBy(d => (d.Documento, d.Version)).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Una versión de un documento contractual está publicada dos veces.");
        if (publicados.GroupBy(d => (d.Documento, d.VigenteDesdeUtc)).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Dos versiones de un documento contractual empiezan a regir en el mismo instante.");
    }
}
