using CaeManager.Application.EstadoMaterializado;
using CaeManager.Domain.EstadoMaterializado;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace CaeManager.Infrastructure.EstadoMaterializado;

/// <inheritdoc cref="IEscritorEstadoMaterializado"/>
/// <remarks>
/// <para>
/// SQL por lotes en vez del rastreador de EF: un <c>Update()</c> completo choca con el permiso de UPDATE por columnas
/// (42501), y estas filas no son datos de negocio que deban pasar por la auditoría de cambios. El <c>Id</c> se genera en la
/// base y el <c>TenantId</c> sale del contexto RLS de la conexión (<c>app.tenant_id</c>); la política RLS con
/// <c>WITH CHECK</c> rechaza cualquier otro. Sin contexto de Tenant, el <c>NOT NULL</c> de <c>TenantId</c> hace fallar la
/// escritura en vez de escribir filas huérfanas.
/// </para>
/// <para>
/// Cada tabla se escribe con una sola sentencia que compara con lo guardado, dentro del alcance del cálculo: borra las filas
/// del alcance que el cálculo ya no trae, actualiza solo las columnas calculadas de las que cambiaron y añade las nuevas. Una
/// fila igual no se toca, así que un recálculo sin cambios no deja versiones muertas de fila (diseño § 13.5). La fila se
/// identifica por su clave natural, que nunca se actualiza; el <c>TenantId</c> lo pone la RLS en el filtro.
/// <c>CalculadoEnUtc</c> no cuenta como cambio: es la hora de la última escritura que cambió la fila.
/// </para>
/// </remarks>
public sealed class EscritorEstadoMaterializado(CaeManagerDbContext dbContext) : IEscritorEstadoMaterializado
{
    private const string TenantDeLaConexion = "NULLIF(current_setting('app.tenant_id', true), '')::uuid";

