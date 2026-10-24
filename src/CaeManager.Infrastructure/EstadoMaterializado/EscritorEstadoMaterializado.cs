using CaeManager.Application.EstadoMaterializado;
using CaeManager.Application.EstadoMaterializado.Frescura;
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
public sealed class EscritorEstadoMaterializado(CaeManagerDbContext dbContext, IRegistroCambiosEstado registroCambios) : IEscritorEstadoMaterializado
{
    private const string TenantDeLaConexion = "NULLIF(current_setting('app.tenant_id', true), '')::uuid";

    /// <summary>Cierre de cada sentencia de escritura: cuántas filas borró, cambió o añadió.</summary>
    private const string SumaDeCambios =
        """SELECT ((SELECT count(*) FROM borradas) + (SELECT count(*) FROM cambiadas) + (SELECT count(*) FROM insertadas))::int AS "Value" """;

    public async Task<int> EscribirAsync(CalculoEstadoMaterializado calculo, bool crearVersionSiFalta, CancellationToken cancellationToken) =>
        await EnTransaccionAsync(async () =>
        {
            var cambiadas = await EscribirFilasEnTransaccionAsync(calculo, cancellationToken);
            await SubirVersionEnTransaccionAsync(crearVersionSiFalta, huboCambios: cambiadas > 0, EntidadesDelAlcance(calculo), cancellationToken);
            return cambiadas;
        }, cancellationToken);

    public Task<int> EscribirFilasAsync(CalculoEstadoMaterializado calculo, CancellationToken cancellationToken) =>
        EnTransaccionAsync(() => EscribirFilasEnTransaccionAsync(calculo, cancellationToken), cancellationToken);

    // El final del relleno: los lotes y la purga de huérfanos tocaron un número de entidades sin cota, así que el aviso va sin
    // entidades. La caché lo toma como «no dice qué cambió» y relee entero; el circuito repinta igual.
    public Task SubirVersionAsync(bool crearSiFalta, bool huboCambios, CancellationToken cancellationToken) =>
        EnTransaccionAsync(async () =>
        {
            await SubirVersionEnTransaccionAsync(crearSiFalta, huboCambios, [], cancellationToken);
            return 0;
        }, cancellationToken);

    /// <summary>
    /// Todas las entidades cuyas filas puede haber escrito (borrado, cambiado o añadido) el cálculo: las de su alcance (diseño
    /// § 13.11, contrato de <see cref="IRegistroCambiosEstado"/>). Los pares y las acreditaciones solo se escriben en Centros del
    /// alcance y con titulares del cálculo o de un Centro entero del alcance; se añaden sus titulares para que una lectura que
    /// dependa del titular también los vea. Más de las que cambiaron no es un error: la caché relee alguna fila de más.
    /// </summary>
    private static IReadOnlyCollection<Guid> EntidadesDelAlcance(CalculoEstadoMaterializado calculo) =>
        calculo.CentroIds
            .Concat(calculo.CentrosConTodosLosPares)
            .Concat(calculo.TrabajadorIds)
            .Concat(calculo.EmpresaIds)
            .Concat(calculo.Pares.Select(p => p.TitularId))
            .Concat(calculo.Plataforma.Select(p => p.TitularId))
            .ToHashSet();

    public async Task<T> EnTransaccionConTenantExclusivoAsync<T>(Func<Task<T>> trabajo, CancellationToken cancellationToken) =>
        await EnTransaccionAsync(async () =>
        {
            // Sin Tenant en la conexión no hay nada que rellenar (y el NOT NULL de TenantId haría fallar la escritura).
            var tenant = await CerrojosEstadoMaterializado.TenantDeLaConexionAsync(dbContext)
                         ?? throw new InvalidOperationException("El relleno del estado materializado necesita el contexto RLS de un Tenant.");
            var anterior = await CerrojosEstadoMaterializado.FijarTopeDeEsperaAsync(dbContext, TopeDeEsperaDelCerrojoExclusivo);
            await CerrojosEstadoMaterializado.BloquearAsync(dbContext, [CerrojosEstadoMaterializado.ClaveDeTenant(tenant)], compartido: false);
            await CerrojosEstadoMaterializado.FijarTopeDeEsperaAsync(dbContext, anterior);
            return await trabajo();
        }, cancellationToken);

