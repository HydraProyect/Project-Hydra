using CaeManager.Application.Plataforma;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CaeManager.Infrastructure.Plataforma;

/// <inheritdoc cref="IElevacionEscrituraPrivilegiada" />
///
/// <summary>
/// Contraparte de <see cref="Interceptors.TenantRlsConnectionInterceptor"/>:
/// aquel decide el rol en CADA apertura de conexión consultando
/// <see cref="AmbitoEscrituraPrivilegiada.Actual"/>; esta clase es la única
/// que ESTABLECE ese ámbito, y la única razón por la que puede haber una
/// conexión YA abierta con el rol equivocado en el instante en que se abre —
/// de ahí el <c>SET ROLE</c> inmediato aquí, además de lo que hará el
/// interceptor en la próxima apertura.
///
/// Recibe <see cref="CaeManagerDbContext"/> directamente, no una abstracción
/// de más alto nivel: es la única pieza de Application/Infrastructure que
/// necesita tocar la conexión Npgsql cruda para un <c>SET ROLE</c> fuera del
/// ciclo normal de apertura/cierre que gestiona el interceptor.
/// </summary>
public sealed class ElevacionEscrituraPrivilegiada(CaeManagerDbContext contexto) : IElevacionEscrituraPrivilegiada
{
    private const string RolAprovisionamiento = "cae_app_aprovisionamiento";
    private const string RolSoporte = "cae_app_soporte";

    public async Task<IAsyncDisposable> EstablecerAsync(
        Guid sesionId, Guid tenantObjetivoId, CancellationToken cancellationToken = default)
    {
        var restaurador = AmbitoEscrituraPrivilegiada.Establecer(sesionId, tenantObjetivoId);

        var conexion = (NpgsqlConnection)contexto.Database.GetDbConnection();
        if (conexion.State == System.Data.ConnectionState.Open)
            await EjecutarSetRoleAsync(conexion, RolAprovisionamiento, cancellationToken);

        return new Cierre(restaurador, conexion);
    }

    private static async Task EjecutarSetRoleAsync(
        NpgsqlConnection conexion, string rol, CancellationToken cancellationToken)
    {
        await using var comando = conexion.CreateCommand();
        // Identificador fijo del código (uno de los dos roles literales de
        // esta clase), no un valor de entrada: no hay parámetro que valga
        // para SET ROLE y tampoco hace falta ninguno.
        comando.CommandText = $"SET ROLE {rol};";
        await comando.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Orden normativo (ver <see cref="AmbitoEscrituraPrivilegiada"/>): limpiar
    /// el <c>AsyncLocal</c> ANTES de emitir el <c>SET ROLE</c> de vuelta, para
    /// que el caso habitual —<c>SaveChangesAsync</c> ya cerró la conexión antes
    /// de que este ámbito se disponga— también quede seguro sin depender de
    /// que el <c>SET ROLE</c> llegue a ejecutarse: sin ámbito abierto, la
    /// próxima apertura de conexión cae en <c>cae_app_soporte</c> por la rama
    /// normal del interceptor.
    ///
    /// <c>CancellationToken.None</c> en el cierre a propósito: este es código
    /// de limpieza que corre en el <c>Dispose</c> del ámbito, y una petición
    /// cancelada no puede dejar la conexión con el rol de escritura puesto.
    /// </summary>
    private sealed class Cierre(IDisposable restaurador, NpgsqlConnection conexion) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            restaurador.Dispose();

            if (conexion.State == System.Data.ConnectionState.Open)
                await EjecutarSetRoleAsync(conexion, RolSoporte, CancellationToken.None);
        }
    }
}
