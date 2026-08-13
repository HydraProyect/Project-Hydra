using CaeManager.Application.Common;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Segunda línea de aislamiento por tenant, bajo el filtro global de EF Core
/// (ver docs/MULTITENANCY.md § 4.2 y RUNBOOK-RLS.md). En cada apertura de
/// conexión Npgsql fija la variable de sesión <c>app.tenant_id</c>, que las
/// políticas de Row-Level Security creadas por la migración
/// <c>HabilitarRlsPostgres</c> usan para filtrar filas incluso si una
/// consulta se saltara el <c>HasQueryFilter</c> (<c>IgnoreQueryFilters</c>
/// mal revisado, SQL crudo, un bug del propio EF).
///
/// Sin tenant resuelto (login, operaciones sobre tablas de Identity sin
/// filtro) se fija una cadena vacía a propósito:
/// <c>NULLIF(current_setting(...), '')::uuid</c> da <c>NULL</c>, y
/// <c>NULL</c> nunca iguala ningún <c>TenantId</c> real en la política —
/// fallo cerrado, el mismo principio que <see cref="ITenantActual.TenantId"/>
/// devolviendo <c>null</c>.
///
/// RLS solo restringe roles que no son propietarios de la tabla ni
/// superusuario. Mientras la conexión de la aplicación siga usando el rol
/// propietario (el caso de hoy, ver RUNBOOK-RLS.md), esta variable se fija
/// igual pero Postgres no la usa para nada — queda inerte hasta que se rota
/// la conexión de runtime al rol restringido <c>cae_app_runtime</c>.
///
/// <c>set_config</c> se invoca como función parametrizada (no
/// <c>SET app.tenant_id = '...'</c> interpolado) para no construir SQL a
/// partir de un valor variable, aunque hoy ese valor sea siempre un
/// <see cref="Guid"/> generado por el propio sistema.
/// </summary>
public class TenantRlsConnectionInterceptor(ITenantActual tenantActual) : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await FijarTenantDeSesionAsync((NpgsqlConnection)connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        FijarTenantDeSesionAsync((NpgsqlConnection)connection, CancellationToken.None)
            .GetAwaiter().GetResult();
        base.ConnectionOpened(connection, eventData);
    }

    private async Task FijarTenantDeSesionAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var comando = connection.CreateCommand();
        comando.CommandText = "SELECT set_config('app.tenant_id', @valor, false);";
        comando.Parameters.AddWithValue("valor", tenantActual.TenantId?.ToString() ?? string.Empty);
        await comando.ExecuteNonQueryAsync(cancellationToken);
    }
}
