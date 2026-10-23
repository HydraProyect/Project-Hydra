using System.Text.Json;
using System.Text.RegularExpressions;
using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.BusquedaGlobal.Queries.BuscarGlobal;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionEmpresasDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerTrabajadoresAsignadosDeCentro;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Clientes.Queries.ObtenerEmpresasDeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerResumenCliente;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Empresas.Queries.BuscarEmpresaPorCif;
using CaeManager.Application.Empresas.Queries.ObtenerCumplimientoEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// La siembra del piloto y, al lado, una segunda organización con la misma estructura: un Operador CAE externo
/// con su cartera y sus Tenants beneficiarios, clonados con identificadores nuevos y los mismos CIF. Es la
/// versión pequeña, para CI, de la base de escala que se mide en local con 500.000 Documentos.
/// </summary>
public sealed class EscalaYAislamientoFixture : IAsyncLifetime
{
    /// <summary>Prefijo del nombre de los Tenants clonados y del correo de sus cuentas.</summary>
    internal const string PrefijoTenantOtra = "Otra organización · ";
    internal const string PrefijoCorreoOtra = "otra.";

    internal ArnesPilotoOutbound Arnes { get; private set; } = null!;
    internal IReadOnlyDictionary<string, Guid> TenantsPiloto { get; private set; } = null!;
    internal IReadOnlyDictionary<string, Guid> TenantsOtra { get; private set; } = null!;
    internal HashSet<Guid> IdsPiloto { get; private set; } = null!;
    internal HashSet<Guid> IdsOtra { get; private set; } = null!;
    internal string CorreoDireccion { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Arnes = await ArnesPilotoOutbound.CrearAsync();
        await Arnes.SembrarAsync(ArnesPilotoOutbound.Configurar(ArnesPilotoOutbound.FechaDemostracion()));
        await Arnes.BackfillAsync();

        var nombres = CatalogoPilotoOutbound.Tenants.Select(t => t.Nombre).ToArray();
        await using var conexion = new NpgsqlConnection(Arnes.CadenaPropietario);
        await conexion.OpenAsync();

        var operador = await Arnes.TenantIdAsync(CatalogoPilotoOutbound.NombreTenantOperador);
        TenantsPiloto = await TenantsPorNombreAsync(conexion, nombres);

        var errores = await OrganizacionClonada.ClonarAsync(conexion, operador, [.. TenantsPiloto.Values]);
        errores.Should().BeEmpty("la segunda organización se clona entera o el aislamiento se mediría sobre media estructura");

        TenantsOtra = (await TenantsPorNombreAsync(conexion, [.. nombres.Select(n => PrefijoTenantOtra + n)]))
            .ToDictionary(p => p.Key[PrefijoTenantOtra.Length..], p => p.Value);
        TenantsOtra.Keys.Should().BeEquivalentTo(TenantsPiloto.Keys);

        IdsPiloto = await IdsDeAsync(conexion, [operador, .. TenantsPiloto.Values]);
        IdsOtra = await IdsDeAsync(conexion, [await Arnes.TenantIdAsync(PrefijoTenantOtra + CatalogoPilotoOutbound.NombreTenantOperador), .. TenantsOtra.Values]);

        await using var comando = new NpgsqlCommand(
            """
            SELECT u."Email" FROM "AspNetUsers" u
            JOIN "AspNetUserRoles" ur ON ur."UserId" = u."Id" JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
            WHERE u."TenantId" = @operador AND r."Name" = 'DireccionCae' ORDER BY 1 LIMIT 1
            """, conexion);
        comando.Parameters.AddWithValue("operador", operador);
        CorreoDireccion = (string)(await comando.ExecuteScalarAsync())!;
    }

    public async Task DisposeAsync() => await Arnes.DisposeAsync();

    private static async Task<Dictionary<string, Guid>> TenantsPorNombreAsync(NpgsqlConnection conexion, string[] nombres)
    {
        await using var comando = new NpgsqlCommand("""SELECT "Nombre", "Id" FROM "Tenants" WHERE "Nombre" = ANY(@nombres)""", conexion);
        comando.Parameters.AddWithValue("nombres", nombres);
        var resultado = new Dictionary<string, Guid>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            resultado[lector.GetString(0)] = lector.GetGuid(1);
        return resultado;
    }

