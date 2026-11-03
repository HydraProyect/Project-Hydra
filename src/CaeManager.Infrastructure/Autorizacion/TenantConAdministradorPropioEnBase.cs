using CaeManager.Application.Tenants;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Autorizacion;

/// <inheritdoc cref="ITenantConAdministradorPropio" />
/// <remarks>
/// Por la función <c>app_tenant_tiene_administrador_propio</c> (SECURITY DEFINER) y no por
/// <c>UserManager.Users</c>: quien pregunta es una persona del Operador CAE externo, y lo que ve de
/// <c>AspNetUsers</c> depende del Tenant activo de la conexión (política <c>cuentas_lectura</c>). Si no
/// viera las cuentas del Tenant propietario contaría cero y daría el encargo por pleno: fallaría
/// abierto. La función solo devuelve un booleano y no expone ninguna cuenta. Responde solo por el Tenant
/// del contexto firmado de la conexión (migración <c>CierraFuncionesDeAdministradorAlTenantDelContexto</c>):
/// por cualquier otro Tenant, o sin contexto, devuelve <c>true</c>, como si lo tuviera, así que
/// el <c>tenantId</c> que se pregunta tiene que ser el Tenant activo.
/// </remarks>
public class TenantConAdministradorPropioEnBase(CaeManagerDbContext dbContext) : ITenantConAdministradorPropio
{
    public async Task<bool> TieneAdministradorPropioAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty) return true;

        var respuesta = await dbContext.Database
            .SqlQuery<bool?>($"SELECT app_tenant_tiene_administrador_propio({tenantId}) AS \"Value\"")
            .SingleAsync(cancellationToken);

        // Sin respuesta, como si lo tuviera.
        return respuesta ?? true;
    }
}
