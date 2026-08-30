using CaeManager.Application.Common;

namespace CaeManager.Infrastructure.AsistenteIa;

/// <summary>
/// Cuenta cada petición que sale hacia un proveedor de IA (ver
/// <see cref="Observabilidad.LlamadasProveedorIa"/>).
///
/// Va <b>dentro</b> del pipeline de resiliencia, no fuera: así cada reintento
/// que hace Polly pasa por aquí y suma. Es lo que se quiere medir — el
/// proveedor puede haber procesado y cobrado la petición cuya respuesta se
/// perdió, de modo que un reintento es otra llamada potencialmente facturable,
/// no la misma vista dos veces. Colocado fuera, contaría operaciones lógicas y
/// volvería a esconder exactamente el gasto que este contador existe para
/// revelar.
///
/// Solo el host como etiqueta. Ni la ruta (llevaría a cardinalidad alta sin
/// aportar nada: cada proveedor tiene dos o tres) ni nada derivado del cuerpo,
/// que es contenido de documentos de clientes.
/// </summary>
public class ContadorLlamadasProveedorIaHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host ?? "desconocido";
        Observabilidad.LlamadasProveedorIa.Add(1, new KeyValuePair<string, object?>("Host", host));

        return base.SendAsync(request, cancellationToken);
    }
}
