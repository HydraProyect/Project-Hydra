using CaeManager.Application.Plataforma;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CaeManager.Infrastructure.Plataforma;

/// <inheritdoc cref="IElevacionEscrituraPrivilegiada" />
///
/// <summary>
/// Contraparte de <see cref="Interceptors.TenantRlsConnectionInterceptor"/>:
/// aquel decide el rol en CADA apertura de conexión (y en cada realineación
/// de una conexión retenida por <c>PuertaAccesoDatos</c>) consultando
/// <see cref="AmbitoEscrituraPrivilegiada.Actual"/> (que abre y cierra
/// <c>ElevacionEscrituraAprovisionamientoBehavior</c>, no esta clase — ver el
/// porqué en <see cref="IElevacionEscrituraPrivilegiada"/>); esta clase solo
/// adelanta el <c>SET ROLE</c> en la conexión YA abierta en el instante en que
/// se invoca, para no depender de que haya una apertura de conexión nueva
/// después de establecer el ámbito.
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

    public Task ElevarSiConexionAbiertaAsync(CancellationToken cancellationToken = default) =>
        EjecutarSiConexionAbiertaAsync(RolAprovisionamiento, cancellationToken);

    public Task DevolverSiConexionAbiertaAsync(CancellationToken cancellationToken = default) =>
        EjecutarSiConexionAbiertaAsync(RolSoporte, cancellationToken);

    private async Task EjecutarSiConexionAbiertaAsync(string rol, CancellationToken cancellationToken)
    {
        var conexion = (NpgsqlConnection)contexto.Database.GetDbConnection();
        if (conexion.State != System.Data.ConnectionState.Open) return;

        await using var comando = conexion.CreateCommand();
        // Identificador fijo del código (uno de los dos roles literales de
        // esta clase), no un valor de entrada: no hay parámetro que valga
        // para SET ROLE y tampoco hace falta ninguno.
        comando.CommandText = $"SET ROLE {rol};";
        await comando.ExecuteNonQueryAsync(cancellationToken);

        // La conexión puede seguir abierta el resto de la operación (retenida
        // por PuertaAccesoDatos): sin anotarlo, el interceptor tomaría este rol
        // por un cambio y volvería a aplicar la sesión entera en el siguiente
        // comando. Solo tras el éxito: si el SET ROLE falla, lo anotado sigue
        // siendo el rol anterior y el siguiente comando realinea con el deseado.
        Persistence.Interceptors.TenantRlsConnectionInterceptor.AnotarRolAdoptado(conexion, rol);
    }
}
