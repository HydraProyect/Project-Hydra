using System.Net.Http.Json;
using CaeManager.Application.Seguridad.Incidentes;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Seguridad.Incidentes;

/// <summary>
/// Canal HTTP de la alerta de incidente: un POST a la URL configurada
/// (<c>IncidentesSeguridad:UrlAlertaSaliente</c>), pensado para el heartbeat «fail» o un
/// incidente de Better Stack, que ya tiene la guardia con escalado. El cuerpo solo lleva
/// el identificador del incidente y el enlace a su página de Plataforma: ni datos
/// personales ni nombres de Tenant salen hacia el proveedor. Un intento por llamada; un
/// estado HTTP que no sea de éxito lanza, y la cola reintenta.
/// </summary>
public sealed class CanalAlertaIncidenteSeguridadHttp(IHttpClientFactory clientes, IOptions<OpcionesIncidentesSeguridad> opciones)
    : ICanalAlertaIncidenteSeguridad
{
    /// <summary>Cliente con nombre: la cola es singleton y no debe retener un HttpClient tipado.</summary>
    public const string NombreCliente = "AlertaIncidenteSeguridad";

    public bool Activo => !string.IsNullOrWhiteSpace(opciones.Value.UrlAlertaSaliente);

    public async Task EnviarAsync(Guid incidenteId, CancellationToken ct = default)
    {
        var o = opciones.Value;
        if (string.IsNullOrWhiteSpace(o.UrlAlertaSaliente))
            return;

        using var contenido = JsonContent.Create(CuerpoAlerta(incidenteId, o));
        var http = clientes.CreateClient(NombreCliente);
        using var respuesta = await http.PostAsync(new Uri(o.UrlAlertaSaliente, UriKind.Absolute), contenido, ct);
        respuesta.EnsureSuccessStatusCode();
    }

    /// <summary>Todo lo que sale hacia el proveedor. Público para que el test lo fije campo a campo.</summary>
    public static CuerpoAlertaIncidente CuerpoAlerta(Guid incidenteId, OpcionesIncidentesSeguridad opciones)
        => new(incidenteId, opciones.EnlaceIncidente(incidenteId));
}

/// <summary>Cuerpo de la alerta saliente: identificador y enlace, nada más.</summary>
public sealed record CuerpoAlertaIncidente(Guid IncidenteId, string Enlace);

/// <summary>
/// Canal desactivado: lo que se registra cuando no hay URL configurada (desarrollo, tests,
/// un entorno sin guardia). No envía nada; la cola lo detecta por <see cref="Activo"/> y lo
/// deja en el log como error y en la alerta operativa, para que la falta de canal no pase
/// desapercibida.
/// </summary>
public sealed class CanalAlertaIncidenteSeguridadNulo : ICanalAlertaIncidenteSeguridad
{
    public bool Activo => false;

    public Task EnviarAsync(Guid incidenteId, CancellationToken ct = default) => Task.CompletedTask;
}