    /// <summary>Los identificadores de las filas de pantalla de una organización (y sus Tenants).</summary>
    private static async Task<HashSet<Guid>> IdsDeAsync(NpgsqlConnection conexion, Guid[] tenants)
    {
        var ids = new HashSet<Guid>(tenants);
        foreach (var tabla in new[] { "Documentos", "Trabajadores", "Empresas", "Centros", "Visitas", "Conversaciones", "Mensajes", "Gestiones", "Asignaciones" })
        {
            await using var comando = new NpgsqlCommand($"""SELECT "Id" FROM "{tabla}" WHERE "TenantId" = ANY(@tenants)""", conexion);
            comando.Parameters.AddWithValue("tenants", tenants);
            await using var lector = await comando.ExecuteReaderAsync();
            while (await lector.ReadAsync())
                ids.Add(lector.GetGuid(0));
        }
        return ids;
    }

    /// <summary>Los identificadores de las fichas 360 de un Tenant: el peor caso, el que más filas arrastra.</summary>
    internal async Task<IdsFicha> IdsFichaAsync(Guid tenant)
    {
        await using var conexion = new NpgsqlConnection(Arnes.CadenaPropietario);
        await conexion.OpenAsync();
        async Task<Guid?> Id(string sql)
        {
            await using var comando = new NpgsqlCommand(sql, conexion);
            comando.Parameters.AddWithValue("t", tenant);
            return await comando.ExecuteScalarAsync() as Guid?;
        }

        return new IdsFicha(
            Centro: await Id("""SELECT "CentroId" FROM "Asignaciones" WHERE "TenantId" = @t GROUP BY 1 ORDER BY count(*) DESC, 1 LIMIT 1"""),
            Trabajador: await Id("""SELECT "TrabajadorId" FROM "Documentos" WHERE "TenantId" = @t AND "TrabajadorId" IS NOT NULL AND NOT "EstaEliminado" GROUP BY 1 ORDER BY count(*) DESC, 1 LIMIT 1"""),
            Empresa: await Id("""SELECT "EmpresaId" FROM "Trabajadores" WHERE "TenantId" = @t AND "EmpresaId" IS NOT NULL GROUP BY 1 ORDER BY count(*) DESC, 1 LIMIT 1"""),
            Cliente: await Id("""SELECT "ClienteId" FROM "Centros" WHERE "TenantId" = @t GROUP BY 1 ORDER BY count(*) DESC, 1 LIMIT 1"""),
            Conversacion: await Id("""SELECT "ConversacionId" FROM "Mensajes" WHERE "TenantId" = @t GROUP BY 1 ORDER BY count(*) DESC, 1 LIMIT 1"""));
    }
}

internal sealed record IdsFicha(Guid? Centro, Guid? Trabajador, Guid? Empresa, Guid? Cliente, Guid? Conversacion);

/// <summary>
/// Copia una organización (el Operador CAE externo y sus Tenants beneficiarios) con identificadores nuevos y
/// deterministas: toda fila que mencione uno de sus Tenants, y las filas hijas que apunten a esas, con cada
/// uuid traducido por el mismo mapa, así que las referencias siguen cerrando. Cifras, CIF y textos se conservan;
/// cambian el nombre de los Tenants y el correo de las cuentas, que son únicos en todo el clúster. SQL directo
/// a propósito (§ 13): reproducir con entidades toda la estructura de cartera, delegación y cuentas es justo lo
/// que no se quiere reescribir, y la medición de escala en local usa el mismo mecanismo.
/// </summary>
internal static class OrganizacionClonada
{
    internal static async Task<IReadOnlyList<string>> ClonarAsync(NpgsqlConnection conexion, Guid operador, Guid[] beneficiarios)
    {
        await using (var preparar = new NpgsqlCommand(
            """
            CREATE TEMP TABLE base_tenants(id uuid PRIMARY KEY);
            INSERT INTO base_tenants SELECT unnest(@tenants);
            """, conexion))
        {
            preparar.Parameters.AddWithValue("tenants", (Guid[])[operador, .. beneficiarios]);
            await preparar.ExecuteNonQueryAsync();
        }

        await using (var clonar = new NpgsqlCommand(SqlDeClonacion
                         .Replace("{PREFIJO_TENANT}", EscalaYAislamientoFixture.PrefijoTenantOtra, StringComparison.Ordinal)
                         .Replace("{PREFIJO_CORREO}", EscalaYAislamientoFixture.PrefijoCorreoOtra, StringComparison.Ordinal), conexion))
        {
            clonar.CommandTimeout = 600;
            await clonar.ExecuteNonQueryAsync();
        }

        var errores = new List<string>();
        await using var leer = new NpgsqlCommand("SELECT t::text || ': ' || error FROM clonadas WHERE error IS NOT NULL", conexion);
        await using var lector = await leer.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            errores.Add(lector.GetString(0));
        return errores;
    }

