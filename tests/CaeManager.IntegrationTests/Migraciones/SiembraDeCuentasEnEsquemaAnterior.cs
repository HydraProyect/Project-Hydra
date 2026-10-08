using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// Alta de cuentas en una base migrada solo hasta una migración intermedia. Con
/// <c>contexto.Users.Add</c> el INSERT lleva todas las columnas del modelo de hoy, y la
/// primera columna que una migración posterior añada a <c>AspNetUsers</c> lo rompe con
/// 42703 (pasó con <c>Avatar</c>). Aquí se insertan solo las columnas que el esquema
/// migrado tiene de verdad, leídas de <c>information_schema</c>, con los valores que EF
/// habría escrito.
/// </summary>
internal static class SiembraDeCuentasEnEsquemaAnterior
{
    public static async Task InsertarAsync(CaeManagerDbContext contexto, params ApplicationUser[] cuentas)
    {
        var existentes = await contexto.Database
            .SqlQueryRaw<string>(
                """SELECT column_name AS "Value" FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'AspNetUsers'""")
            .ToListAsync();
        if (existentes.Count == 0)
            throw new InvalidOperationException("AspNetUsers no existe en el esquema migrado: la siembra no puede decidir qué columnas escribir.");

        var tipo = contexto.Model.FindEntityType(typeof(ApplicationUser))!;
        var tabla = StoreObjectIdentifier.Table(tipo.GetTableName()!, tipo.GetSchema());
        var columnas = tipo.GetProperties()
            .Select(p => (Propiedad: p, Columna: p.GetColumnName(tabla)))
            .Where(c => c.Columna is not null && existentes.Contains(c.Columna))
            .ToList();

        // La cuenta sin Coordinador CAE entra antes que quien cuelga de ella (FK a la propia tabla).
        foreach (var cuenta in cuentas.OrderBy(c => c.CoordinadorUsuarioId is null ? 0 : 1))
        {
            var entrada = contexto.Entry(cuenta);
            var parametros = columnas
                .Select((c, i) =>
                {
                    var valor = entrada.Property(c.Propiedad.Name).CurrentValue;
                    var conversor = c.Propiedad.GetTypeMapping().Converter;
                    if (valor is not null && conversor is not null) valor = conversor.ConvertToProvider(valor);
                    return new NpgsqlParameter($"p{i}", valor ?? DBNull.Value);
                })
                .ToArray<object>();

            var sql = $"""
                INSERT INTO "AspNetUsers" ({string.Join(", ", columnas.Select(c => $"\"{c.Columna}\""))})
                VALUES ({string.Join(", ", columnas.Select((_, i) => $"@p{i}"))})
                """;
            await contexto.Database.ExecuteSqlRawAsync(sql, parametros);
            entrada.State = EntityState.Detached;
        }
    }
}
