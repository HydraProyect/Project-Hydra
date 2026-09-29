using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerLogoTenant;
using MediatR;
using Microsoft.AspNetCore.Diagnostics;

namespace CaeManager.Web.Features.Tenants;

/// <summary>
/// <c>GET /tenants/{id}/logo</c>: el logo de un Tenant para quien lo alcanza (contrato del selector de
/// Tenant, § 4.1.5, opción A). Lo sirve también a quien no es del Tenant propietario —el Gestor CAE de
/// un Operador CAE externo—, de ahí el endpoint dedicado: el camino normal de archivos solo lee la
/// carpeta del Tenant actual.
/// <list type="bullet">
/// <item>La autorización la decide <see cref="ObtenerLogoTenantQuery"/> antes de leer nada: mismo
/// predicado que el selector (lote 0).</item>
/// <item>404 idéntico —cuerpo vacío, mismas cabeceras— para Tenant no autorizado, inexistente o sin
/// logo, y para un blob que falte (I11).</item>
/// <item><c>?v=</c> no se lee: solo rompe la caché de la URL, nunca autoriza (C10).</item>
/// <item><c>no-store</c>: ninguna caché conserva el logo tras perder la autorización (C3, I17).</item>
/// </list>
/// </summary>
public static class LogoTenantEndpoints
{
    public static IEndpointRouteBuilder MapLogoTenantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/tenants/{id:guid}/logo", ServirAsync);
        return endpoints;
    }

    public static async Task<IResult> ServirAsync(
        Guid id, HttpContext contexto, IMediator mediator, IFileStorageService almacenamiento,
        CancellationToken cancellationToken)
    {
        // Antes de decidir nada, para que las cabeceras sean las mismas en todas las respuestas.
        // UseStatusCodePagesWithReExecute convertiría cualquier 404 sin cuerpo en la página Blazor
        // /not-found, y los negativos dejarían de ser idénticos (I11): en esta ruta, no.
        var paginasDeEstado = contexto.Features.Get<IStatusCodePagesFeature>();
        if (paginasDeEstado is not null) paginasDeEstado.Enabled = false;

        CabecerasArchivoSensible.ProhibirCache(contexto);
        contexto.Response.Headers.XContentTypeOptions = "nosniff";

        var logo = await mediator.Send(new ObtenerLogoTenantQuery(id), cancellationToken);
        if (logo is null)
            return Results.NotFound();

        byte[] png;
        try
        {
            // El blob está cifrado con la clave del Tenant propietario y en su carpeta: se lee dentro
            // de su ámbito, que la query acaba de autorizar, y solo para este blob (I13).
            using (AmbitoTenantExplicito.Establecer(id))
            {
                await using var flujo = await almacenamiento.AbrirAsync(logo.ArchivoClave, cancellationToken);
                using var memoria = new MemoryStream();
                await flujo.CopyToAsync(memoria, cancellationToken);
                png = memoria.ToArray();
            }
        }
        catch (FileNotFoundException)
        {
            return Results.NotFound();
        }

        contexto.Response.Headers.ContentDisposition = "inline";
        return Results.File(png, "image/png");
    }
}
