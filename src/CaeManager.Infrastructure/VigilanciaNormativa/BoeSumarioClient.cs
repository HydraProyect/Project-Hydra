using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CaeManager.Application.VigilanciaNormativa;

namespace CaeManager.Infrastructure.VigilanciaNormativa;

/// <summary>
/// Cliente del sumario diario del BOE (`boe.es/datosabiertos/api/boe/sumario/{fecha}`,
/// documentado públicamente, sin autenticación). La forma real del JSON
/// (verificada contra la API real, no supuesta) anida sección → departamento
/// → epígrafe → item, y cada uno de esos niveles llega como objeto único
/// (no array) cuando solo hay un elemento — quirk típico de una API generada
/// desde PHP. <see cref="ComoLista"/> normaliza los dos casos.
/// </summary>
public class BoeSumarioClient(HttpClient httpClient) : IBoeSumarioClient
{
    public async Task<IReadOnlyList<PublicacionBoeDto>> ObtenerSumarioAsync(DateOnly fecha, CancellationToken cancellationToken = default)
    {
        var fechaTexto = fecha.ToString("yyyyMMdd");
        using var solicitud = new HttpRequestMessage(HttpMethod.Get, $"datosabiertos/api/boe/sumario/{fechaTexto}");
        solicitud.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var respuesta = await httpClient.SendAsync(solicitud, cancellationToken);

        // El BOE no publica sumario los festivos y fines de semana — un 404
        // ese día es el comportamiento esperado, no un fallo de la integración.
        if (respuesta.StatusCode == HttpStatusCode.NotFound) return [];

        respuesta.EnsureSuccessStatusCode();

        await using var flujo = await respuesta.Content.ReadAsStreamAsync(cancellationToken);
        using var documento = await JsonDocument.ParseAsync(flujo, cancellationToken: cancellationToken);

        var publicaciones = new List<PublicacionBoeDto>();

        if (!documento.RootElement.TryGetProperty("data", out var datos)) return publicaciones;
        if (!datos.TryGetProperty("sumario", out var sumario)) return publicaciones;
        if (!sumario.TryGetProperty("diario", out var diarioElemento)) return publicaciones;

        foreach (var dia in ComoLista(diarioElemento))
        {
            if (!dia.TryGetProperty("seccion", out var seccionElemento)) continue;

            foreach (var seccion in ComoLista(seccionElemento))
            {
                if (!seccion.TryGetProperty("departamento", out var departamentoElemento)) continue;

                foreach (var departamento in ComoLista(departamentoElemento))
                {
                    if (!departamento.TryGetProperty("epigrafe", out var epigrafeElemento)) continue;

                    foreach (var epigrafe in ComoLista(epigrafeElemento))
                    {
                        if (!epigrafe.TryGetProperty("item", out var itemElemento)) continue;

                        foreach (var item in ComoLista(itemElemento))
                        {
                            var identificador = ObtenerTexto(item, "identificador");
                            var titulo = ObtenerTexto(item, "titulo");
                            var urlHtml = ObtenerTexto(item, "url_html");

                            if (identificador is null || titulo is null || urlHtml is null) continue;

                            publicaciones.Add(new PublicacionBoeDto(identificador, titulo, urlHtml));
                        }
                    }
                }
            }
        }

        return publicaciones;
    }

    private static IEnumerable<JsonElement> ComoLista(JsonElement elemento) =>
        elemento.ValueKind == JsonValueKind.Array ? elemento.EnumerateArray() : [elemento];

    private static string? ObtenerTexto(JsonElement elemento, string propiedad) =>
        elemento.TryGetProperty(propiedad, out var valor) && valor.ValueKind == JsonValueKind.String
            ? valor.GetString()
            : null;
}
