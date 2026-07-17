using System.Text.Json;
using CaeManager.Application.Auditoria.Queries;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using MediatR;

namespace CaeManager.Web.Features.Auditoria;

/// <summary>
/// Sirve el archivo que tenía un Documento justo antes de una renovación —
/// AuditoriaInterceptor ya guarda su ArchivoUrl en el JSON de "DatosAntes"
/// de cada Modificado, así que no hace falta ningún cambio de modelo, solo
/// leerlo. Solo Administrador, igual que la propia página de Auditoría.
/// </summary>
public static class AuditoriaEndpoints
{
    public static IEndpointRouteBuilder MapAuditoriaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/auditoria/{id:guid}/archivo-anterior", async (
            Guid id, IMediator mediator, IFileStorageService almacenamiento, CancellationToken cancellationToken) =>
        {
            var registro = await mediator.Send(new ObtenerRegistroAuditoriaPorIdQuery(id), cancellationToken);
            if (registro is null || registro.EntidadTipo != "Documento" || registro.DatosAntes is null)
                return Results.NotFound();

            string? archivoUrl = null;
            try
            {
                using var datosAntes = JsonDocument.Parse(registro.DatosAntes);
                if (datosAntes.RootElement.TryGetProperty("ArchivoUrl", out var valor) && valor.ValueKind == JsonValueKind.String)
                    archivoUrl = valor.GetString();
            }
            catch (JsonException)
            {
                // DatosAntes mal formado no debería ocurrir (lo escribe el propio
                // interceptor), pero un 404 es más seguro que propagar la excepción.
            }

            if (string.IsNullOrWhiteSpace(archivoUrl))
                return Results.NotFound();

            try
            {
                var flujo = await almacenamiento.AbrirAsync(archivoUrl, cancellationToken);
                return Results.File(flujo, "application/pdf", "documento-anterior.pdf");
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound();
            }
        }).RequireAuthorization(policy => policy.RequireRole(Roles.Administrador));

        return endpoints;
    }
}
