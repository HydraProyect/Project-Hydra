using System.Text.Json;
using CaeManager.Application.Auditoria.Queries;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using ClosedXML.Excel;
using MediatR;
using Microsoft.AspNetCore.Identity;

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
        // H2 (docs/ux-audit/14-administracion.md): "con los filtros aplicados"
        // — el único filtro de la pantalla es el tipo de entidad. Resuelve
        // nombre de usuario aquí (no en Application, que no conoce Identity),
        // mismo criterio que Auditoria.razor.cs.
        endpoints.MapGet("/auditoria/exportar.xlsx", async (
            string? entidad, IMediator mediator, UserManager<ApplicationUser> userManager, CancellationToken cancellationToken) =>
        {
            var resultado = await mediator.Send(
                new ObtenerAuditoriaQuery(
                    EntidadTipo: string.IsNullOrWhiteSpace(entidad) ? null : entidad,
                    UsuarioId: null, Pagina: 1, TamanoPagina: int.MaxValue),
                cancellationToken);

            var usuariosPorId = new Dictionary<Guid, string>();
            foreach (var usuarioId in resultado.Elementos.Where(r => r.UsuarioId is not null).Select(r => r.UsuarioId!.Value).Distinct())
            {
                var usuario = await userManager.FindByIdAsync(usuarioId.ToString());
                usuariosPorId[usuarioId] = usuario?.NombreCompleto ?? usuario?.Email ?? "(usuario eliminado)";
            }

            using var libro = new XLWorkbook();
            var hoja = libro.Worksheets.Add("Auditoría");

            hoja.Cell(1, 1).Value = "Fecha";
            hoja.Cell(1, 2).Value = "Entidad";
            hoja.Cell(1, 3).Value = "Acción";
            hoja.Cell(1, 4).Value = "Usuario";
            hoja.Row(1).Style.Font.Bold = true;

            var fila = 2;
            foreach (var registro in resultado.Elementos)
            {
                hoja.Cell(fila, 1).Value = registro.FechaUtc.ToLocalTime();
                hoja.Cell(fila, 2).Value = registro.EntidadTipo;
                hoja.Cell(fila, 3).Value = registro.Accion;
                hoja.Cell(fila, 4).Value = registro.UsuarioId is null ? "Sistema" : usuariosPorId.GetValueOrDefault(registro.UsuarioId.Value, "—");
                fila++;
            }

            hoja.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            libro.SaveAs(stream);

            return Results.File(
                stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "auditoria.xlsx");
        }).RequireAuthorization(policy => policy.RequireRole(Roles.Administrador));

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
