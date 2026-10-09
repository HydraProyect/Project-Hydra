using CaeManager.Domain.Common;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.IncorporacionCartera.Recursos;

/// <summary>
/// Marcador de <c>IStringLocalizer&lt;TextosApoyoCartera&gt;</c>: los textos de la propuesta de
/// apoyo entre Gestores CAE de un mismo Operador CAE —el ítem de la campana de avisos y el panel
/// «Dar acceso»—. <c>.resx</c> neutral en español y <c>.ca-ES.resx</c> con las mismas claves. El
/// Tenant propietario se rotula «Empresa» (contrato de Mi trabajo Gen2 multi-Tenant, § 14).
/// </summary>
public sealed class TextosApoyoCartera
{
    private const string PrefijoCodigo = "PropuestaApoyo.";

    /// <summary>
    /// Texto de un error de los handlers: cada código estable <c>PropuestaApoyo.X</c> tiene su
    /// clave <c>ErrorX</c>. Un código sin clave (uno nuevo, o uno de otro behavior del pipeline)
    /// cae al genérico en vez de enseñar la clave.
    /// </summary>
    public static string MensajeDeError(IStringLocalizer<TextosApoyoCartera> textos, Error error)
    {
        ArgumentNullException.ThrowIfNull(textos);
        ArgumentNullException.ThrowIfNull(error);

        if (error.Codigo.StartsWith(PrefijoCodigo, StringComparison.Ordinal))
        {
            var texto = textos[ClaveDeError(error)];
            if (!texto.ResourceNotFound)
                return texto.Value;
        }

        return textos["ErrorGenerico"].Value;
    }

    /// <summary>
    /// Clave <c>ErrorX</c> del código <c>PropuestaApoyo.X</c>. Compuesta, así que el cruce literal
    /// de claves no la ve: la cubre ApoyoCarteraRecursosTests, código a código.
    /// </summary>
    public static string ClaveDeError(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return string.Concat("Error", error.Codigo.AsSpan(PrefijoCodigo.Length));
    }
}
