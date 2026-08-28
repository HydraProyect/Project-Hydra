using Npgsql;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Siembra por SQL directo en las tablas legacy <c>Clientes</c> y
/// <c>Subcontratas</c>.
///
/// F3c (2026-08-28) retiró los tipos de dominio <c>Cliente</c>/<c>Subcontrata</c>
/// y las tablas al final de la cadena de migraciones. Los tests de F3a
/// (backfill) y F3b (repunteo de FKs) se ejecutan en un punto ANTERIOR de esa
/// cadena, donde las tablas todavía existen físicamente — pero ya no hay
/// entidad EF que las mapee, así que la única forma de sembrarlas es SQL
/// directo. Es el mismo patrón que
/// <c>F3aEmpresasUnificadaPreparacionTests</c> ya usaba para sembrar una
/// <c>Empresa</c> antes de que existiera la columna <c>EsPropia</c>: código
/// nuevo, base de datos en un estado anterior.
///
/// Las columnas de abajo NO están escritas de memoria: salen del snapshot
/// <c>20260822163546_EstadoBootstrapPlataforma.Designer.cs</c>, que es el
/// modelo exacto en el punto de la cadena donde estos tests siembran.
/// </summary>
internal static class SiembraTablasLegacyF3
{
    public static async Task InsertarClienteAsync(
        string cadenaConexion,
        Guid id,
        Guid tenantId,
        string razonSocial,
        string cif,
        bool esCritico,
        string? notas = null,
        Guid? ejecutivoUsuarioId = null,
        Guid? eliminadoPorUsuarioId = null)
    {
        await using var conexion = new NpgsqlConnection(cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            INSERT INTO "Clientes"
                ("Id", "TenantId", "RazonSocial", "Cif", "EsCritico", "Notas", "EjecutivoUsuarioId",
                 "CreadoEnUtc", "EstaEliminado", "EliminadoEnUtc", "EliminadoPorUsuarioId", "Version")
            VALUES (@id, @tenantId, @razonSocial, @cif, @esCritico, @notas, @ejecutivoUsuarioId,
                    now(), @estaEliminado, @eliminadoEnUtc, @eliminadoPorUsuarioId, @version);
            """;
        comando.Parameters.AddWithValue("id", id);
        comando.Parameters.AddWithValue("tenantId", tenantId);
        comando.Parameters.AddWithValue("razonSocial", razonSocial);
        comando.Parameters.AddWithValue("cif", cif);
        comando.Parameters.AddWithValue("esCritico", esCritico);
        comando.Parameters.AddWithValue("notas", (object?)notas ?? DBNull.Value);
        comando.Parameters.AddWithValue("ejecutivoUsuarioId", (object?)ejecutivoUsuarioId ?? DBNull.Value);
        comando.Parameters.AddWithValue("estaEliminado", eliminadoPorUsuarioId is not null);
        comando.Parameters.AddWithValue(
            "eliminadoEnUtc", eliminadoPorUsuarioId is null ? DBNull.Value : DateTime.UtcNow);
        comando.Parameters.AddWithValue("eliminadoPorUsuarioId", (object?)eliminadoPorUsuarioId ?? DBNull.Value);
        comando.Parameters.AddWithValue("version", Guid.NewGuid());
        await comando.ExecuteNonQueryAsync();
    }

    /// <param name="nivelServicio">
    /// El entero de <c>NivelServicioSubcontrata</c> tal y como lo guardaba EF
    /// antes de F3a: 0 = Gestionada (default), 1 = Supervisada. Se pasa como
    /// entero a propósito — el CASE WHEN del backfill de F3a es justo lo que
    /// traduce ese entero a texto, y el test debe poder sembrar el valor
    /// crudo sin depender del enum, que ya no participa en esta tabla.
    /// </param>
    public static async Task InsertarSubcontrataAsync(
        string cadenaConexion,
        Guid id,
        Guid tenantId,
        string razonSocial,
        string? cif,
        int nivelServicio = 0)
    {
        await using var conexion = new NpgsqlConnection(cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            INSERT INTO "Subcontratas"
                ("Id", "TenantId", "RazonSocial", "Cif", "NivelServicio",
                 "CreadoEnUtc", "EstaEliminado", "Version")
            VALUES (@id, @tenantId, @razonSocial, @cif, @nivelServicio, now(), false, @version);
            """;
        comando.Parameters.AddWithValue("id", id);
        comando.Parameters.AddWithValue("tenantId", tenantId);
        comando.Parameters.AddWithValue("razonSocial", razonSocial);
        comando.Parameters.AddWithValue("cif", (object?)cif ?? DBNull.Value);
        comando.Parameters.AddWithValue("nivelServicio", nivelServicio);
        comando.Parameters.AddWithValue("version", Guid.NewGuid());
        await comando.ExecuteNonQueryAsync();
    }
}
