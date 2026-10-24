using CaeManager.Application.EstadoMaterializado;
using CaeManager.Domain.EstadoMaterializado;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace CaeManager.Infrastructure.EstadoMaterializado;

/// <inheritdoc cref="IEscritorEstadoMaterializado"/>
/// <remarks>
/// SQL por lotes en vez del rastreador de EF: un <c>Update()</c> completo choca con el permiso de UPDATE por columnas
/// (42501), y estas filas no son datos de negocio que deban pasar por la auditoría de cambios. El <c>Id</c> se genera en la
/// base y el <c>TenantId</c> sale del contexto RLS de la conexión (<c>app.tenant_id</c>); la política RLS con
/// <c>WITH CHECK</c> rechaza cualquier otro. Sin contexto de Tenant, el <c>NOT NULL</c> de <c>TenantId</c> hace fallar la
/// escritura en vez de escribir filas huérfanas.
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
        if (calculo.CentroIds.Count > 0)
        {
            var centros = Lista("centros", NpgsqlDbType.Uuid, calculo.CentroIds.ToArray());
            await dbContext.Database.ExecuteSqlRawAsync(
                """DELETE FROM "EstadosRequisito" WHERE "CentroId" = ANY(@centros)""", [centros], ct);
            await dbContext.Database.ExecuteSqlRawAsync(
                """DELETE FROM "EstadosPlataforma" WHERE "CentroId" = ANY(@centros)""",
                [Lista("centros", NpgsqlDbType.Uuid, calculo.CentroIds.ToArray())], ct);
        }

        await BorrarEntidadesAsync(AmbitoEstadoEntidad.Centro, calculo.CentroIds, ct);
        await BorrarEntidadesAsync(AmbitoEstadoEntidad.Trabajador, calculo.TrabajadorIds, ct);
        await BorrarEntidadesAsync(AmbitoEstadoEntidad.Empresa, calculo.EmpresaIds, ct);

        if (calculo.Pares.Count > 0)
        {
            var p = calculo.Pares;
            await dbContext.Database.ExecuteSqlRawAsync(
                $"""
                INSERT INTO "EstadosRequisito" ("Id", "CentroId", "AmbitoTitular", "TitularId", "TipoDocumentoId",
                    "DocumentoEfectivoId", "EstadoTalveg", "BloqueaAcceso", "CuentaParaCumplimiento", "EstadoPlataforma",
                    "VencimientoEfectivo", "ProximoCambioEl", "DiaCalculo", "CalculadoEnUtc", "VersionCalculo", "TenantId")
                SELECT gen_random_uuid(), f.c, f.a, f.t, f.tipo, f.doc, f.est, f.bloq, f.cuenta, f.plat, f.ve, f.pc,
                    @dia, now(), {CalculoEstadoMaterializado.VersionDeLaRegla}, {TenantDeLaConexion}
                FROM unnest(@c, @a, @t, @tipo, @doc, @est, @bloq, @cuenta, @plat, @ve, @pc)
                    AS f(c, a, t, tipo, doc, est, bloq, cuenta, plat, ve, pc)
                """,
                [
                    Parametro("dia", NpgsqlDbType.Date, calculo.Dia),
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

        if (calculo.Plataforma.Count > 0)
        {
            var p = calculo.Plataforma;
            await dbContext.Database.ExecuteSqlRawAsync(
                $"""
                INSERT INTO "EstadosPlataforma" ("Id", "CentroId", "CanalGestionDocumentalId", "AmbitoTitular", "TitularId",
                    "TipoDocumentoId", "Estado", "EstadoVigencia", "FechaVencimientoEnPlataforma", "ProximoCambioEl",
                    "DiaCalculo", "CalculadoEnUtc", "VersionCalculo", "TenantId")
                SELECT gen_random_uuid(), f.c, f.canal, f.a, f.t, f.tipo, f.est, f.vig, f.fv, f.pc,
                    @dia, now(), {CalculoEstadoMaterializado.VersionDeLaRegla}, {TenantDeLaConexion}
                FROM unnest(@c, @canal, @a, @t, @tipo, @est, @vig, @fv, @pc) AS f(c, canal, a, t, tipo, est, vig, fv, pc)
                """,
                [
                    Parametro("dia", NpgsqlDbType.Date, calculo.Dia),
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

        if (calculo.Entidades.Count > 0)
        {
            var e = calculo.Entidades;
            await dbContext.Database.ExecuteSqlRawAsync(
                $"""
                INSERT INTO "EstadosEntidad" ("Id", "Ambito", "EntidadId", "Estado", "EstadoPlataforma", "Vencidos",
                    "VencidosSinBloqueo", "Urgentes", "Proximos", "Faltan", "SinConfirmar", "PendientesEnPlataforma",
                    "Requeridos", "AlDia", "ProximoCambioEl", "DiaCalculo", "CalculadoEnUtc", "VersionCalculo", "TenantId")
                SELECT gen_random_uuid(), f.amb, f.id, f.est, f.plat, f.v, f.vsb, f.u, f.p, f.fa, f.sc, f.pp, f.req, f.ald, f.pc,
                    @dia, now(), {CalculoEstadoMaterializado.VersionDeLaRegla}, {TenantDeLaConexion}
                FROM unnest(@amb, @id, @est, @plat, @v, @vsb, @u, @p, @fa, @sc, @pp, @req, @ald, @pc)
                    AS f(amb, id, est, plat, v, vsb, u, p, fa, sc, pp, req, ald, pc)
                """,
                [
                    Parametro("dia", NpgsqlDbType.Date, calculo.Dia),
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

    private Task BorrarEntidadesAsync(AmbitoEstadoEntidad ambito, IReadOnlyList<Guid> ids, CancellationToken ct) =>
        ids.Count == 0
            ? Task.CompletedTask
            : dbContext.Database.ExecuteSqlRawAsync(
                """DELETE FROM "EstadosEntidad" WHERE "Ambito" = @ambito AND "EntidadId" = ANY(@ids)""",
                [Parametro("ambito", NpgsqlDbType.Smallint, (short)ambito), Lista("ids", NpgsqlDbType.Uuid, ids.ToArray())],
                ct);

    private static NpgsqlParameter Parametro<T>(string nombre, NpgsqlDbType tipo, T valor) =>
        new(nombre, tipo) { Value = valor is null ? DBNull.Value : valor };

    private static NpgsqlParameter Lista<T>(string nombre, NpgsqlDbType tipo, T[] valores) =>
        new(nombre, NpgsqlDbType.Array | tipo) { Value = valores };
}
