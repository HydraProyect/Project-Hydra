using Microsoft.EntityFrameworkCore;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Siembra un Centro por SQL crudo en tests que migran solo hasta una
/// migración intermedia: el INSERT de EF usa el modelo actual y fallaría con
/// "column ... does not exist" por las columnas que añaden migraciones
/// posteriores (p. ej. "GestionCae", de AgregarGestionCaeCentro). Solo usa las
/// columnas de la línea base de "Centros".
/// </summary>
public static class SiembraCentroEnEsquemaAnterior
{
    public static Task InsertarAsync(
        DbContext contexto, Guid centroId, Guid tenantId, Guid clienteId, Guid empresaId, string nombre) =>
        contexto.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Centros" ("Id", "TenantId", "ClienteId", "EmpresaId", "Nombre", "Version", "CreadoEnUtc", "EstaEliminado")
            VALUES ({centroId}, {tenantId}, {clienteId}, {empresaId}, {nombre}, {Guid.NewGuid()}, {DateTime.UtcNow}, FALSE)
            """);
}
