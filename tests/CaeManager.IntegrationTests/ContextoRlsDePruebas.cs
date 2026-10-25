using System.Collections.Concurrent;
using System.Security.Cryptography;
using CaeManager.Infrastructure.Persistence.ContextoRls;
using Npgsql;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Contexto RLS firmado (P6, fase «contraer») para los tests que preparan una
/// conexión cruda a mano en vez de pasar por <c>TenantRlsConnectionInterceptor</c>.
///
/// <para>
/// Desde la migración <c>ContraeContextoRlsFirmado</c> ninguna política lee las
/// GUC sueltas (<c>app.tenant_id</c>, <c>app.tenant_origen_id</c>,
/// <c>app.usuario_id</c>): leen <c>app_ctx_*()</c>, que solo cree un token
/// firmado en <c>app.contexto</c> para el backend de esta conexión. Un test que
/// siguiera fijando la GUC suelta vería cero filas, igual que un atacante con la
/// credencial de tráfico. Este ayudante firma como lo hace el interceptor, con
/// una clave registrada en la base del test por la conexión propietaria.
/// </para>
///
/// <para>
/// <b>Independiente del rol de la conexión.</b> La clave se lee por una conexión
/// propietaria aparte, no por <paramref name="conexion"/>: así se puede firmar
/// también después de un <c>SET ROLE</c> a <c>cae_app_soporte</c> o
/// <c>cae_app_aprovisionamiento</c>, y el token queda atado al
/// <c>ProcessID</c> de la conexión del test.
/// </para>
/// </summary>
internal static class ContextoRlsDePruebas
{
    private static readonly ConcurrentDictionary<string, ClaveContextoLeida> ClavePorBase = new();

    /// <summary>
    /// Firma el contexto y lo fija en <c>app.contexto</c> de la conexión
    /// (abierta). <paramref name="local"/> = <c>true</c> lo fija solo para la
    /// transacción en curso, como <c>set_config(..., true)</c>. Sin ningún
    /// identificador el token sigue siendo válido: «contexto firmado sin Tenant
    /// ni usuario», que no es lo mismo que no tener contexto.
    /// </summary>
    internal static async Task FijarAsync(
        NpgsqlConnection conexion, Guid? tenantId = null, Guid? tenantOrigenId = null, Guid? usuarioId = null,
        bool local = false, NpgsqlTransaction? transaccion = null)
    {
        var token = await FirmarAsync(conexion, tenantId, tenantOrigenId, usuarioId);
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = "SELECT set_config('app.contexto', @contexto, @local);";
        comando.Parameters.AddWithValue("contexto", token);
        comando.Parameters.AddWithValue("local", local);
        await comando.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// El token sin fijarlo, para los tests que lo combinan en su propio
    /// comando (por ejemplo, con un <c>SET LOCAL ROLE</c> en la misma sentencia).
    /// </summary>
    internal static async Task<string> FirmarAsync(
        NpgsqlConnection conexion, Guid? tenantId = null, Guid? tenantOrigenId = null, Guid? usuarioId = null)
    {
        var clave = await ClaveAsync(conexion.Database);
        var origen = usuarioId is not null ? OrigenContextoRls.Peticion : OrigenContextoRls.Ambito;
        return TokenContextoRls.Construir(
            clave.Id, clave.Bytes, new ContextoSesionRls(tenantId, tenantOrigenId, usuarioId, origen),
            conexion.ProcessID, DateTimeOffset.UtcNow + FirmanteContextoRls.TtlPorDefecto,
            RandomNumberGenerator.GetHexString(32, lowercase: true));
    }

    private static async Task<ClaveContextoLeida> ClaveAsync(string baseDeDatos)
    {
        if (ClavePorBase.TryGetValue(baseDeDatos, out var clave)
            && clave.ValidaHasta - DateTimeOffset.UtcNow > FirmanteContextoRls.TtlPorDefecto)
            return clave;

        var cadena = new NpgsqlConnectionStringBuilder(BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool())
        {
            Database = baseDeDatos,
        }.ConnectionString;

        await using (var propietaria = new NpgsqlConnection(cadena))
        {
            await propietaria.OpenAsync();
            var leida = await ClaveContextoRls.LeerVigenteAsync(
                propietaria, BaseDatosPostgresDePruebas.ProteccionDePruebas, CancellationToken.None);
            if (leida is not null && leida.ValidaHasta - DateTimeOffset.UtcNow > FirmanteContextoRls.TtlPorDefecto)
                return ClavePorBase[baseDeDatos] = leida;
        }

        return ClavePorBase[baseDeDatos] = await ClaveContextoRls.RegistrarAsync(
            cadena, BaseDatosPostgresDePruebas.ProteccionDePruebas, ClaveContextoRls.VigenciaPorDefecto,
            CancellationToken.None);
    }
}
