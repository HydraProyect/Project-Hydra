using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

/// <summary>
/// SQL y no el seguimiento de EF: leer y sumar perdería peticiones simultáneas, y este contador se
/// escribe fuera del <c>SaveChanges</c> de la petición, que en esta API solo lee. La política de RLS
/// comprueba el Tenant de la fila.
/// </summary>
public class RastroUsoApi(CaeManagerDbContext dbContext) : IRastroUsoApi
{
    public Task RegistrarPeticionAsync(
        Guid tenantId, Guid apiKeyId, TipoClaveUsoApi tipoClave, DateOnly dia, string endpoint,
        CancellationToken cancellationToken = default) =>
        dbContext.Database.ExecuteSqlAsync($"""
            INSERT INTO "UsosApiDiarios" ("Id", "TenantId", "ApiKeyId", "TipoClave", "Dia", "Endpoint", "Peticiones")
            VALUES ({IdentificadorEntidad.Nuevo()}, {tenantId}, {apiKeyId}, {(int)tipoClave}, {dia}, {endpoint}, 1)
            ON CONFLICT ("TenantId", "ApiKeyId", "TipoClave", "Dia", "Endpoint")
            DO UPDATE SET "Peticiones" = "UsosApiDiarios"."Peticiones" + 1
            """, cancellationToken);
}