    public async Task EscribirAsync(CalculoEstadoMaterializado calculo, bool crearVersionSiFalta, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
        {
            await EscribirEnTransaccionAsync(calculo, crearVersionSiFalta, cancellationToken);
            return;
        }

        var estrategia = dbContext.Database.CreateExecutionStrategy();
        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            await EscribirEnTransaccionAsync(calculo, crearVersionSiFalta, cancellationToken);
            await transaccion.CommitAsync(cancellationToken);
        });
    }

    private async Task EscribirEnTransaccionAsync(CalculoEstadoMaterializado calculo, bool crearVersionSiFalta, CancellationToken ct)
    {
        await EscribirParesAsync(calculo, ct);
        await EscribirPlataformaAsync(calculo, ct);
        await EscribirEntidadesAsync(calculo, ct);

        // La versión del Tenant: el relleno completo la crea; un recálculo parcial solo la sube si ya existe, para que los
        // lectores no tomen por entero un Tenant a medio rellenar (diseño § 7.3).
        await dbContext.Database.ExecuteSqlRawAsync(
            crearVersionSiFalta
                ? $"""
                  INSERT INTO "VersionesEstadoTenant" ("Id", "Version", "ActualizadoEnUtc", "UltimoDiaProcesado", "TenantId")
                  VALUES (gen_random_uuid(), 1, now(), NULL, {TenantDeLaConexion})
                  ON CONFLICT ("TenantId") DO UPDATE
                      SET "Version" = "VersionesEstadoTenant"."Version" + 1, "ActualizadoEnUtc" = now()
                  """
                : """UPDATE "VersionesEstadoTenant" SET "Version" = "Version" + 1, "ActualizadoEnUtc" = now()""",
            ct);
    }

    /// <summary>
    /// Alcance de los pares (<see cref="CalculoEstadoMaterializado"/>): todos los de <c>CentrosConTodosLosPares</c>, y en los
    /// demás Centros del cálculo solo los de sus Trabajadores y Empresas.
    /// </summary>
    private async Task EscribirParesAsync(CalculoEstadoMaterializado calculo, CancellationToken ct)
    {
        var titulares = calculo.TrabajadorIds.Concat(calculo.EmpresaIds).Distinct().ToArray();
        if (calculo.Pares.Count == 0 && calculo.CentrosConTodosLosPares.Count == 0 && (calculo.CentroIds.Count == 0 || titulares.Length == 0))
            return;

        var p = calculo.Pares;
        await dbContext.Database.ExecuteSqlRawAsync(
            $"""
            WITH f AS (
                SELECT * FROM unnest(@c, @a, @t, @tipo, @doc, @est, @bloq, @cuenta, @plat, @ve, @pc)
                    AS f(c, a, t, tipo, doc, est, bloq, cuenta, plat, ve, pc)
            ),
            borradas AS (
                DELETE FROM "EstadosRequisito" r
                WHERE (r."CentroId" = ANY(@completos) OR (r."CentroId" = ANY(@centros) AND r."TitularId" = ANY(@titulares)))
                  AND NOT EXISTS (SELECT 1 FROM f WHERE f.c = r."CentroId" AND f.a = r."AmbitoTitular" AND f.t = r."TitularId"
                                                    AND f.tipo = r."TipoDocumentoId")
            ),
            cambiadas AS (
                UPDATE "EstadosRequisito" r
                SET "DocumentoEfectivoId" = f.doc, "EstadoTalveg" = f.est, "BloqueaAcceso" = f.bloq,
                    "CuentaParaCumplimiento" = f.cuenta, "EstadoPlataforma" = f.plat, "VencimientoEfectivo" = f.ve,
                    "ProximoCambioEl" = f.pc, "DiaCalculo" = @dia, "CalculadoEnUtc" = now(),
                    "VersionCalculo" = {CalculoEstadoMaterializado.VersionDeLaRegla}
                FROM f
                WHERE f.c = r."CentroId" AND f.a = r."AmbitoTitular" AND f.t = r."TitularId" AND f.tipo = r."TipoDocumentoId"
                  AND (r."DocumentoEfectivoId" IS DISTINCT FROM f.doc OR r."EstadoTalveg" <> f.est OR r."BloqueaAcceso" <> f.bloq
                       OR r."CuentaParaCumplimiento" <> f.cuenta OR r."EstadoPlataforma" IS DISTINCT FROM f.plat
                       OR r."VencimientoEfectivo" IS DISTINCT FROM f.ve OR r."ProximoCambioEl" IS DISTINCT FROM f.pc
                       OR r."DiaCalculo" <> @dia OR r."VersionCalculo" <> {CalculoEstadoMaterializado.VersionDeLaRegla})
            )
            INSERT INTO "EstadosRequisito" ("Id", "CentroId", "AmbitoTitular", "TitularId", "TipoDocumentoId",
                "DocumentoEfectivoId", "EstadoTalveg", "BloqueaAcceso", "CuentaParaCumplimiento", "EstadoPlataforma",
                "VencimientoEfectivo", "ProximoCambioEl", "DiaCalculo", "CalculadoEnUtc", "VersionCalculo", "TenantId")
            SELECT gen_random_uuid(), f.c, f.a, f.t, f.tipo, f.doc, f.est, f.bloq, f.cuenta, f.plat, f.ve, f.pc,
                @dia, now(), {CalculoEstadoMaterializado.VersionDeLaRegla}, {TenantDeLaConexion}
            FROM f
            WHERE NOT EXISTS (SELECT 1 FROM "EstadosRequisito" r
                              WHERE r."CentroId" = f.c AND r."AmbitoTitular" = f.a AND r."TitularId" = f.t
                                AND r."TipoDocumentoId" = f.tipo)
            """,
            [
                Parametro("dia", NpgsqlDbType.Date, calculo.Dia),
                Lista("completos", NpgsqlDbType.Uuid, calculo.CentrosConTodosLosPares.ToArray()),
                Lista("centros", NpgsqlDbType.Uuid, calculo.CentroIds.ToArray()),
                Lista("titulares", NpgsqlDbType.Uuid, titulares),
                Lista("c", NpgsqlDbType.Uuid, p.Select(x => x.CentroId).ToArray()),
                Lista("a", NpgsqlDbType.Smallint, p.Select(x => (short)x.AmbitoTitular).ToArray()),
                Lista("t", NpgsqlDbType.Uuid, p.Select(x => x.TitularId).ToArray()),
                Lista("tipo", NpgsqlDbType.Uuid, p.Select(x => x.TipoDocumentoId).ToArray()),
                Lista("doc", NpgsqlDbType.Uuid, p.Select(x => x.DocumentoEfectivoId).ToArray()),
                Lista("est", NpgsqlDbType.Smallint, p.Select(x => (short)x.EstadoTalveg).ToArray()),
                Lista("bloq", NpgsqlDbType.Boolean, p.Select(x => x.BloqueaAcceso).ToArray()),
                Lista("cuenta", NpgsqlDbType.Boolean, p.Select(x => x.CuentaParaCumplimiento).ToArray()),
                Lista("plat", NpgsqlDbType.Smallint, p.Select(x => x.EstadoPlataforma is { } e ? (short?)e : null).ToArray()),
                Lista("ve", NpgsqlDbType.Date, p.Select(x => x.VencimientoEfectivo).ToArray()),
                Lista("pc", NpgsqlDbType.Date, p.Select(x => x.ProximoCambioEl).ToArray()),
            ],
            ct);
    }

    /// <summary>Alcance: todas las acreditaciones de los Centros del cálculo.</summary>
    private async Task EscribirPlataformaAsync(CalculoEstadoMaterializado calculo, CancellationToken ct)
    {
        if (calculo.CentroIds.Count == 0 && calculo.Plataforma.Count == 0)
            return;

        var p = calculo.Plataforma;
        await dbContext.Database.ExecuteSqlRawAsync(
            $"""
            WITH f AS (
                SELECT * FROM unnest(@c, @canal, @a, @t, @tipo, @est, @vig, @fv, @pc) AS f(c, canal, a, t, tipo, est, vig, fv, pc)
            ),
            borradas AS (
                DELETE FROM "EstadosPlataforma" r
                WHERE r."CentroId" = ANY(@centros)
                  AND NOT EXISTS (SELECT 1 FROM f WHERE f.c = r."CentroId" AND f.canal = r."CanalGestionDocumentalId"
                                                    AND f.a = r."AmbitoTitular" AND f.t = r."TitularId" AND f.tipo = r."TipoDocumentoId")
            ),
            cambiadas AS (
                UPDATE "EstadosPlataforma" r
                SET "Estado" = f.est, "EstadoVigencia" = f.vig, "FechaVencimientoEnPlataforma" = f.fv, "ProximoCambioEl" = f.pc,
                    "DiaCalculo" = @dia, "CalculadoEnUtc" = now(), "VersionCalculo" = {CalculoEstadoMaterializado.VersionDeLaRegla}
                FROM f
                WHERE f.c = r."CentroId" AND f.canal = r."CanalGestionDocumentalId" AND f.a = r."AmbitoTitular"
                  AND f.t = r."TitularId" AND f.tipo = r."TipoDocumentoId"
                  AND (r."Estado" <> f.est OR r."EstadoVigencia" <> f.vig
                       OR r."FechaVencimientoEnPlataforma" IS DISTINCT FROM f.fv OR r."ProximoCambioEl" IS DISTINCT FROM f.pc
                       OR r."DiaCalculo" <> @dia OR r."VersionCalculo" <> {CalculoEstadoMaterializado.VersionDeLaRegla})
            )
            INSERT INTO "EstadosPlataforma" ("Id", "CentroId", "CanalGestionDocumentalId", "AmbitoTitular", "TitularId",
                "TipoDocumentoId", "Estado", "EstadoVigencia", "FechaVencimientoEnPlataforma", "ProximoCambioEl",
                "DiaCalculo", "CalculadoEnUtc", "VersionCalculo", "TenantId")
            SELECT gen_random_uuid(), f.c, f.canal, f.a, f.t, f.tipo, f.est, f.vig, f.fv, f.pc,
                @dia, now(), {CalculoEstadoMaterializado.VersionDeLaRegla}, {TenantDeLaConexion}
            FROM f
            WHERE NOT EXISTS (SELECT 1 FROM "EstadosPlataforma" r
                              WHERE r."CentroId" = f.c AND r."CanalGestionDocumentalId" = f.canal AND r."AmbitoTitular" = f.a
                                AND r."TitularId" = f.t AND r."TipoDocumentoId" = f.tipo)
            """,
            [
                Parametro("dia", NpgsqlDbType.Date, calculo.Dia),
                Lista("centros", NpgsqlDbType.Uuid, calculo.CentroIds.ToArray()),
                Lista("c", NpgsqlDbType.Uuid, p.Select(x => x.CentroId).ToArray()),
                Lista("canal", NpgsqlDbType.Uuid, p.Select(x => x.CanalGestionDocumentalId).ToArray()),
                Lista("a", NpgsqlDbType.Smallint, p.Select(x => (short)x.AmbitoTitular).ToArray()),
                Lista("t", NpgsqlDbType.Uuid, p.Select(x => x.TitularId).ToArray()),
                Lista("tipo", NpgsqlDbType.Uuid, p.Select(x => x.TipoDocumentoId).ToArray()),
                Lista("est", NpgsqlDbType.Smallint, p.Select(x => (short)x.Estado).ToArray()),
                Lista("vig", NpgsqlDbType.Smallint, p.Select(x => (short)x.EstadoVigencia).ToArray()),
                Lista("fv", NpgsqlDbType.Date, p.Select(x => x.FechaVencimientoEnPlataforma).ToArray()),
                Lista("pc", NpgsqlDbType.Date, p.Select(x => x.ProximoCambioEl).ToArray()),
            ],
            ct);
    }

    /// <summary>Alcance: la fila de cada Centro, Trabajador y Empresa del cálculo, exista o no la entidad.</summary>
    private async Task EscribirEntidadesAsync(CalculoEstadoMaterializado calculo, CancellationToken ct)
    {
        if (calculo.CentroIds.Count == 0 && calculo.TrabajadorIds.Count == 0 && calculo.EmpresaIds.Count == 0 && calculo.Entidades.Count == 0)
            return;

        var e = calculo.Entidades;
        await dbContext.Database.ExecuteSqlRawAsync(
            $"""
            WITH f AS (
                SELECT * FROM unnest(@amb, @id, @est, @plat, @v, @vsb, @u, @p, @fa, @sc, @pp, @req, @ald, @pc)
                    AS f(amb, id, est, plat, v, vsb, u, p, fa, sc, pp, req, ald, pc)
            ),
            borradas AS (
                DELETE FROM "EstadosEntidad" r
                WHERE ((r."Ambito" = @ambCentro AND r."EntidadId" = ANY(@centros))
                       OR (r."Ambito" = @ambTrabajador AND r."EntidadId" = ANY(@trabajadores))
                       OR (r."Ambito" = @ambEmpresa AND r."EntidadId" = ANY(@empresas)))
                  AND NOT EXISTS (SELECT 1 FROM f WHERE f.amb = r."Ambito" AND f.id = r."EntidadId")
            ),
            cambiadas AS (
                UPDATE "EstadosEntidad" r
                SET "Estado" = f.est, "EstadoPlataforma" = f.plat, "Vencidos" = f.v, "VencidosSinBloqueo" = f.vsb,
                    "Urgentes" = f.u, "Proximos" = f.p, "Faltan" = f.fa, "SinConfirmar" = f.sc, "PendientesEnPlataforma" = f.pp,
                    "Requeridos" = f.req, "AlDia" = f.ald, "ProximoCambioEl" = f.pc, "DiaCalculo" = @dia, "CalculadoEnUtc" = now(),
                    "VersionCalculo" = {CalculoEstadoMaterializado.VersionDeLaRegla}
                FROM f
                WHERE f.amb = r."Ambito" AND f.id = r."EntidadId"
                  AND (r."Estado" <> f.est OR r."EstadoPlataforma" IS DISTINCT FROM f.plat OR r."Vencidos" <> f.v
                       OR r."VencidosSinBloqueo" <> f.vsb OR r."Urgentes" <> f.u OR r."Proximos" <> f.p OR r."Faltan" <> f.fa
                       OR r."SinConfirmar" <> f.sc OR r."PendientesEnPlataforma" <> f.pp OR r."Requeridos" <> f.req
                       OR r."AlDia" <> f.ald OR r."ProximoCambioEl" IS DISTINCT FROM f.pc OR r."DiaCalculo" <> @dia
                       OR r."VersionCalculo" <> {CalculoEstadoMaterializado.VersionDeLaRegla})
            )
            INSERT INTO "EstadosEntidad" ("Id", "Ambito", "EntidadId", "Estado", "EstadoPlataforma", "Vencidos",
                "VencidosSinBloqueo", "Urgentes", "Proximos", "Faltan", "SinConfirmar", "PendientesEnPlataforma",
                "Requeridos", "AlDia", "ProximoCambioEl", "DiaCalculo", "CalculadoEnUtc", "VersionCalculo", "TenantId")
            SELECT gen_random_uuid(), f.amb, f.id, f.est, f.plat, f.v, f.vsb, f.u, f.p, f.fa, f.sc, f.pp, f.req, f.ald, f.pc,
                @dia, now(), {CalculoEstadoMaterializado.VersionDeLaRegla}, {TenantDeLaConexion}
            FROM f
            WHERE NOT EXISTS (SELECT 1 FROM "EstadosEntidad" r WHERE r."Ambito" = f.amb AND r."EntidadId" = f.id)
            """,
            [
                Parametro("dia", NpgsqlDbType.Date, calculo.Dia),
                Parametro("ambCentro", NpgsqlDbType.Smallint, (short)AmbitoEstadoEntidad.Centro),
                Parametro("ambTrabajador", NpgsqlDbType.Smallint, (short)AmbitoEstadoEntidad.Trabajador),
                Parametro("ambEmpresa", NpgsqlDbType.Smallint, (short)AmbitoEstadoEntidad.Empresa),
                Lista("centros", NpgsqlDbType.Uuid, calculo.CentroIds.ToArray()),
                Lista("trabajadores", NpgsqlDbType.Uuid, calculo.TrabajadorIds.ToArray()),
                Lista("empresas", NpgsqlDbType.Uuid, calculo.EmpresaIds.ToArray()),
                Lista("amb", NpgsqlDbType.Smallint, e.Select(x => (short)x.Ambito).ToArray()),
                Lista("id", NpgsqlDbType.Uuid, e.Select(x => x.EntidadId).ToArray()),
                Lista("est", NpgsqlDbType.Smallint, e.Select(x => x.Estado).ToArray()),
                Lista("plat", NpgsqlDbType.Smallint, e.Select(x => x.EstadoPlataforma is { } s ? (short?)s : null).ToArray()),
                Lista("v", NpgsqlDbType.Integer, e.Select(x => x.Vencidos).ToArray()),
                Lista("vsb", NpgsqlDbType.Integer, e.Select(x => x.VencidosSinBloqueo).ToArray()),
                Lista("u", NpgsqlDbType.Integer, e.Select(x => x.Urgentes).ToArray()),
                Lista("p", NpgsqlDbType.Integer, e.Select(x => x.Proximos).ToArray()),
                Lista("fa", NpgsqlDbType.Integer, e.Select(x => x.Faltan).ToArray()),
                Lista("sc", NpgsqlDbType.Integer, e.Select(x => x.SinConfirmar).ToArray()),
                Lista("pp", NpgsqlDbType.Integer, e.Select(x => x.PendientesEnPlataforma).ToArray()),
                Lista("req", NpgsqlDbType.Integer, e.Select(x => x.Requeridos).ToArray()),
                Lista("ald", NpgsqlDbType.Integer, e.Select(x => x.AlDia).ToArray()),
                Lista("pc", NpgsqlDbType.Date, e.Select(x => x.ProximoCambioEl).ToArray()),
            ],
            ct);
    }

    public async Task<IReadOnlyList<Guid>> CentrosConFilasDeTitularesAsync(IReadOnlyCollection<Guid> titularIds, CancellationToken cancellationToken)
    {
        if (titularIds.Count == 0)
            return [];

        var ids = titularIds.Distinct().ToList();
        return await dbContext.Set<EstadoRequisito>().AsNoTracking()
            .Where(p => ids.Contains(p.TitularId))
            .Select(p => p.CentroId)
            .Union(dbContext.Set<EstadoEnPlataforma>().AsNoTracking()
                .Where(p => ids.Contains(p.TitularId))
                .Select(p => p.CentroId))
            .ToListAsync(cancellationToken);
    }

    private static NpgsqlParameter Parametro<T>(string nombre, NpgsqlDbType tipo, T valor) =>
        new(nombre, tipo) { Value = valor is null ? DBNull.Value : valor };

    private static NpgsqlParameter Lista<T>(string nombre, NpgsqlDbType tipo, T[] valores) =>
        new(nombre, NpgsqlDbType.Array | tipo) { Value = valores };
}
