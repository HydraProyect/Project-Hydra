using FluentAssertions;
using Npgsql;

namespace CaeManager.IntegrationTests.Operaciones;

/// <summary>
/// Espera observada a un bloqueo de fila (ronda 4 de Codex en la PR #2237, baja 6): una pausa fija demuestra solo que la
/// tarea no terminó en ese tiempo, no que estuviera esperando al bloqueo; en una máquina lenta, la tarea podía no haber
/// llegado aún a la sentencia y la prueba pasaba sin ejercer la carrera. Aquí se mira en <c>pg_stat_activity</c> que
/// alguna sesión esté bloqueada por la conexión que retiene el bloqueo.
/// </summary>
internal static class EsperaDeBloqueoEnPostgres
{
    /// <summary>
    /// Espera a que alguna sesión quede bloqueada por <paramref name="pidBloqueante"/>. Falla si
    /// <paramref name="tarea"/> termina antes (no llegó a esperar al bloqueo) o si en 30 s nadie espera.
    /// </summary>
    internal static async Task BloqueadaPorAsync(string cadenaObservador, int pidBloqueante, Task tarea, string porque)
    {
        await using var observador = new NpgsqlConnection(cadenaObservador);
        await observador.OpenAsync();
        await using var consulta = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE @pid = ANY(pg_blocking_pids(pid)))", observador);
        consulta.Parameters.AddWithValue("pid", pidBloqueante);

        var limite = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < limite)
        {
            tarea.IsCompleted.Should().BeFalse(porque);
            if ((bool)(await consulta.ExecuteScalarAsync())!)
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Nadie llegó a esperar al bloqueo: {porque}");
    }
}