    private const string SqlDeClonacion =
        """
        SET session_replication_role = replica;
        CREATE TEMP TABLE mapa(old uuid PRIMARY KEY, new uuid NOT NULL);
        INSERT INTO mapa SELECT id, md5('otra-' || id)::uuid FROM base_tenants;
        CREATE TEMP TABLE tablas AS
          SELECT c.oid::regclass AS t, c.relname AS nombre
          FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
          WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND NOT c.relispartition
            AND c.relname NOT LIKE '\_\_EF%' AND c.relname NOT LIKE 'Registros%' AND c.relname NOT LIKE 'Eventos%';
        CREATE TEMP TABLE clonadas(t regclass, filas bigint, error text);
        CREATE FUNCTION pg_temp.cols(r regclass, tipo text) RETURNS text[] LANGUAGE sql STABLE AS $f$
          SELECT coalesce(array_agg(quote_ident(a.attname) ORDER BY a.attnum), '{}') FROM pg_attribute a
          WHERE a.attrelid = r AND a.attnum > 0 AND NOT a.attisdropped AND a.attgenerated = '' AND a.attidentity = ''
            AND (tipo = '*' OR format_type(a.atttypid, a.atttypmod) = tipo) $f$;

        DO $d$
        DECLARE r record; uu text[]; cond text; pasada int;
        BEGIN
          FOR pasada IN 1..2 LOOP
            FOR r IN SELECT * FROM tablas LOOP
              uu := pg_temp.cols(r.t, 'uuid');
              IF array_length(uu, 1) IS NULL OR NOT ('"Id"' = ANY(uu)) THEN CONTINUE; END IF;
              IF pasada = 1 THEN
                SELECT string_agg(c || ' IN (SELECT id FROM base_tenants)', ' OR ') INTO cond FROM unnest(uu) c;
              ELSE
                SELECT string_agg(c || ' IN (SELECT old FROM mapa)', ' OR ') INTO cond FROM unnest(uu) c WHERE c <> '"Id"';
                IF cond IS NULL THEN CONTINUE; END IF;
              END IF;
              EXECUTE format('INSERT INTO mapa SELECT "Id", md5(''otra-'' || "Id")::uuid FROM %s WHERE %s ON CONFLICT DO NOTHING', r.t, cond);
            END LOOP;
          END LOOP;
        END $d$;

        DO $d$
        DECLARE r record; todas text[]; uu text[]; cond text; exprs text; c text; filas bigint;
        BEGIN
          FOR r IN SELECT * FROM tablas LOOP
            todas := pg_temp.cols(r.t, '*');
            uu := pg_temp.cols(r.t, 'uuid');
            IF array_length(uu, 1) IS NULL THEN CONTINUE; END IF;
            SELECT string_agg(x || ' IN (SELECT old FROM mapa)', ' OR ') INTO cond FROM unnest(uu) x;
            exprs := '';
            FOREACH c IN ARRAY todas LOOP
              IF c = ANY(uu) THEN
                exprs := exprs || format('coalesce((SELECT new FROM mapa WHERE old = %s), %s), ', c, c);
              ELSIF r.nombre = 'Tenants' AND c = '"Nombre"' THEN
                exprs := exprs || format('''{PREFIJO_TENANT}'' || %s, ', c);
              ELSIF r.nombre = 'AspNetUsers' AND c IN ('"Email"', '"UserName"') THEN
                exprs := exprs || format('''{PREFIJO_CORREO}'' || %s, ', c);
              ELSIF r.nombre = 'AspNetUsers' AND c IN ('"NormalizedEmail"', '"NormalizedUserName"') THEN
                exprs := exprs || format('upper(''{PREFIJO_CORREO}'') || %s, ', c);
              ELSE
                exprs := exprs || c || ', ';
              END IF;
            END LOOP;
            exprs := left(exprs, length(exprs) - 2);
            BEGIN
              EXECUTE format('INSERT INTO %s (%s) SELECT %s FROM %s WHERE %s', r.t, array_to_string(todas, ', '), exprs, r.t, cond);
              GET DIAGNOSTICS filas = ROW_COUNT;
              INSERT INTO clonadas VALUES (r.t, filas, NULL);
            EXCEPTION WHEN others THEN
              INSERT INTO clonadas VALUES (r.t, 0, SQLSTATE || ' ' || SQLERRM);
            END;
          END LOOP;
        END $d$;
        SET session_replication_role = origin;
        ANALYZE;
        """;
}

