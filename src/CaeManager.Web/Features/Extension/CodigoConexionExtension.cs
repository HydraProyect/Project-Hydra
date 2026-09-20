using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaeManager.Web.Features.Extension;

/// <summary>
/// Empaqueta en una sola cadena las tres cosas que la extensión necesita para
/// conectarse a mano: el origen de Hydra, el token y su caducidad.
///
/// <para>
/// <b>No cifra ni oculta nada</b> — es base64 de un JSON, y dentro viaja el
/// mismo token que emite <see cref="Infrastructure.Autenticacion.EmisorTokenExtension"/>.
/// Se trata con el mismo cuidado que el token: es un credencial de 8 horas.
/// </para>
///
/// <para>
/// Existe por dos motivos concretos, los dos medidos contra el flujo real.
/// Uno: pegar tres datos por separado es tres veces más fácil de equivocar que
/// pegar uno. Dos: sin la caducidad la extensión tendría que inventársela, y
/// <c>obtenerConexion</c> de <c>background.js</c> considera «no conectado» todo
/// lo que no traiga fecha — así que un token pegado a secas no serviría de nada.
/// </para>
///
/// <para>
/// El camino normal sigue siendo el enlace automático (DEC A'): esto es la
/// salida para cuando ese puente no existe en el entorno o el navegador no
/// expone <c>chrome.runtime</c> a esta página. <b>El formato es un contrato con
/// <c>extension/background.js</c></b> (<c>conectarManual</c>): si cambian los
/// nombres de campo, hay que cambiarlos en los dos lados y publicar la
/// extensión, porque una versión vieja no sabrá leer el código nuevo.
/// </para>
/// </summary>
public static class CodigoConexionExtension
{
    public static string Crear(string hydraUrl, string token, DateTime expiraEnUtc)
    {
        var carga = new CargaCodigoConexion(hydraUrl, token, expiraEnUtc.ToString("O"));
        var json = JsonSerializer.Serialize(carga);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }
}

/// <summary>
/// Nombres de campo cortos a propósito: el código se copia y se pega a mano, y
/// cada carácter de más es una oportunidad de recortarlo al seleccionar.
/// </summary>
public record CargaCodigoConexion(
    [property: JsonPropertyName("u")] string HydraUrl,
    [property: JsonPropertyName("t")] string Token,
    [property: JsonPropertyName("e")] string ExpiraEnUtc);
