using CaeManager.Application.EstadoMaterializado;
using CaeManager.Domain.EstadoMaterializado;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.EstadoMaterializado;

/// <summary>
/// Sentencias de la cola <c>RecalculosPendientes</c> (diseño § 4.5 y § 13.8), compartidas por quien encola (el recálculo en
/// línea, el ámbito sin recálculo) y por quien la consume (<see cref="ProcesadorColaEstadoMaterializado"/>). Todas con el
/// <c>TenantId</c> explícito además de la RLS.
///
/// <para>
/// <b>Sin perder un cambio mientras la cola trabaja.</b> Quien encola no duplica una entrada sin intentar que ya cubra su
/// cambio, pero solo cuenta las que nadie está procesando: la comprobación lleva <c>FOR KEY SHARE SKIP LOCKED</c>, así que
/// una entrada que la cola ya reclamó (con <c>FOR UPDATE</c>, en una transacción que la borrará) no la tapa, y se inserta
/// otra. La que sí cuenta queda bloqueada en compartido hasta el COMMIT de quien encola, y la cola no la reclama hasta
/// entonces: cuando la lee, el cambio ya está confirmado.
/// </para>
/// </summary>
public static class ColaEstadoMaterializado
{
    /// <summary>Una entrada «Tenant entero» si no hay ya una sin intentar y libre (la cola las junta igualmente).</summary>
    public static Task EncolarTenantEnteroAsync(DbContext dbContext, Guid tenantId, string motivo) =>
        InsertarTenantEnteroAsync(dbContext, tenantId, motivo, comoIntentoFallido: false);

    /// <summary>
    /// Un relleno que falló sin entradas reclamadas (el Tenant no tenía versión): deja una entrada «Tenant entero» ya
    /// intentada una vez, si no hay ya alguna de Tenant entero, para que el relleno espere como cualquier entrada fallida
    /// (2, 4, 8… minutos, como mucho una hora) en vez de repetirse en cada sondeo.
    /// </summary>
    public static Task AnotarFalloDeRellenoSinEntradasAsync(DbContext dbContext, Guid tenantId, string motivo) =>
        InsertarTenantEnteroAsync(dbContext, tenantId, motivo, comoIntentoFallido: true);

    private static Task InsertarTenantEnteroAsync(DbContext dbContext, Guid tenantId, string motivo, bool comoIntentoFallido)
    {
        var texto = Recortar(motivo);
        return dbContext.Database.ExecuteSqlAsync($"""
            INSERT INTO "RecalculosPendientes" ("Id", "Ambito", "EntidadId", "Motivo", "CreadoEnUtc", "IntentoEnUtc", "Intentos", "TenantId")
            SELECT gen_random_uuid(), {(short)AmbitoEstadoEntidad.Centro}, NULL, {texto}, now(),
                   CASE WHEN {comoIntentoFallido} THEN now() END, CASE WHEN {comoIntentoFallido} THEN 1 ELSE 0 END, {tenantId}
            WHERE NOT EXISTS (
                SELECT 1 FROM "RecalculosPendientes" p
                WHERE p."TenantId" = {tenantId} AND p."EntidadId" IS NULL AND ({comoIntentoFallido} OR p."IntentoEnUtc" IS NULL)
                FOR KEY SHARE OF p SKIP LOCKED)
            """, CancellationToken.None);
    }

    /// <summary>Una entrada por raíz (los Centros afectados como Centro entero: por encima, nunca por debajo).</summary>
    public static Task EncolarRaicesAsync(DbContext dbContext, Guid tenantId, RaicesDeRecalculo raices, string motivo)
    {
        var filas = raices.CentroIds.Concat(raices.CentrosAfectados).Distinct().Select(id => (Ambito: (short)AmbitoEstadoEntidad.Centro, Id: id))
            .Concat(raices.TrabajadorIds.Distinct().Select(id => (Ambito: (short)AmbitoEstadoEntidad.Trabajador, Id: id)))
            .Concat(raices.EmpresaIds.Distinct().Select(id => (Ambito: (short)AmbitoEstadoEntidad.Empresa, Id: id)))
            .ToList();
        if (filas.Count == 0)
            return Task.CompletedTask;
        var ambitos = filas.Select(f => f.Ambito).ToArray();
        var ids = filas.Select(f => f.Id).ToArray();
        var texto = Recortar(motivo);
        return dbContext.Database.ExecuteSqlAsync($"""
            INSERT INTO "RecalculosPendientes" ("Id", "Ambito", "EntidadId", "Motivo", "CreadoEnUtc", "IntentoEnUtc", "Intentos", "TenantId")
            SELECT gen_random_uuid(), r.ambito, r.id, {texto}, now(), NULL, 0, {tenantId}
            FROM unnest({ambitos}, {ids}) AS r(ambito, id)
            WHERE NOT EXISTS (
                SELECT 1 FROM "RecalculosPendientes" p
                WHERE p."TenantId" = {tenantId} AND p."IntentoEnUtc" IS NULL
                  AND (p."EntidadId" IS NULL OR (p."Ambito" = r.ambito AND p."EntidadId" = r.id))
                FOR KEY SHARE OF p SKIP LOCKED)
            """, CancellationToken.None);
    }