/// <summary>
/// Dos protecciones sobre las pantallas del piloto, con la identidad de aplicación (cae_app_runtime y contexto
/// RLS firmado) y la Gestora CAE sembrada:
/// <list type="bullet">
/// <item>presupuesto de SQL por query de pantalla: una regresión de N+1 o una lectura de más sale en rojo aquí
/// antes de notarse en la base de 500.000 Documentos, donde se midieron estas cifras (informe
/// MEDICION-ESCALA-500K-2026-10-24 en el repositorio de documentación);</item>
/// <item>aislamiento entre organizaciones: ninguna pantalla, ficha 360, buscador global ni recuento de chips de
/// una organización devuelve un identificador de la otra, en los dos sentidos, aunque las Empresas compartan CIF.</item>
/// </list>
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class EscalaPresupuestoSqlYAislamientoTests(EscalaYAislamientoFixture fixture, ITestOutputHelper salida)
    : IClassFixture<EscalaYAislamientoFixture>
{
    private const string TenantMedido = "Mantenimientos Integrales Corvesta, S.A.";

    private static readonly Dictionary<string, Func<ISender, IdsFicha, Task<object?>>> LecturasDeTenant = new()
    {
        ["Documentos"] = async (s, _) => await s.Send(new ObtenerDocumentosQuery(null, null, null, TamanoPagina: 20, ConRecuentosPorEstado: true)),
        ["Documentos vencidos por vencimiento"] = async (s, _) => await s.Send(new ObtenerDocumentosQuery(null, null, null, TamanoPagina: 20,
            OrdenarPor: "FechaVencimiento", Estados: [EstadoDocumento.Vencido], ConRecuentosPorEstado: true)),
        ["Trabajadores"] = async (s, _) => await s.Send(new ObtenerTrabajadoresQuery(null, TamanoPagina: 20, ConDesgloseDocumental: true, ConRecuentosPorEstado: true)),
        ["Empresas"] = async (s, _) => await s.Send(new ObtenerEmpresasQuery(null, TamanoPagina: 20, ConRecuentosPorEstado: true, ConDesgloseDocumental: true, ConClientePrincipal: true)),
        ["Centros"] = async (s, _) => await s.Send(new ObtenerCentrosQuery(null, null, TamanoPagina: 20, ConRecuentosPorEstado: true, ConResumenPorGrupo: true)),
        ["Centros bloqueados"] = async (s, _) => await s.Send(new ObtenerCentrosQuery(null, null, TamanoPagina: 20, Estados: [EstadoCentro.Bloqueado], ConRecuentosPorEstado: true)),
        ["Subcontratas"] = async (s, _) => await s.Send(new ObtenerSubcontratasQuery(null, TamanoPagina: 20, ConRecuentosPorEstado: true)),
        ["Clientes"] = async (s, _) => await s.Send(new ObtenerClientesQuery(null, null, TamanoPagina: 20, ConRecuentosPorEstado: true, ConDesgloseDocumental: true)),
        ["Visitas"] = async (s, _) => await s.Send(new ObtenerVisitasQuery(null, false, null, TamanoPagina: 20, ConRecuentosPorEstado: true)),
        ["Conversaciones"] = async (s, _) => await s.Send(new ObtenerConversacionesQuery(TamanoPagina: 20)),
        ["Conversación"] = async (s, i) => i.Conversacion is { } id ? await s.Send(new ObtenerConversacionPorIdQuery(id)) : null,
        ["Gestiones"] = async (s, _) => await s.Send(new ObtenerGestionesQuery(null, null, null, TamanoPagina: 20, ConRecuentosPorEstado: true)),
        ["Alertas"] = async (s, _) => await s.Send(new ObtenerAlertasQuery()),
        ["Inicio: KPIs"] = async (s, _) => await s.Send(new ObtenerKpisDashboardQuery()),
        ["Inicio: próximas visitas"] = async (s, _) => await s.Send(new ObtenerVisitasQuery(null, true, null, TamanoPagina: 5)),
        ["Inicio: bandeja agrupada"] = async (s, _) => await s.Send(new ObtenerBandejaAgrupadaQuery()),
        ["Buscador global"] = async (s, _) => await s.Send(new BuscarGlobalQuery("ser")),
        ["Centro 360"] = async (s, i) => i.Centro is { } id ? await s.Send(new ObtenerCentroPorIdQuery(id)) : null,
        ["Centro 360: documentación de Empresas"] = async (s, i) => i.Centro is { } id ? await s.Send(new ObtenerDocumentacionEmpresasDeCentroQuery(id)) : null,
        ["Centro 360: documentación requerida"] = async (s, i) => i.Centro is { } id ? await s.Send(new ObtenerDocumentacionRequeridaDeCentroQuery(id)) : null,
        ["Centro 360: trabajadores asignados"] = async (s, i) => i.Centro is { } id ? await s.Send(new ObtenerTrabajadoresAsignadosDeCentroQuery(id)) : null,
        ["Empresa 360"] = async (s, i) => i.Empresa is { } id ? await s.Send(new ObtenerEmpresaPorIdQuery(id)) : null,
        ["Empresa 360: cumplimiento"] = async (s, i) => i.Empresa is { } id ? await s.Send(new ObtenerCumplimientoEmpresaQuery(id)) : null,
        ["Empresa 360: trabajadores"] = async (s, i) => i.Empresa is { } id ? await s.Send(new ObtenerTrabajadoresQuery(null, EmpresaId: id, TamanoPagina: 20, ConDesgloseDocumental: true)) : null,
        ["Trabajador 360"] = async (s, i) => i.Trabajador is { } id ? await s.Send(new ObtenerTrabajadorPorIdQuery(id)) : null,
        ["Trabajador 360: documentación por centro"] = async (s, i) => i.Trabajador is { } id ? await s.Send(new ObtenerDocumentacionPorCentroDeTrabajadorQuery(id)) : null,
        ["Trabajador 360: documentos"] = async (s, i) => i.Trabajador is { } id ? await s.Send(new ObtenerDocumentosQuery(id, null, null, TamanoPagina: 50)) : null,
        ["Cliente 360"] = async (s, i) => i.Cliente is { } id ? await s.Send(new ObtenerClientePorIdQuery(id)) : null,
        ["Cliente 360: resumen"] = async (s, i) => i.Cliente is { } id ? await s.Send(new ObtenerResumenClienteQuery(id)) : null,
        ["Cliente 360: centros"] = async (s, i) => i.Cliente is { } id ? await s.Send(new ObtenerCentrosQuery(null, id, TamanoPagina: 20)) : null,
        ["Cliente 360: empresas"] = async (s, i) => i.Cliente is { } id ? await s.Send(new ObtenerEmpresasDeClienteQuery(id)) : null,
    };

    /// <summary>
    /// Sentencias SQL por query, medidas con esta misma siembra y con un ámbito nuevo por query tras una ejecución
    /// de calentamiento, con un margen del 10 % (mínimo 2) sobre lo medido para no depender del orden de siembra. Son techos: bajar una cifra es bienvenido (y se actualiza aquí); subirla exige explicar
    /// en la PR por qué la pantalla necesita una lectura más.
    /// </summary>
    public static TheoryData<string, int> Presupuestos => new()
    {
        { "Documentos", 6 }, // medido: 4
        { "Documentos vencidos por vencimiento", 5 }, // medido: 3
        { "Trabajadores", 14 }, // medido: 12
        { "Empresas", 21 }, // medido: 19
        { "Centros", 33 }, // medido: 30
        { "Centros bloqueados", 33 }, // medido: 30
        { "Subcontratas", 13 }, // medido: 11
        { "Clientes", 31 }, // medido: 28
        { "Visitas", 11 }, // medido: 9
        { "Conversaciones", 18 }, // medido: 16
        { "Conversación", 22 }, // medido: 20
        { "Gestiones", 5 }, // medido: 3
        { "Alertas", 11 }, // medido: 9
        { "Inicio: KPIs", 26 }, // medido: 23
        { "Inicio: próximas visitas", 10 }, // medido: 8
        { "Inicio: bandeja agrupada", 33 }, // medido: 30
        { "Buscador global", 8 }, // medido: 6
        { "Centro 360", 7 }, // medido: 5
        { "Centro 360: documentación de Empresas", 20 }, // medido: 18
        { "Centro 360: documentación requerida", 6 }, // medido: 4
        { "Centro 360: trabajadores asignados", 8 }, // medido: 6
        { "Empresa 360", 8 }, // medido: 6
        { "Empresa 360: cumplimiento", 11 }, // medido: 9
        { "Empresa 360: trabajadores", 14 }, // medido: 12
        { "Trabajador 360", 6 }, // medido: 4
        { "Trabajador 360: documentación por centro", 17 }, // medido: 15
        { "Trabajador 360: documentos", 6 }, // medido: 4
        { "Cliente 360", 5 }, // medido: 3
        { "Cliente 360: resumen", 5 }, // medido: 3
        { "Cliente 360: centros", 35 }, // medido: 31
        { "Cliente 360: empresas", 3 }, // medido: 1
    };

    [Theory]
    [MemberData(nameof(Presupuestos))]
    public async Task Cada_query_de_pantalla_cabe_en_su_presupuesto_de_SQL(string query, int maximo)
    {
        var tenant = fixture.TenantsPiloto[TenantMedido];
        var ids = await fixture.IdsFichaAsync(tenant);
        var sentencias = await fixture.Arnes.ComoGestoraPrimeraEnAsync(TenantMedido, sp => ContarAsync(sp, s => LecturasDeTenant[query](s, ids)));

        salida.WriteLine($"{query}: {sentencias} sentencias (presupuesto {maximo})");
        sentencias.Should().BeLessThanOrEqualTo(maximo, $"«{query}» tenía un presupuesto de {maximo} sentencias SQL y ha emitido {sentencias}");
    }

    [Fact]
    public async Task Mi_trabajo_y_el_Cuadro_de_direccion_caben_en_su_presupuesto_de_SQL()
    {
        var miTrabajo = await fixture.Arnes.ComoCuentaAsync(CatalogoPilotoOutbound.NombreTenantOperador, CuentasPilotoOutbound.Locales.GestoraPrimera,
            sp => ContarAsync(sp, async s => await s.Send(new ObtenerMiTrabajoAgregadoQuery())));
        var hoy = DateOnly.FromDateTime(DateTime.Today);
        var cuadro = await fixture.Arnes.ComoCuentaAsync(CatalogoPilotoOutbound.NombreTenantOperador, fixture.CorreoDireccion,
            sp => ContarAsync(sp, async s => await s.Send(new ObtenerCuadroDireccionQuery(hoy.Year, hoy.Month))));

        salida.WriteLine($"Mi trabajo: {miTrabajo}; Cuadro de dirección: {cuadro}");
        // Medido: 270 y 533. Las dos crecen con la cartera (una lectura por Empresa gestionada): el techo congela el N+1
        // actual para que no empeore; bajarlo es el trabajo pendiente que recoge el informe de escala.
        miTrabajo.Should().BeLessThanOrEqualTo(297, "presupuesto de Mi trabajo");
        cuadro.Should().BeLessThanOrEqualTo(587, "presupuesto del Cuadro de dirección");
    }

    [Fact]
    public async Task Desde_cada_Tenant_de_una_organizacion_ninguna_pantalla_devuelve_filas_de_la_otra_en_los_dos_sentidos()
    {
        var fugas = new List<string>();
        var propiasVistas = 0;

        foreach (var (nombre, tenantPiloto) in fixture.TenantsPiloto)
        {
            var (fuga, propias) = await LeerTodoAsync(CatalogoPilotoOutbound.NombreTenantOperador, CuentasPilotoOutbound.Locales.GestoraPrimera,
                tenantPiloto, fixture.IdsOtra, fixture.IdsPiloto);
            fugas.AddRange(fuga.Select(f => $"piloto→otra, {nombre}: {f}"));
            propiasVistas += propias;

            (fuga, propias) = await LeerTodoAsync(EscalaYAislamientoFixture.PrefijoTenantOtra + CatalogoPilotoOutbound.NombreTenantOperador,
                EscalaYAislamientoFixture.PrefijoCorreoOtra + CuentasPilotoOutbound.Locales.GestoraPrimera,
                fixture.TenantsOtra[nombre], fixture.IdsPiloto, fixture.IdsOtra);
            fugas.AddRange(fuga.Select(f => $"otra→piloto, {nombre}: {f}"));
            propiasVistas += propias;
        }

        // Control positivo: sin él, una cuenta que no viera nada daría verde (vacío ≠ negativo).
        propiasVistas.Should().BeGreaterThan(1000, "las dos Gestoras CAE ven las filas de su propia organización");
        fugas.Should().BeEmpty();
    }

    [Fact]
    public async Task Mi_trabajo_y_el_Cuadro_de_direccion_no_cruzan_organizaciones()
    {
        var hoy = DateOnly.FromDateTime(DateTime.Today);
        foreach (var (operador, correo, correoDireccion, ajenos, propios) in new[]
                 {
                     (CatalogoPilotoOutbound.NombreTenantOperador, CuentasPilotoOutbound.Locales.GestoraPrimera, fixture.CorreoDireccion, fixture.IdsOtra, fixture.IdsPiloto),
                     (EscalaYAislamientoFixture.PrefijoTenantOtra + CatalogoPilotoOutbound.NombreTenantOperador,
                      EscalaYAislamientoFixture.PrefijoCorreoOtra + CuentasPilotoOutbound.Locales.GestoraPrimera,
                      EscalaYAislamientoFixture.PrefijoCorreoOtra + fixture.CorreoDireccion, fixture.IdsPiloto, fixture.IdsOtra),
                 })
        {
            var miTrabajo = await fixture.Arnes.ComoCuentaAsync(operador, correo, async sp => Json(await sp.GetRequiredService<ISender>().Send(new ObtenerMiTrabajoAgregadoQuery())));
            var cuadro = await fixture.Arnes.ComoCuentaAsync(operador, correoDireccion, async sp => Json(await sp.GetRequiredService<ISender>().Send(new ObtenerCuadroDireccionQuery(hoy.Year, hoy.Month))));

            Guids(miTrabajo).Should().NotIntersectWith(ajenos, $"Mi trabajo de {operador} solo lleva Tenants de su cartera");
            Guids(miTrabajo).Should().IntersectWith(propios, $"Mi trabajo de {operador} lleva su propia cartera");
            Guids(cuadro).Should().NotIntersectWith(ajenos, $"el Cuadro de dirección de {operador} solo cuenta su organización");
        }
    }

    [Fact]
    public async Task El_mismo_CIF_en_las_dos_organizaciones_son_registros_distintos_y_cada_Tenant_encuentra_solo_el_suyo()
    {
        var (nombre, cif) = await CifDeEmpresaAsync(soloEnUnTenant: false);
        var idPiloto = await EmpresaPorCifAsync(fixture.TenantsPiloto[nombre], cif);
        var idOtra = await EmpresaPorCifAsync(fixture.TenantsOtra[nombre], cif);
        idPiloto.Should().NotBeNull();
        idOtra.Should().NotBeNull().And.NotBe(idPiloto!.Value, "cada Tenant propietario tiene su propio registro de la Empresa");

        var vistaDesdePiloto = await fixture.Arnes.ComoGestoraPrimeraEnAsync(nombre,
            async sp => await sp.GetRequiredService<ISender>().Send(new BuscarEmpresaPorCifQuery(cif)));
        var vistaDesdeOtra = await ComoGestoraOtraEnAsync(nombre,
            async sp => await sp.GetRequiredService<ISender>().Send(new BuscarEmpresaPorCifQuery(cif)));

        vistaDesdePiloto!.Id.Should().Be(idPiloto.Value);
        vistaDesdeOtra!.Id.Should().Be(idOtra!.Value);
    }

    [Fact]
    public async Task Buscar_por_CIF_no_revela_si_la_Empresa_existe_en_otra_organizacion()
    {
        // Un CIF que solo tiene una Empresa del piloto, buscado desde un Tenant de la otra organización que no la
        // tiene: el resultado tiene que ser el mismo que el de un CIF que no existe en ningún sitio.
        var (nombre, cif) = await CifDeEmpresaAsync(soloEnUnTenant: true);
        var otroTenant = fixture.TenantsOtra.First(t => t.Key != nombre).Key;

        var existente = await ComoGestoraOtraEnAsync(otroTenant, async sp => await sp.GetRequiredService<ISender>().Send(new BuscarEmpresaPorCifQuery(cif)));
        var inexistente = await ComoGestoraOtraEnAsync(otroTenant, async sp => await sp.GetRequiredService<ISender>().Send(new BuscarEmpresaPorCifQuery("B99999990")));

        existente.Should().BeNull("la Empresa existe en otra organización, y eso no se puede deducir desde aquí");
        inexistente.Should().BeNull();
    }

    private async Task<(IReadOnlyList<string> Fugas, int Propias)> LeerTodoAsync(
        string operador, string correo, Guid tenant, HashSet<Guid> ajenos, HashSet<Guid> propios)
    {
        var ids = await fixture.IdsFichaAsync(tenant);
        return await fixture.Arnes.ComoCuentaAsync(operador, correo, async sp =>
        {
            var fugas = new List<string>();
            var propias = 0;
            using (AmbitoTenantExplicito.Establecer(tenant))
            {
                var sender = sp.GetRequiredService<ISender>();
                foreach (var (pantalla, lectura) in LecturasDeTenant)
                {
                    var vistos = Guids(Json(await lectura(sender, ids)));
                    var cruzados = vistos.Where(ajenos.Contains).ToList();
                    if (cruzados.Count > 0)
                        fugas.Add($"{pantalla} devuelve {cruzados.Count} identificadores ajenos, p. ej. {cruzados[0]}");
                    propias += vistos.Count(propios.Contains);
                    sp.GetRequiredService<CaeManagerDbContext>().ChangeTracker.Clear();
                }
            }
            return ((IReadOnlyList<string>)fugas, propias);
        });
    }

    private Task<T> ComoGestoraOtraEnAsync<T>(string nombreTenant, Func<IServiceProvider, Task<T>> lectura) =>
        fixture.Arnes.ComoCuentaAsync(EscalaYAislamientoFixture.PrefijoTenantOtra + CatalogoPilotoOutbound.NombreTenantOperador,
            EscalaYAislamientoFixture.PrefijoCorreoOtra + CuentasPilotoOutbound.Locales.GestoraPrimera, async sp =>
            {
                using (AmbitoTenantExplicito.Establecer(fixture.TenantsOtra[nombreTenant]))
                    return await lectura(sp);
            });

    private static async Task<int> ContarAsync(IServiceProvider sp, Func<ISender, Task<object?>> lectura)
    {
        var sender = sp.GetRequiredService<ISender>();
        var contexto = sp.GetRequiredService<CaeManagerDbContext>();
        await lectura(sender); // Calentamiento: compilación de EF y memorias del ámbito, como en una sesión abierta.
        contexto.ChangeTracker.Clear();
        var (_, sentencias) = await ArnesPilotoOutbound.Contador.ContarAsync(() => lectura(sender));
        contexto.ChangeTracker.Clear();
        return sentencias;
    }

    /// <summary>El nombre del Tenant del piloto y el CIF de una de sus Empresas (opcionalmente, una cuyo CIF no tiene ningún otro Tenant del piloto).</summary>
    private async Task<(string Tenant, string Cif)> CifDeEmpresaAsync(bool soloEnUnTenant)
    {
        await using var conexion = new NpgsqlConnection(fixture.Arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(
            """
            SELECT e."TenantId", e."Cif" FROM "Empresas" e
            WHERE e."TenantId" = ANY(@piloto) AND e."Cif" IS NOT NULL AND NOT e."EstaEliminado"
              AND (NOT @solo OR NOT EXISTS (SELECT 1 FROM "Empresas" o WHERE o."Cif" = e."Cif" AND o."TenantId" = ANY(@piloto) AND o."TenantId" <> e."TenantId"))
            ORDER BY e."Cif" LIMIT 1
            """, conexion);
        comando.Parameters.AddWithValue("piloto", fixture.TenantsPiloto.Values.ToArray());
        comando.Parameters.AddWithValue("solo", soloEnUnTenant);
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue("el piloto siembra Empresas con CIF");
        var tenant = lector.GetGuid(0);
        return (fixture.TenantsPiloto.Single(t => t.Value == tenant).Key, lector.GetString(1));
    }

    private async Task<Guid?> EmpresaPorCifAsync(Guid tenant, string cif)
    {
        await using var conexion = new NpgsqlConnection(fixture.Arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand("""SELECT "Id" FROM "Empresas" WHERE "TenantId" = @t AND "Cif" = @cif""", conexion);
        comando.Parameters.AddWithValue("t", tenant);
        comando.Parameters.AddWithValue("cif", cif);
        return await comando.ExecuteScalarAsync() as Guid?;
    }

    private static string Json(object? resultado) =>
        resultado is null ? "null" : JsonSerializer.Serialize(resultado, new JsonSerializerOptions { MaxDepth = 256 });

    private static HashSet<Guid> Guids(string json) =>
        [.. Regex.Matches(json, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Select(m => Guid.Parse(m.Value))];
}
