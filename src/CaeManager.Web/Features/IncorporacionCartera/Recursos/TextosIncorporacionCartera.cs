using System.Globalization;
using System.Resources;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.IncorporacionCartera.Recursos;

/// <summary>
/// Marcador de <c>IStringLocalizer&lt;TextosIncorporacionCartera&gt;</c>: los
/// textos de la bandeja de solicitudes de incorporación a cartera, del aviso
/// al Coordinador CAE y del diálogo de solicitar. <c>.resx</c> neutral en
/// español y <c>.ca-ES.resx</c> con las mismas claves. El Tenant propietario
/// se rotula «Empresa» (contrato de Mi trabajo Gen2 multi-Tenant, § 14).
/// </summary>
public sealed class TextosIncorporacionCartera
{
    private const string PrefijoCodigo = "SolicitudCartera.";

    private static readonly ResourceManager Recursos = new(typeof(TextosIncorporacionCartera));

    /// <summary>
    /// Para el código sin inyección (el rótulo del enlace en
    /// <c>CatalogoMenuLateral</c>). Lee el mismo recurso con la cultura de
    /// interfaz en curso, como <c>ResourceManagerStringLocalizer</c>.
    /// </summary>
    public static string Texto(string clave) =>
        Recursos.GetString(clave, CultureInfo.CurrentUICulture) ?? clave;

    /// <summary>
    /// Texto de un error de los handlers: cada código estable
    /// <c>SolicitudCartera.X</c> tiene su clave <c>ErrorX</c>. Un código sin
    /// clave (uno nuevo, o uno de otro behavior del pipeline) cae al genérico
    /// en vez de enseñar la clave.
    /// </summary>
    public static string MensajeDeError(IStringLocalizer<TextosIncorporacionCartera> textos, Error error)
    {
        ArgumentNullException.ThrowIfNull(textos);
        ArgumentNullException.ThrowIfNull(error);

        if (error.Codigo.StartsWith(PrefijoCodigo, StringComparison.Ordinal))
        {
            var texto = textos["Error" + error.Codigo[PrefijoCodigo.Length..]];
            if (!texto.ResourceNotFound)
                return texto.Value;
        }

        return textos["ErrorGenerico"].Value;
    }
}