    /// <summary>
    /// Espera máxima por el cerrojo exclusivo del Tenant en el relleno: las acciones en línea lo tienen compartido como mucho
    /// el tope de su recálculo; si no llega, el relleno falla y se reintenta en la pasada siguiente.
    /// </summary>
    public const string TopeDeEsperaDelCerrojoExclusivo = "15s";

    /// <summary>En la transacción abierta (la de la acción, la de la cola o la del relleno) o en una propia.</summary>
    private async Task<T> EnTransaccionAsync<T>(Func<Task<T>> trabajo, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
            return await trabajo();

        var estrategia = dbContext.Database.CreateExecutionStrategy();
        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var resultado = await trabajo();
            await transaccion.CommitAsync(cancellationToken);
            return resultado;
        });
    }

    private async Task<int> EscribirFilasEnTransaccionAsync(CalculoEstadoMaterializado calculo, CancellationToken ct) =>
        await EscribirParesAsync(calculo, ct)
        + await EscribirPlataformaAsync(calculo, ct)
        + await EscribirEntidadesAsync(calculo, ct);

    /// <summary>
    /// La versión del Tenant: el relleno completo la crea; un recálculo parcial solo la sube si ya existe, para que los
    /// lectores no tomen por entero un Tenant a medio rellenar (diseño § 7.3). Solo sube si alguna fila cambió: un recálculo
    /// sin cambios no toca la fila caliente, así que dos recálculos de clausuras disjuntas del mismo Tenant no se esperan en
    /// ella (diseño § 4.4, M1).
    /// </summary>
    /// <remarks>
    /// Tras la subida, y dentro de la misma transacción, anota el aviso para los circuitos (diseño § 13.11) con la versión que
    /// devolvió esta misma sentencia (<c>RETURNING</c>, nunca una lectura aparte, que podría ver la de otra transacción). Sin
    /// fila devuelta (no se subió: un Tenant sin versión en un recálculo parcial, o la versión ya existía en el relleno sin
    /// cambios) no se anota. El aviso sale tras el <c>COMMIT</c> y solo si el Tenant coincide con el de la conexión
    /// (<c>AvisoEstadoTrasConfirmarInterceptor</c>).
    /// </remarks>
    private async Task SubirVersionEnTransaccionAsync(
        bool crearSiFalta, bool huboCambios, IReadOnlyCollection<Guid> entidades, CancellationToken ct)
    {
        if (!crearSiFalta && !huboCambios)
            return;

        // Como SumaDeCambios: la sentencia que escribe va en un CTE y la consulta de fuera es un SELECT, sin componer.
        var subida = (crearSiFalta, huboCambios) switch
        {
            (true, true) => $"""
                INSERT INTO "VersionesEstadoTenant" ("Id", "Version", "ActualizadoEnUtc", "UltimoDiaProcesado", "TenantId")
                VALUES (gen_random_uuid(), 1, now(), NULL, {TenantDeLaConexion})
                ON CONFLICT ("TenantId") DO UPDATE
                    SET "Version" = "VersionesEstadoTenant"."Version" + 1, "ActualizadoEnUtc" = now()
                """,
            (true, false) => $"""
                INSERT INTO "VersionesEstadoTenant" ("Id", "Version", "ActualizadoEnUtc", "UltimoDiaProcesado", "TenantId")
                VALUES (gen_random_uuid(), 1, now(), NULL, {TenantDeLaConexion})
                ON CONFLICT ("TenantId") DO NOTHING
                """,
            _ => $"""
                UPDATE "VersionesEstadoTenant" SET "Version" = "Version" + 1, "ActualizadoEnUtc" = now()
                WHERE "TenantId" = {TenantDeLaConexion}
                """,
        };
        var sql = $"""
            WITH s AS ({subida}
                RETURNING "TenantId", "Version")
            SELECT "TenantId", "Version" FROM s
            """;
        var subidas = await dbContext.Database.SqlQueryRaw<VersionSubida>(sql).ToListAsync(ct);

        foreach (var fila in subidas)
            registroCambios.Anotar(new AvisoCambioEstado(fila.TenantId, fila.Version, entidades));
    }

    /// <summary>Fila que devuelve la subida de versión.</summary>
    private sealed class VersionSubida
    {
        public Guid TenantId { get; set; }

        public long Version { get; set; }
    }

    public Task<int> PurgarHuerfanosAsync(
        IReadOnlyCollection<Guid> centroIds, IReadOnlyCollection<Guid> trabajadorIds, IReadOnlyCollection<Guid> empresaIds,
        CancellationToken cancellationToken) =>
        EnTransaccionAsync(() => ContarAsync(
            $"""
            WITH centros AS (SELECT unnest(@centros) AS id),
            trabajadores AS (SELECT unnest(@trabajadores) AS id),
            empresas AS (SELECT unnest(@empresas) AS id),
            pares AS (
                DELETE FROM "EstadosRequisito" r
                WHERE r."TenantId" = {TenantDeLaConexion} AND NOT EXISTS (SELECT 1 FROM centros WHERE centros.id = r."CentroId")
                RETURNING 1
            ),
            plataforma AS (
                DELETE FROM "EstadosPlataforma" r
                WHERE r."TenantId" = {TenantDeLaConexion} AND NOT EXISTS (SELECT 1 FROM centros WHERE centros.id = r."CentroId")
                RETURNING 1
            ),
            entidades AS (
                DELETE FROM "EstadosEntidad" r
                WHERE r."TenantId" = {TenantDeLaConexion}
                  AND NOT ((r."Ambito" = @ambCentro AND EXISTS (SELECT 1 FROM centros WHERE centros.id = r."EntidadId"))
                        OR (r."Ambito" = @ambTrabajador AND EXISTS (SELECT 1 FROM trabajadores WHERE trabajadores.id = r."EntidadId"))
                        OR (r."Ambito" = @ambEmpresa AND EXISTS (SELECT 1 FROM empresas WHERE empresas.id = r."EntidadId")))
                RETURNING 1
            )
            SELECT ((SELECT count(*) FROM pares) + (SELECT count(*) FROM plataforma) + (SELECT count(*) FROM entidades))::int AS "Value"
            """,
            [
                Parametro("ambCentro", NpgsqlDbType.Smallint, (short)AmbitoEstadoEntidad.Centro),
                Parametro("ambTrabajador", NpgsqlDbType.Smallint, (short)AmbitoEstadoEntidad.Trabajador),
                Parametro("ambEmpresa", NpgsqlDbType.Smallint, (short)AmbitoEstadoEntidad.Empresa),
                Lista("centros", NpgsqlDbType.Uuid, centroIds.Distinct().ToArray()),
                Lista("trabajadores", NpgsqlDbType.Uuid, trabajadorIds.Distinct().ToArray()),
                Lista("empresas", NpgsqlDbType.Uuid, empresaIds.Distinct().ToArray()),
            ],
            cancellationToken), cancellationToken);

    /// <summary>Una sentencia de escritura con CTE que termina en el recuento de filas que cambió.</summary>
    private async Task<int> ContarAsync(string sql, NpgsqlParameter[] parametros, CancellationToken ct) =>
        (await dbContext.Database.SqlQueryRaw<int>(sql, parametros).ToListAsync(ct)).Single();

    /// <summary>
    /// Alcance de los pares (<see cref="CalculoEstadoMaterializado"/>): todos los de <c>CentrosConTodosLosPares</c>, y en los
    /// demás Centros del cálculo solo los de sus Trabajadores y Empresas.
    /// </summary>
    private async Task<int> EscribirParesAsync(CalculoEstadoMaterializado calculo, CancellationToken ct)
    {
        var titulares = calculo.TrabajadorIds.Concat(calculo.EmpresaIds).Distinct().ToArray();
        if (calculo.Pares.Count == 0 && calculo.CentrosConTodosLosPares.Count == 0 && (calculo.CentroIds.Count == 0 || titulares.Length == 0))
            return 0;

        var p = calculo.Pares;
        return await ContarAsync(
            $"""
            WITH f AS (
                SELECT * FROM unnest(@c, @a, @t, @tipo, @doc, @est, @bloq, @cuenta, @plat, @ve, @pc)
                    AS f(c, a, t, tipo, doc, est, bloq, cuenta, plat, ve, pc)
            ),
            borradas AS (
                DELETE FROM "EstadosRequisito" r
                WHERE r."TenantId" = {TenantDeLaConexion} AND (r."CentroId" = ANY(@completos) OR (r."CentroId" = ANY(@centros) AND r."TitularId" = ANY(@titulares)))
                  AND NOT EXISTS (SELECT 1 FROM f WHERE r."TenantId" = {TenantDeLaConexion} AND f.c = r."CentroId" AND f.a = r."AmbitoTitular" AND f.t = r."TitularId"
                                                    AND f.tipo = r."TipoDocumentoId")
                RETURNING 1
            ),
            cambiadas AS (
                UPDATE "EstadosRequisito" r
                SET "DocumentoEfectivoId" = f.doc, "EstadoTalveg" = f.est, "BloqueaAcceso" = f.bloq,
                    "CuentaParaCumplimiento" = f.cuenta, "EstadoPlataforma" = f.plat, "VencimientoEfectivo" = f.ve,
                    "ProximoCambioEl" = f.pc, "DiaCalculo" = @dia, "CalculadoEnUtc" = now(),
                    "VersionCalculo" = {CalculoEstadoMaterializado.VersionDeLaRegla}
                FROM f
                WHERE r."TenantId" = {TenantDeLaConexion} AND f.c = r."CentroId" AND f.a = r."AmbitoTitular" AND f.t = r."TitularId" AND f.tipo = r."TipoDocumentoId"
                  AND (r."DocumentoEfectivoId" IS DISTINCT FROM f.doc OR r."EstadoTalveg" <> f.est OR r."BloqueaAcceso" <> f.bloq
                       OR r."CuentaParaCumplimiento" <> f.cuenta OR r."EstadoPlataforma" IS DISTINCT FROM f.plat
                       OR r."VencimientoEfectivo" IS DISTINCT FROM f.ve OR r."ProximoCambioEl" IS DISTINCT FROM f.pc
                       OR r."DiaCalculo" <> @dia OR r."VersionCalculo" <> {CalculoEstadoMaterializado.VersionDeLaRegla})
                RETURNING 1
            ),
            insertadas AS (
                INSERT INTO "EstadosRequisito" ("Id", "CentroId", "AmbitoTitular", "TitularId", "TipoDocumentoId",
                    "DocumentoEfectivoId", "EstadoTalveg", "BloqueaAcceso", "CuentaParaCumplimiento", "EstadoPlataforma",
                    "VencimientoEfectivo", "ProximoCambioEl", "DiaCalculo", "CalculadoEnUtc", "VersionCalculo", "TenantId")
                SELECT gen_random_uuid(), f.c, f.a, f.t, f.tipo, f.doc, f.est, f.bloq, f.cuenta, f.plat, f.ve, f.pc,
                    @dia, now(), {CalculoEstadoMaterializado.VersionDeLaRegla}, {TenantDeLaConexion}
                FROM f
                WHERE NOT EXISTS (SELECT 1 FROM "EstadosRequisito" r
                                  WHERE r."TenantId" = {TenantDeLaConexion} AND r."CentroId" = f.c AND r."AmbitoTitular" = f.a AND r."TitularId" = f.t
                                    AND r."TipoDocumentoId" = f.tipo)
                RETURNING 1
            )
            {SumaDeCambios}
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
    private async Task<int> EscribirPlataformaAsync(CalculoEstadoMaterializado calculo, CancellationToken ct)
    {
        if (calculo.CentroIds.Count == 0 && calculo.Plataforma.Count == 0)
            return 0;

        var p = calculo.Plataforma;
        return await ContarAsync(
            $"""
            WITH f AS (
                SELECT * FROM unnest(@c, @canal, @a, @t, @tipo, @est, @vig, @fv, @pc) AS f(c, canal, a, t, tipo, est, vig, fv, pc)
            ),
            borradas AS (
                DELETE FROM "EstadosPlataforma" r
                WHERE r."TenantId" = {TenantDeLaConexion} AND r."CentroId" = ANY(@centros)
                  AND NOT EXISTS (SELECT 1 FROM f WHERE r."TenantId" = {TenantDeLaConexion} AND f.c = r."CentroId" AND f.canal = r."CanalGestionDocumentalId"
                                                    AND f.a = r."AmbitoTitular" AND f.t = r."TitularId" AND f.tipo = r."TipoDocumentoId")
                RETURNING 1
            ),
            cambiadas AS (
                UPDATE "EstadosPlataforma" r
                SET "Estado" = f.est, "EstadoVigencia" = f.vig, "FechaVencimientoEnPlataforma" = f.fv, "ProximoCambioEl" = f.pc,
                    "DiaCalculo" = @dia, "CalculadoEnUtc" = now(), "VersionCalculo" = {CalculoEstadoMaterializado.VersionDeLaRegla}
                FROM f
                WHERE r."TenantId" = {TenantDeLaConexion} AND f.c = r."CentroId" AND f.canal = r."CanalGestionDocumentalId" AND f.a = r."AmbitoTitular"
                  AND f.t = r."TitularId" AND f.tipo = r."TipoDocumentoId"
                  AND (r."Estado" <> f.est OR r."EstadoVigencia" <> f.vig
                       OR r."FechaVencimientoEnPlataforma" IS DISTINCT FROM f.fv OR r."ProximoCambioEl" IS DISTINCT FROM f.pc
                       OR r."DiaCalculo" <> @dia OR r."VersionCalculo" <> {CalculoEstadoMaterializado.VersionDeLaRegla})
                RETURNING 1
            ),
            insertadas AS (
                INSERT INTO "EstadosPlataforma" ("Id", "CentroId", "CanalGestionDocumentalId", "AmbitoTitular", "TitularId",
                    "TipoDocumentoId", "Estado", "EstadoVigencia", "FechaVencimientoEnPlataforma", "ProximoCambioEl",
                    "DiaCalculo", "CalculadoEnUtc", "VersionCalculo", "TenantId")
                SELECT gen_random_uuid(), f.c, f.canal, f.a, f.t, f.tipo, f.est, f.vig, f.fv, f.pc,
                    @dia, now(), {CalculoEstadoMaterializado.VersionDeLaRegla}, {TenantDeLaConexion}
                FROM f
                WHERE NOT EXISTS (SELECT 1 FROM "EstadosPlataforma" r
                                  WHERE r."TenantId" = {TenantDeLaConexion} AND r."CentroId" = f.c AND r."CanalGestionDocumentalId" = f.canal AND r."AmbitoTitular" = f.a
                                    AND r."TitularId" = f.t AND r."TipoDocumentoId" = f.tipo)
                RETURNING 1
            )
            {SumaDeCambios}
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
    private async Task<int> EscribirEntidadesAsync(CalculoEstadoMaterializado calculo, CancellationToken ct)
    {
        if (calculo.CentroIds.Count == 0 && calculo.TrabajadorIds.Count == 0 && calculo.EmpresaIds.Count == 0 && calculo.Entidades.Count == 0)
            return 0;

        var e = calculo.Entidades;
        return await ContarAsync(
            $"""
            WITH f AS (
                SELECT * FROM unnest(@amb, @id, @est, @plat, @v, @vsb, @u, @p, @fa, @sc, @pp, @req, @ald, @pc)
                    AS f(amb, id, est, plat, v, vsb, u, p, fa, sc, pp, req, ald, pc)
            ),
            borradas AS (
                DELETE FROM "EstadosEntidad" r
                WHERE r."TenantId" = {TenantDeLaConexion} AND ((r."Ambito" = @ambCentro AND r."EntidadId" = ANY(@centros))
                       OR (r."Ambito" = @ambTrabajador AND r."EntidadId" = ANY(@trabajadores))
                       OR (r."Ambito" = @ambEmpresa AND r."EntidadId" = ANY(@empresas)))
                  AND NOT EXISTS (SELECT 1 FROM f WHERE r."TenantId" = {TenantDeLaConexion} AND f.amb = r."Ambito" AND f.id = r."EntidadId")
                RETURNING 1
            ),
            cambiadas AS (
                UPDATE "EstadosEntidad" r
                SET "Estado" = f.est, "EstadoPlataforma" = f.plat, "Vencidos" = f.v, "VencidosSinBloqueo" = f.vsb,
                    "Urgentes" = f.u, "Proximos" = f.p, "Faltan" = f.fa, "SinConfirmar" = f.sc, "PendientesEnPlataforma" = f.pp,
                    "Requeridos" = f.req, "AlDia" = f.ald, "ProximoCambioEl" = f.pc, "DiaCalculo" = @dia, "CalculadoEnUtc" = now(),
                    "VersionCalculo" = {CalculoEstadoMaterializado.VersionDeLaRegla}
                FROM f
                WHERE r."TenantId" = {TenantDeLaConexion} AND f.amb = r."Ambito" AND f.id = r."EntidadId"
                  AND (r."Estado" <> f.est OR r."EstadoPlataforma" IS DISTINCT FROM f.plat OR r."Vencidos" <> f.v
                       OR r."VencidosSinBloqueo" <> f.vsb OR r."Urgentes" <> f.u OR r."Proximos" <> f.p OR r."Faltan" <> f.fa
                       OR r."SinConfirmar" <> f.sc OR r."PendientesEnPlataforma" <> f.pp OR r."Requeridos" <> f.req
                       OR r."AlDia" <> f.ald OR r."ProximoCambioEl" IS DISTINCT FROM f.pc OR r."DiaCalculo" <> @dia
                       OR r."VersionCalculo" <> {CalculoEstadoMaterializado.VersionDeLaRegla})
                RETURNING 1
            ),
            insertadas AS (
                INSERT INTO "EstadosEntidad" ("Id", "Ambito", "EntidadId", "Estado", "EstadoPlataforma", "Vencidos",
                    "VencidosSinBloqueo", "Urgentes", "Proximos", "Faltan", "SinConfirmar", "PendientesEnPlataforma",
                    "Requeridos", "AlDia", "ProximoCambioEl", "DiaCalculo", "CalculadoEnUtc", "VersionCalculo", "TenantId")
                SELECT gen_random_uuid(), f.amb, f.id, f.est, f.plat, f.v, f.vsb, f.u, f.p, f.fa, f.sc, f.pp, f.req, f.ald, f.pc,
                    @dia, now(), {CalculoEstadoMaterializado.VersionDeLaRegla}, {TenantDeLaConexion}
                FROM f
                WHERE NOT EXISTS (SELECT 1 FROM "EstadosEntidad" r WHERE r."TenantId" = {TenantDeLaConexion} AND r."Ambito" = f.amb AND r."EntidadId" = f.id)
                RETURNING 1
            )
            {SumaDeCambios}
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