    /// <summary>
    /// Reclama hasta <paramref name="maximo"/> entradas del Tenant que toca intentar (sin intentar, o con la espera tras su
    /// último fallo cumplida: 2, 4, 8… minutos, como mucho una hora), primero las de Tenant entero. <c>FOR UPDATE SKIP
    /// LOCKED</c>: las que otra transacción tiene bloqueadas se saltan, nunca se espera por ellas.
    /// </summary>
    public static Task<List<EntradaColaEstado>> ReclamarAsync(DbContext dbContext, Guid tenantId, int maximo) =>
        dbContext.Database.SqlQuery<EntradaColaEstado>($"""
            SELECT p."Id", p."Ambito", p."EntidadId"
            FROM "RecalculosPendientes" p
            WHERE p."TenantId" = {tenantId}
              AND (p."IntentoEnUtc" IS NULL
                   OR p."IntentoEnUtc" < now() - least(interval '1 minute' * power(2, least(p."Intentos", 10)), interval '1 hour'))
            ORDER BY (p."EntidadId" IS NULL) DESC, p."CreadoEnUtc", p."Id"
            LIMIT {maximo}
            FOR UPDATE OF p SKIP LOCKED
            """).ToListAsync(CancellationToken.None);

    /// <summary>
    /// Borra todas las entradas del Tenant que nadie más tiene bloqueadas (las reclamadas por esta transacción, también):
    /// las cubre el recálculo del Tenant entero que viene a continuación, que lee después de este borrado. Lo que se encole
    /// a partir de aquí se queda para la pasada siguiente.
    /// </summary>
    public static Task<int> BorrarTodasAsync(DbContext dbContext, Guid tenantId) =>
        dbContext.Database.ExecuteSqlAsync($"""
            DELETE FROM "RecalculosPendientes"
            WHERE "TenantId" = {tenantId}
              AND "Id" IN (SELECT p."Id" FROM "RecalculosPendientes" p WHERE p."TenantId" = {tenantId} FOR UPDATE OF p SKIP LOCKED)
            """, CancellationToken.None);

    /// <summary>Borra las entradas ya procesadas (reclamadas por esta transacción).</summary>
    public static Task<int> BorrarAsync(DbContext dbContext, Guid tenantId, IReadOnlyCollection<Guid> ids)
    {
        var array = ids.ToArray();
        return dbContext.Database.ExecuteSqlAsync($"""
            DELETE FROM "RecalculosPendientes" WHERE "TenantId" = {tenantId} AND "Id" = ANY({array})
            """, CancellationToken.None);
    }

    /// <summary>Anota un intento fallido: la entrada espera más cada vez antes de volver a reclamarse.</summary>
    public static Task<int> AnotarFalloAsync(DbContext dbContext, Guid tenantId, IReadOnlyCollection<Guid> ids)
    {
        var array = ids.ToArray();
        return dbContext.Database.ExecuteSqlAsync($"""
            UPDATE "RecalculosPendientes" SET "IntentoEnUtc" = now(), "Intentos" = "Intentos" + 1
            WHERE "TenantId" = {tenantId} AND "Id" = ANY({array})
            """, CancellationToken.None);
    }

    /// <summary>Si el Tenant tiene alguna entrada en la cola, de Tenant entero (<c>true</c>) o de raíces.</summary>
    public static async Task<(bool Alguna, bool TenantEntero)> HayEntradasAsync(DbContext dbContext, Guid tenantId)
    {
        var fila = (await dbContext.Database.SqlQuery<ResumenColaEstado>($"""
            SELECT EXISTS (SELECT 1 FROM "RecalculosPendientes" WHERE "TenantId" = {tenantId}) AS "Alguna",
                   EXISTS (SELECT 1 FROM "RecalculosPendientes" WHERE "TenantId" = {tenantId} AND "EntidadId" IS NULL) AS "TenantEntero"
            """).ToListAsync(CancellationToken.None)).Single();
        return (fila.Alguna, fila.TenantEntero);
    }

    private static string Recortar(string motivo) =>
        motivo.Length <= RecalculoPendiente.LongitudMaximaMotivo ? motivo : motivo[..RecalculoPendiente.LongitudMaximaMotivo];
}

/// <summary>Una entrada reclamada de <c>RecalculosPendientes</c>; <see cref="EntidadId"/> null es el Tenant entero.</summary>
public sealed class EntradaColaEstado
{
    public Guid Id { get; set; }

    /// <summary>Un <see cref="AmbitoEstadoEntidad"/>, tal como lo guarda la columna.</summary>
    public short Ambito { get; set; }

    public Guid? EntidadId { get; set; }
}

/// <summary>Resumen de la cola de un Tenant.</summary>
public sealed class ResumenColaEstado
{
    public bool Alguna { get; set; }
    public bool TenantEntero { get; set; }
}
