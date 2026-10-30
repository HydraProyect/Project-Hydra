using CaeManager.Application.Common;
using Microsoft.AspNetCore.Diagnostics;

namespace CaeManager.Web.Services;

/// <summary>
/// Seguimiento de S-C-3 (OWASP API4:2023, ASVS 4.0.3 V12.1.1): una petición HTTP que no encuentra
/// hueco en el presupuesto de memoria de documentos (<see cref="PresupuestoMemoriaAgotadoException"/>)
/// responde <c>503 Service Unavailable</c> con <c>Retry-After</c>, no un 500: es saturación
/// transitoria del proceso, no un fallo, y el cliente puede reintentar.
///
/// <para>
/// Solo traduce esa excepción y solo si la respuesta no ha empezado; cualquier otra sigue hasta
/// <c>UseExceptionHandler</c>. Va detrás de <c>UseCabecerasSeguridad</c> y conserva lo que ese
/// middleware y los anteriores ya fijaron (CSP, <c>nosniff</c>, <c>no-store</c>…): descarta solo lo
/// que el endpoint escribió antes de fallar. Desactiva las páginas de estado para que el 503 no se
/// reejecute como «no encontrado».
/// </para>
/// </summary>
public static class PresupuestoMemoriaAgotadoExtensions
{
    /// <summary>
    /// Segundos que se sugiere esperar: los mismos que espera por defecto una reserva
    /// (<see cref="OpcionesPresupuestoMemoriaDocumentos.EsperaMaxima"/>).
    /// </summary>
    public const string SegundosReintento = "10";

    public static IApplicationBuilder UsePresupuestoMemoriaAgotadoComo503(this IApplicationBuilder app) =>
        app.Use(async (contexto, siguiente) =>
        {
            var cabecerasPrevias = contexto.Response.Headers.ToArray();
            try
            {
                await siguiente(contexto);
            }
            catch (PresupuestoMemoriaAgotadoException) when (!contexto.Response.HasStarted)
            {
                contexto.Response.Clear();
                foreach (var (nombre, valor) in cabecerasPrevias)
                    contexto.Response.Headers[nombre] = valor;

                if (contexto.Features.Get<IStatusCodePagesFeature>() is { } paginasEstado)
                    paginasEstado.Enabled = false;

                contexto.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                contexto.Response.Headers.RetryAfter = SegundosReintento;
                contexto.Response.ContentType = "text/plain; charset=utf-8";
                await contexto.Response.WriteAsync(PresupuestoMemoriaAgotadoException.MensajeUsuario, contexto.RequestAborted);
            }
        });
}
