using System.Security.Cryptography;
using System.Text;
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
using CaeManager.Application.Documentos;
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
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Application.EstadoMaterializado;
using CaeManager.Domain.EstadoMaterializado;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// Un Operador CAE externo y su cartera de Tenants beneficiarios: el del piloto o el clonado a su lado.
/// <see cref="Tenants"/> va por el nombre del Tenant beneficiario en el catálogo del piloto (sin prefijo), para
/// emparejar cada Tenant con su copia.
/// </summary>
internal sealed record LadoOperadorCae(
    string Rotulo, string NombreTenantOperador, string PrefijoCorreo, IReadOnlyDictionary<string, Guid> Tenants, HashSet<Guid> Ids)
{
    /// <summary>El Gestor CAE que lleva ese Tenant beneficiario en su Asignación de Cartera (el compartido, la Gestora primera).</summary>
    internal string CorreoGestorDe(string nombreTenant) =>
        PrefijoCorreo + (CatalogoPilotoOutbound.Tenants.Single(t => t.Nombre == nombreTenant).EnCarteraDeLaGestoraPrimera
            ? CuentasPilotoOutbound.Locales.GestoraPrimera
            : CuentasPilotoOutbound.Locales.GestorSegundo);

    internal string CorreoGestoraPrimera => PrefijoCorreo + CuentasPilotoOutbound.Locales.GestoraPrimera;
}

/// <summary>
/// La siembra del piloto y, al lado, un segundo Operador CAE externo y su cartera con la misma estructura: sus
/// Tenants beneficiarios clonados con identificadores nuevos y los mismos CIF. Es la versión pequeña, para CI, de
/// la base de escala que se mide en local con 500.000 Documentos.
/// </summary>
public sealed class EscalaYAislamientoFixture : IAsyncLifetime
{
    /// <summary>Prefijo del nombre de los Tenants clonados y del correo de sus cuentas.</summary>
    internal const string PrefijoTenantOtro = "Otro Operador CAE · ";
    internal const string PrefijoCorreoOtro = "otra.";

    internal ArnesPilotoOutbound Arnes { get; private set; } = null!;
    internal LadoOperadorCae Piloto { get; private set; } = null!;
    internal LadoOperadorCae Otro { get; private set; } = null!;
    internal string CorreoDireccion { get; private set; } = null!;

    /// <summary>Los identificadores de las fichas 360 de cada Tenant beneficiario del piloto (su copia lleva <see cref="OperadorCaeClonado.IdClonado"/>).</summary>
    internal IReadOnlyDictionary<string, IdsFicha> FichasPiloto { get; private set; } = null!;

    /// <summary>
    /// El oráculo de los recuentos: la huella de cada pantalla de cada Tenant beneficiario medida ANTES de clonar,
    /// cuando no existía nada que contar de más. Después de clonar, el Tenant del piloto y su copia tienen que dar
    /// exactamente esto; si un recuento sumara filas del otro Operador CAE externo, se movería.
    /// </summary>
    internal IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> HuellasAntesDeClonar { get; private set; } = null!;

    /// <summary>Las cifras del Cuadro de dirección del piloto antes de clonar, con el mismo propósito.</summary>
    internal string CifrasCuadroAntesDeClonar { get; private set; } = null!;

    /// <summary>Las cifras de Mi trabajo de la Gestora CAE primera del piloto antes de clonar.</summary>
    internal string CifrasMiTrabajoAntesDeClonar { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Arnes = await ArnesPilotoOutbound.CrearAsync();
        try
        {
            await PrepararAsync();
        }
        catch
        {
            // xUnit no llama a DisposeAsync si InitializeAsync falla: sin esto la base quedaría en el clúster compartido.
            var arnes = Arnes;
            Arnes = null!;
            await arnes.DisposeAsync();
            throw;
        }
    }

    private async Task PrepararAsync()
    {
        await Arnes.SembrarAsync(ArnesPilotoOutbound.Configurar(ArnesPilotoOutbound.FechaDemostracion()));
        await Arnes.BackfillAsync();

        var nombres = CatalogoPilotoOutbound.Tenants.Select(t => t.Nombre).ToArray();
        await using var conexion = new NpgsqlConnection(Arnes.CadenaPropietario);
        await conexion.OpenAsync();

        var operador = await Arnes.TenantIdAsync(CatalogoPilotoOutbound.NombreTenantOperador);
        var tenantsPiloto = await TenantsPorNombreAsync(conexion, nombres);
        Piloto = new LadoOperadorCae("piloto", CatalogoPilotoOutbound.NombreTenantOperador, "", tenantsPiloto,
            await IdsDeAsync(conexion, [operador, .. tenantsPiloto.Values]));
        CorreoDireccion = await CorreoDireccionAsync(conexion, operador);

        var fichas = new Dictionary<string, IdsFicha>();
        var huellas = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        foreach (var (nombre, tenant) in tenantsPiloto)
        {
            fichas[nombre] = await IdsFichaAsync(conexion, tenant);
            huellas[nombre] = await ComoGestorEnAsync(Piloto, nombre, sp => LecturasDePantalla.HuellasAsync(sp, fichas[nombre]));
        }
        FichasPiloto = fichas;
        HuellasAntesDeClonar = huellas;
        CifrasCuadroAntesDeClonar = await CifrasCuadroAsync(Piloto);
        CifrasMiTrabajoAntesDeClonar = await CifrasMiTrabajoAsync(Piloto);

        var errores = await OperadorCaeClonado.ClonarAsync(conexion, operador, [.. tenantsPiloto.Values]);
        errores.Should().BeEmpty("el segundo Operador CAE externo se clona entero con su cartera, o el aislamiento se mediría sobre media estructura");
        var (originales, copias) = await OperadorCaeClonado.MapaAsync(conexion);

        var operadorOtro = await Arnes.TenantIdAsync(PrefijoTenantOtro + CatalogoPilotoOutbound.NombreTenantOperador);
        operadorOtro.Should().Be(OperadorCaeClonado.IdClonado(operador), "la traducción de identificadores del test es la misma que la del SQL de clonación");
        var tenantsOtro = (await TenantsPorNombreAsync(conexion, [.. nombres.Select(n => PrefijoTenantOtro + n)]))
            .ToDictionary(p => p.Key[PrefijoTenantOtro.Length..], p => p.Value);
        tenantsOtro.Keys.Should().BeEquivalentTo(tenantsPiloto.Keys);
        Otro = new LadoOperadorCae("otro", PrefijoTenantOtro + CatalogoPilotoOutbound.NombreTenantOperador, PrefijoCorreoOtro, tenantsOtro,
            await IdsDeAsync(conexion, [operadorOtro, .. tenantsOtro.Values]));

        // El mapa de la clonación añade lo que no lleva columna de Tenant con ese nombre: hijas de hijas, y tablas que
        // nombran al Tenant de otra forma (destinatario, lector, contratista, operador, delegante).
        Piloto.Ids.UnionWith(originales);
        Otro.Ids.UnionWith(copias);
        Piloto.Ids.Should().NotIntersectWith(Otro.Ids, "cada fila clonada lleva un identificador nuevo");
    }

    public async Task DisposeAsync()
    {
        if (Arnes is not null)
            await Arnes.DisposeAsync();
    }

    internal LadoOperadorCae ElOtro(LadoOperadorCae lado) => ReferenceEquals(lado, Piloto) ? Otro : Piloto;

    /// <summary>La ficha 360 del Tenant <paramref name="nombre"/> en ese lado: la del piloto o su traducción exacta en la copia.</summary>
    internal IdsFicha FichaDe(LadoOperadorCae lado, string nombre) =>
        ReferenceEquals(lado, Piloto) ? FichasPiloto[nombre] : FichasPiloto[nombre].Clonada();

    /// <summary>Una lectura como el Gestor CAE que lleva el Tenant en su cartera, dentro del ámbito de ese Tenant.</summary>
    internal Task<T> ComoGestorEnAsync<T>(LadoOperadorCae lado, string nombreTenant, Func<IServiceProvider, Task<T>> lectura) =>
        Arnes.ComoCuentaAsync(lado.NombreTenantOperador, lado.CorreoGestorDe(nombreTenant), async sp =>
        {
            using (AmbitoTenantExplicito.Establecer(lado.Tenants[nombreTenant]))
                return await lectura(sp);
        });

    internal Task<string> CifrasCuadroAsync(LadoOperadorCae lado)
    {
        var hoy = DiaDeNegocio.Hoy();
        return Arnes.ComoCuentaAsync(lado.NombreTenantOperador, lado.PrefijoCorreo + CorreoDireccion,
            async sp => LecturasDePantalla.Cifras(await sp.GetRequiredService<ISender>().Send(new ObtenerCuadroDireccionQuery(hoy.Year, hoy.Month))));
    }

    internal Task<string> CifrasMiTrabajoAsync(LadoOperadorCae lado) =>
        Arnes.ComoCuentaAsync(lado.NombreTenantOperador, lado.CorreoGestoraPrimera,
            async sp => LecturasDePantalla.Cifras(await sp.GetRequiredService<ISender>().Send(new ObtenerMiTrabajoAgregadoQuery())));

    private static async Task<string> CorreoDireccionAsync(NpgsqlConnection conexion, Guid operador)
    {
        await using var comando = new NpgsqlCommand(
            """
            SELECT u."Email" FROM "AspNetUsers" u
            JOIN "AspNetUserRoles" ur ON ur."UserId" = u."Id" JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
            WHERE u."TenantId" = @operador AND r."Name" = 'DireccionCae' ORDER BY 1 LIMIT 1
            """, conexion);
        comando.Parameters.AddWithValue("operador", operador);
        return (string)(await comando.ExecuteScalarAsync())!;
    }

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

    /// <summary>
    /// Los identificadores de un Operador CAE externo y su cartera: los Tenants y todas las filas de toda tabla con
    /// <c>Id</c> uuid y <c>TenantId</c> o <c>PropietarioTenantId</c> de uno de ellos. No una lista de tablas de
    /// pantalla: una fila de una tabla que nadie pensó en listar es justo la fuga que no se vería.
    /// </summary>
    private static async Task<HashSet<Guid>> IdsDeAsync(NpgsqlConnection conexion, Guid[] tenants)
    {
        var tablas = new List<(string Tabla, string Columna)>();
        await using (var listar = new NpgsqlCommand(
                         """
                         SELECT c.relname, t.attname FROM pg_class c
                         JOIN pg_namespace n ON n.oid = c.relnamespace
                         JOIN pg_attribute i ON i.attrelid = c.oid AND i.attname = 'Id' AND i.atttypid = 'uuid'::regtype AND NOT i.attisdropped
                         JOIN pg_attribute t ON t.attrelid = c.oid AND t.attname IN ('TenantId', 'PropietarioTenantId') AND t.atttypid = 'uuid'::regtype AND NOT t.attisdropped
                         WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND NOT c.relispartition
                         ORDER BY 1, 2
                         """, conexion))
        await using (var lector = await listar.ExecuteReaderAsync())
        {
            while (await lector.ReadAsync())
                tablas.Add((lector.GetString(0), lector.GetString(1)));
        }

        tablas.Select(t => t.Tabla).Should().Contain(["Documentos", "Trabajadores", "Empresas", "Centros", "Visitas", "Conversaciones", "Mensajes", "Gestiones", "Asignaciones"],
            "el recorrido del catálogo encuentra al menos las tablas de pantalla");

        var ids = new HashSet<Guid>(tenants);
        foreach (var (tabla, columna) in tablas)
        {
            await using var comando = new NpgsqlCommand($"""SELECT "Id" FROM "{tabla}" WHERE "{columna}" = ANY(@tenants)""", conexion);
            comando.Parameters.AddWithValue("tenants", tenants);
            await using var lector = await comando.ExecuteReaderAsync();
            while (await lector.ReadAsync())
                ids.Add(lector.GetGuid(0));
        }
        return ids;
    }

    /// <summary>Los identificadores de las fichas 360 de un Tenant: el peor caso, el que más filas arrastra; el desempate por Id los hace deterministas.</summary>
    private static async Task<IdsFicha> IdsFichaAsync(NpgsqlConnection conexion, Guid tenant)
    {
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

internal sealed record IdsFicha(Guid? Centro, Guid? Trabajador, Guid? Empresa, Guid? Cliente, Guid? Conversacion)
{
    /// <summary>La misma ficha en la copia: cada identificador traducido como lo traduce la clonación.</summary>
    internal IdsFicha Clonada() => new(
        OperadorCaeClonado.IdClonado(Centro), OperadorCaeClonado.IdClonado(Trabajador), OperadorCaeClonado.IdClonado(Empresa),
        OperadorCaeClonado.IdClonado(Cliente), OperadorCaeClonado.IdClonado(Conversacion));
}

/// <summary>
/// Copia un Operador CAE externo y su cartera (el Tenant del Operador CAE y sus Tenants beneficiarios) con
/// identificadores nuevos y deterministas: toda fila que mencione uno de sus Tenants, y las filas hijas que apunten
/// a esas, con cada uuid traducido por el mismo mapa, así que las referencias siguen cerrando. Cifras, CIF y textos
/// se conservan; cambian el nombre de los Tenants y el correo de las cuentas, que son únicos en todo el clúster. SQL
/// directo a propósito (§ 13): reproducir con entidades toda la estructura de cartera, delegación y cuentas es justo
/// lo que no se quiere reescribir, y la medición de escala en local usa el mismo mecanismo.
/// </summary>
internal static class OperadorCaeClonado
{
    /// <summary>El identificador de la copia de una fila: <c>md5('otra-' || id)::uuid</c>, lo mismo que el SQL de abajo.</summary>
    internal static Guid IdClonado(Guid id) =>
        Guid.ParseExact(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("otra-" + id.ToString("D")))), "N");

    internal static Guid? IdClonado(Guid? id) => id is { } valor ? IdClonado(valor) : null;

    /// <summary>
    /// Todas las filas que la clonación copió, en la misma sesión que la hizo (el mapa es una tabla temporal): los
    /// originales y sus copias, incluidas las hijas que no llevan columna de Tenant.
    /// </summary>
    internal static async Task<(HashSet<Guid> Originales, HashSet<Guid> Copias)> MapaAsync(NpgsqlConnection conexion)
    {
        var (originales, copias) = (new HashSet<Guid>(), new HashSet<Guid>());
        await using var leer = new NpgsqlCommand("SELECT old, new FROM mapa", conexion);
        await using var lector = await leer.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            originales.Add(lector.GetGuid(0));
            copias.Add(lector.GetGuid(1));
        }
        return (originales, copias);
    }

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
                         .Replace("{PREFIJO_TENANT}", EscalaYAislamientoFixture.PrefijoTenantOtro, StringComparison.Ordinal)
                         .Replace("{PREFIJO_CORREO}", EscalaYAislamientoFixture.PrefijoCorreoOtro, StringComparison.Ordinal), conexion))
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

/// <summary>Las lecturas de pantalla de un Tenant beneficiario y la huella de sus recuentos.</summary>
internal static class LecturasDePantalla
{
    internal static readonly IReadOnlyDictionary<string, Func<ISender, IdsFicha, Task<object?>>> DeTenant =
        new Dictionary<string, Func<ISender, IdsFicha, Task<object?>>>
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

    /// <summary>Las fichas 360 que se piden por identificador: con uno del otro Operador CAE externo tienen que devolver null.</summary>
    internal static readonly string[] FichasPorId = ["Centro 360", "Empresa 360", "Trabajador 360", "Cliente 360", "Conversación"];

    /// <summary>Pantallas sin paginar cuyo resultado entero son cifras de recuento.</summary>
    private static readonly HashSet<string> SoloCifras = ["Inicio: KPIs", "Empresa 360: cumplimiento", "Cliente 360: resumen"];

    /// <summary>
    /// La huella de los recuentos de una pantalla, independiente de qué filas caen en la página (que desempatan por
    /// Id, y en la copia los Id son otros): en un listado, el total, los recuentos por estado y los resúmenes por
    /// grupo; en una pantalla de cifras, todas sus cifras ordenadas. <c>null</c> en las demás (fichas de detalle).
    /// </summary>
    internal static string? Huella(string pantalla, object? resultado)
    {
        if (resultado is not null && resultado.GetType().GetProperty(nameof(ResultadoPaginado<int>.TotalElementos)) is { } total
            && resultado.GetType().GetProperty(nameof(ResultadoPaginado<int>.RecuentosPorEstado)) is { } recuentos)
        {
            var tipo = resultado.GetType();
            var porEstado = recuentos.GetValue(resultado) as IReadOnlyDictionary<string, int>;
            var sinEstado = tipo.GetProperty(nameof(ResultadoPaginado<int>.TotalSinFiltroDeEstado))?.GetValue(resultado) as int?;
            var grupos = tipo.GetProperty(nameof(ResultadoPaginado<int>.ResumenPorGrupo))?.GetValue(resultado) as IReadOnlyDictionary<string, ResumenDeGrupo>;
            return $"total={total.GetValue(resultado)}; sin estado={sinEstado?.ToString() ?? "-"}; "
                + $"estados=[{Pares(porEstado)}]; grupos=[{string.Join(" | ", (grupos?.Values ?? []).Select(g => $"{g.Total}: {Pares(g.PorEstado)}").Order(StringComparer.Ordinal))}]";
        }

        return SoloCifras.Contains(pantalla) ? Cifras(resultado) : null;
    }

    /// <summary>Todas las cifras de un resultado, ordenadas: no depende del orden de las filas ni de sus identificadores.</summary>
    internal static string Cifras(object? resultado)
    {
        var cifras = new List<string>();
        void Recorrer(JsonElement elemento)
        {
            switch (elemento.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var propiedad in elemento.EnumerateObject())
                        Recorrer(propiedad.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var hijo in elemento.EnumerateArray())
                        Recorrer(hijo);
                    break;
                case JsonValueKind.Number:
                    cifras.Add(elemento.GetRawText());
                    break;
            }
        }

        using var documento = JsonDocument.Parse(Json(resultado));
        Recorrer(documento.RootElement);
        return string.Join(",", cifras.Order(StringComparer.Ordinal));
    }

    internal static async Task<IReadOnlyDictionary<string, string>> HuellasAsync(IServiceProvider sp, IdsFicha ficha)
    {
        var sender = sp.GetRequiredService<ISender>();
        var huellas = new Dictionary<string, string>();
        foreach (var (pantalla, lectura) in DeTenant)
        {
            if (Huella(pantalla, await lectura(sender, ficha)) is { } huella)
                huellas[pantalla] = huella;
            sp.GetRequiredService<CaeManagerDbContext>().ChangeTracker.Clear();
        }
        return huellas;
    }

    /// <summary>Si la lectura trajo algo: un listado con filas, una pantalla de cifras con alguna distinta de cero, o un resultado con identificadores.</summary>
    internal static bool TraeFilas(string pantalla, object? resultado) => resultado switch
    {
        null => false,
        _ when resultado.GetType().GetProperty(nameof(ResultadoPaginado<int>.TotalElementos))?.GetValue(resultado) is int total => total > 0,
        _ when SoloCifras.Contains(pantalla) => Cifras(resultado).Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(c => double.Parse(c, System.Globalization.CultureInfo.InvariantCulture) != 0),
        _ => Guids(Json(resultado)).Count > 0,
    };

    internal static string Json(object? resultado) =>
        resultado is null ? "null" : JsonSerializer.Serialize(resultado, new JsonSerializerOptions { MaxDepth = 256 });

    internal static HashSet<Guid> Guids(string json) =>
        [.. Regex.Matches(json, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Select(m => Guid.Parse(m.Value))];

    private static string Pares(IReadOnlyDictionary<string, int>? pares) =>
        pares is null ? "-" : string.Join(", ", pares.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
}

/// <summary>
/// Dos protecciones sobre las pantallas del piloto, con la identidad de aplicación (cae_app_runtime y contexto
/// RLS firmado) y el Gestor CAE que lleva cada Tenant beneficiario en su cartera:
/// <list type="bullet">
/// <item>presupuesto de SQL por query de pantalla, medido con esta misma siembra: una regresión de N+1 o una
/// lectura de más sale en rojo aquí antes de notarse en la base de 500.000 Documentos, cuyas cifras están en el
/// informe MEDICION-ESCALA-500K-2026-10-24 del repositorio de documentación;</item>
/// <item>aislamiento de filas entre dos Operadores CAE externos y sus carteras: ninguna pantalla, ficha 360,
/// buscador global, recuento de chips ni lectura por identificador de uno devuelve nada del otro, en los dos
/// sentidos, aunque sus Empresas compartan CIF; y la RLS sola, sin el filtro de EF, tampoco deja ver filas del
/// otro.</item>
/// </list>
/// Límites conocidos: las lecturas de Tenant van con el ámbito explícito del Tenant beneficiario, así que no
/// ejercitan qué Tenant puede activar un Gestor CAE (la selección de cartera tiene sus propios tests; Mi trabajo y
/// el Cuadro de dirección sí van por el camino real, sin ámbito); y las tablas <c>Eventos%</c> y <c>Registros%</c>
/// no se clonan, así que en la copia la Conversación no tiene eventos.
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class EscalaPresupuestoSqlYAislamientoTests(EscalaYAislamientoFixture fixture, ITestOutputHelper salida)
    : IClassFixture<EscalaYAislamientoFixture>
{
    private const string TenantMedido = "Mantenimientos Integrales Corvesta, S.A.";

    /// <summary>
    /// Sentencias SQL por query, medidas con esta misma siembra y con un ámbito nuevo por query tras una ejecución
    /// de calentamiento, con un margen del 10 % (mínimo 2) sobre lo medido para no depender del orden de siembra. Son
    /// techos: bajar una cifra es bienvenido (y se actualiza aquí); subirla exige explicar en la PR por qué la
    /// pantalla necesita una lectura más. El tercer valor dice si en el Tenant medido la lectura trae filas: un
    /// techo medido sobre un resultado vacío solo cubre el camino corto, y se marca para que nadie lo lea como el
    /// coste de la pantalla llena.
    /// </summary>
    public static TheoryData<string, int, bool> Presupuestos => new()
    {
        { "Documentos", 6, true }, // medido: 4
        { "Documentos vencidos por vencimiento", 5, false }, // medido: 3; el Tenant medido no tiene vencidos
        { "Trabajadores", 14, true }, // medido: 12
        // medido: 19 en la primera medición; 22 en CI con la PR 7 (el techo era 21). Esta pantalla pide el estado completo y el Tenant medido no está relleno (no
        // tiene fila de versión, como hoy todos los Tenants reales), así que el lector de Empresa y Trabajador paga UNA sentencia
        // de sondeo (fila de versión y cola vacía, en una sola consulta) y vuelve al cálculo de siempre. En un Tenant completo
        // la fila sustituye al cálculo del peor documento y de los requisitos incumplidos de todas las Empresas del listado.
        { "Empresas", 22, true },
        { "Centros", 33, true }, // medido: 30
        { "Centros bloqueados", 33, false }, // medido: 30; ningún Centro bloqueado en el Tenant medido
        { "Subcontratas", 13, true }, // medido: 11
        { "Clientes", 31, true }, // medido: 28
        { "Visitas", 11, true }, // medido: 9
        { "Conversaciones", 18, true }, // medido: 16
        { "Conversación", 22, true }, // medido: 20
        { "Gestiones", 5, false }, // medido: 3; el Tenant medido no tiene Gestiones
        { "Alertas", 11, false }, // medido: 10 (9 + la sentencia de sondeo del lector); sin alertas en el Tenant medido
        { "Inicio: KPIs", 26, true }, // medido: 24 (23 + sondeo)
        { "Inicio: próximas visitas", 10, true }, // medido: 8
        { "Inicio: bandeja agrupada", 33, true }, // medido: 31 (30 + sondeo)
        { "Buscador global", 8, true }, // medido: 6 en CI, donde «ser» sí encuentra resultados en el Tenant medido
        { "Centro 360", 7, true }, // medido: 5
        { "Centro 360: documentación de Empresas", 20, true }, // medido: 18
        { "Centro 360: documentación requerida", 6, true }, // medido: 4
        { "Centro 360: trabajadores asignados", 8, true }, // medido: 6
        { "Empresa 360", 8, true }, // medido: 6
        { "Empresa 360: cumplimiento", 11, true }, // medido: 9
        { "Empresa 360: trabajadores", 14, true }, // medido: 12
        { "Trabajador 360", 6, true }, // medido: 4
        { "Trabajador 360: documentación por centro", 17, true }, // medido: 15
        { "Trabajador 360: documentos", 6, true }, // medido: 4
        { "Cliente 360", 5, true }, // medido: 3
        { "Cliente 360: resumen", 5, true }, // medido: 3
        { "Cliente 360: centros", 35, true }, // medido: 31
        { "Cliente 360: empresas", 3, true }, // medido: 1
    };

    [Theory]
    [MemberData(nameof(Presupuestos))]
    public async Task Cada_query_de_pantalla_cabe_en_su_presupuesto_de_SQL(string query, int maximo, bool conFilas)
    {
        var ficha = fixture.FichaDe(fixture.Piloto, TenantMedido);
        var (sentencias, resultado) = await fixture.ComoGestorEnAsync(fixture.Piloto, TenantMedido,
            sp => ContarAsync(sp, s => LecturasDePantalla.DeTenant[query](s, ficha)));

        salida.WriteLine($"{query}: {sentencias} sentencias (presupuesto {maximo})");
        LecturasDePantalla.TraeFilas(query, resultado).Should().Be(conFilas,
            conFilas ? $"«{query}» se mide con la pantalla llena, no con el camino vacío" : $"«{query}» está marcada como medida sobre un resultado vacío");
        sentencias.Should().BeLessThanOrEqualTo(maximo, $"«{query}» tenía un presupuesto de {maximo} sentencias SQL y ha emitido {sentencias}");
    }

    [Fact]
    public async Task Mi_trabajo_y_el_Cuadro_de_direccion_caben_en_su_presupuesto_de_SQL()
    {
        var (miTrabajo, resultadoMiTrabajo) = await fixture.Arnes.ComoCuentaAsync(CatalogoPilotoOutbound.NombreTenantOperador, CuentasPilotoOutbound.Locales.GestoraPrimera,
            sp => ContarAsync(sp, async s => await s.Send(new ObtenerMiTrabajoAgregadoQuery())));
        var hoy = DiaDeNegocio.Hoy();
        var (cuadro, resultadoCuadro) = await fixture.Arnes.ComoCuentaAsync(CatalogoPilotoOutbound.NombreTenantOperador, fixture.CorreoDireccion,
            sp => ContarAsync(sp, async s => await s.Send(new ObtenerCuadroDireccionQuery(hoy.Year, hoy.Month))));

        salida.WriteLine($"Mi trabajo: {miTrabajo}; Cuadro de dirección: {cuadro}");
        LecturasDePantalla.Guids(LecturasDePantalla.Json(resultadoMiTrabajo)).Should().IntersectWith(fixture.Piloto.Ids, "Mi trabajo se mide con la cartera llena");
        LecturasDePantalla.Guids(LecturasDePantalla.Json(resultadoCuadro)).Should().IntersectWith(fixture.Piloto.Ids, "el Cuadro de dirección se mide con sus Empresas gestionadas");
        // Medido: 275 (270 + una sentencia de sondeo por Tenant beneficiario) y 533. Las dos crecen con la cartera (una lectura por Empresa gestionada): el techo congela el N+1
        // actual para que no empeore; bajarlo es el trabajo pendiente que recoge el informe de escala.
        miTrabajo.Should().BeLessThanOrEqualTo(277, "presupuesto de Mi trabajo (medido 275, margen de 2 como el resto de la tabla)");
        cuadro.Should().BeLessThanOrEqualTo(587, "presupuesto del Cuadro de dirección");
    }

    /// <summary>
    /// PR 9 del estado materializado: el ahorro de sentencias de las colas de trabajo con los Tenants completos (rellenos). Los
    /// presupuestos de arriba se miden con el Tenant sin rellenar, como hoy están todos los Tenants reales (sin fila de
    /// versión): ahí el lector paga una sentencia de sondeo y vuelve al cálculo. Aquí se mide la misma lectura con los Tenants
    /// del piloto rellenos y se compara con la medida sin rellenar; el Tenant queda sin fila de versión al terminar.
    /// </summary>
    [Fact]
    public async Task Con_los_Tenants_completos_las_colas_de_trabajo_caben_en_su_presupuesto_de_SQL()
    {
        string[] pantallas = ["Alertas", "Inicio: KPIs", "Inicio: bandeja agrupada", "Empresas"];
        var ficha = fixture.FichaDe(fixture.Piloto, TenantMedido);

        async Task<Dictionary<string, int>> MedirAsync()
        {
            var medidas = new Dictionary<string, int>();
            foreach (var pantalla in pantallas)
                medidas[pantalla] = (await fixture.ComoGestorEnAsync(fixture.Piloto, TenantMedido,
                    sp => ContarAsync(sp, s => LecturasDePantalla.DeTenant[pantalla](s, ficha)))).Sentencias;
            medidas["Mi trabajo"] = (await fixture.Arnes.ComoCuentaAsync(CatalogoPilotoOutbound.NombreTenantOperador, CuentasPilotoOutbound.Locales.GestoraPrimera,
                sp => ContarAsync(sp, async s => await s.Send(new ObtenerMiTrabajoAgregadoQuery())))).Sentencias;
            return medidas;
        }

        var sinRellenar = await MedirAsync();
        Dictionary<string, int> rellenos;
        try
        {
            foreach (var tenant in fixture.Piloto.Tenants.Values)
                await fixture.Arnes.EnTenantAsync(tenant, (_, sp) =>
                    sp.GetRequiredService<IRecalculoEstadoMaterializado>().RellenarTenantAsync(CancellationToken.None));
            rellenos = await MedirAsync();
        }
        finally
        {
            foreach (var tenant in fixture.Piloto.Tenants.Values)
                await fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
                {
                    await db.Set<VersionEstadoTenant>().ExecuteDeleteAsync();
                    return 0;
                });
        }

        foreach (var (pantalla, sin) in sinRellenar)
            salida.WriteLine($"{pantalla}: {sin} sentencias sin rellenar, {rellenos[pantalla]} con los Tenants completos");

        // Medido en CI (sin rellenar -> con los Tenants completos): Alertas 10 -> 6, Inicio: KPIs 24 -> 11, Inicio: bandeja
        // agrupada 31 -> 27, Mi trabajo 275 -> 243. Cada techo es lo medido con completos + 1: el margen cubre una sentencia
        // de sondeo de un Tenant más del piloto, no una consulta nueva por pantalla.
        // «Empresas» (PR 7) es la excepción conocida: sin rellenar 22 y con el Tenant completo 25, porque el lector de Empresas
        // paga más sentencias que el cálculo en el piloto. No se exige que baje; se congela para que no empeore.
        foreach (var (pantalla, maximo, debeBajar) in new (string, int, bool)[]
        {
            ("Alertas", 7, true),
            ("Inicio: KPIs", 12, true),
            ("Inicio: bandeja agrupada", 28, true),
            ("Mi trabajo", 244, true),
            ("Empresas", 26, false),
        })
        {
            if (debeBajar)
                rellenos[pantalla].Should().BeLessThan(sinRellenar[pantalla],
                    $"«{pantalla}» con los Tenants completos lee de las tablas: {rellenos[pantalla]} sentencias frente a {sinRellenar[pantalla]} sin rellenar");
            rellenos[pantalla].Should().BeLessThanOrEqualTo(maximo, $"presupuesto de «{pantalla}» con los Tenants completos");
        }
    }

    /// <summary>
    /// La búsqueda de Documentos resuelve el texto dentro de cada rama, por los titulares y los Tipos que casan, y no
    /// sobre la unión de las cinco ramas (medición de escala: 9,3 s con 500.000 Documentos). El oráculo es la forma
    /// anterior: el universo sin buscar, con el mismo orden, filtrado por «PropietarioNombre o TipoDocumentoNombre
    /// contienen el texto». Tiene que dar las mismas filas, en el mismo orden, con la misma página y los mismos
    /// recuentos por estado, para un texto que case en cada rama presente (Trabajador, Empresa, Vehículo, Cliente,
    /// Proyecto), uno que case por Tipo, uno que cruce el espacio entre nombre y apellidos y otros fijos.
    /// </summary>
    [Fact]
    public async Task La_busqueda_de_Documentos_por_titulares_da_lo_mismo_que_filtrar_la_union()
    {
        var fallos = new List<string>();
        var ramasConCoincidencias = new HashSet<AmbitoAplicacion>();
        var ramasPresentes = new HashSet<AmbitoAplicacion>();
        var comparaciones = 0;

        foreach (var nombre in fixture.Piloto.Tenants.Keys.Order(StringComparer.Ordinal))
        {
            await fixture.ComoGestorEnAsync(fixture.Piloto, nombre, async sp =>
            {
                var sender = sp.GetRequiredService<ISender>();
                var universo = (await sender.Send(new ObtenerDocumentosQuery(null, null, null, TamanoPagina: TodoElTenant))).Elementos;
                var universoPorSeveridad = (await sender.Send(new ObtenerDocumentosQuery(null, null, null, TamanoPagina: TodoElTenant,
                    OrdenarPor: ObtenerDocumentosQuery.OrdenPorSeveridad))).Elementos;
                ramasPresentes.UnionWith(universo.Select(d => d.Ambito));

                foreach (var (rotulo, termino) in TerminosDeBusqueda(universo))
                {
                    var caso = $"{nombre}, {rotulo} «{termino}»";
                    bool Casa(DocumentoListaDto d) =>
                        TextoDeBusqueda.Contiene(d.PropietarioNombre, termino) || TextoDeBusqueda.Contiene(d.TipoDocumentoNombre, termino);
                    var esperado = universo.Where(Casa).ToList();
                    ramasConCoincidencias.UnionWith(esperado.Select(d => d.Ambito));

                    var completa = await sender.Send(new ObtenerDocumentosQuery(null, null, termino, TamanoPagina: TodoElTenant, ConRecuentosPorEstado: true));
                    if (!completa.Elementos.Select(d => d.Id).SequenceEqual(esperado.Select(d => d.Id)))
                        fallos.Add($"{caso}: filas u orden distintos ({completa.Elementos.Count} frente a {esperado.Count})");
                    if (completa.TotalElementos != esperado.Count)
                        fallos.Add($"{caso}: total {completa.TotalElementos} frente a {esperado.Count}");
                    var recuentosEsperados = EstadoDocumentalFiltro.RecuentosPorEstado(esperado
                        .GroupBy(d => EstadoDocumentalFiltro.ClaveOrden(d.Estado)).ToDictionary(g => g.Key, g => g.Count()));
                    if (completa.RecuentosPorEstado is null || !completa.RecuentosPorEstado.OrderBy(x => x.Key).SequenceEqual(recuentosEsperados.OrderBy(x => x.Key)))
                        fallos.Add($"{caso}: recuentos por estado distintos");

                    var pagina = await sender.Send(new ObtenerDocumentosQuery(null, null, termino, Pagina: 2, TamanoPagina: 5));
                    if (!pagina.Elementos.Select(d => d.Id).SequenceEqual(esperado.Skip(5).Take(5).Select(d => d.Id)))
                        fallos.Add($"{caso}: la página 2 de 5 no es la de la forma anterior");

                    var porSeveridad = await sender.Send(new ObtenerDocumentosQuery(null, null, termino, TamanoPagina: TodoElTenant,
                        OrdenarPor: ObtenerDocumentosQuery.OrdenPorSeveridad));
                    if (!porSeveridad.Elementos.Select(d => d.Id).SequenceEqual(universoPorSeveridad.Where(Casa).Select(d => d.Id)))
                        fallos.Add($"{caso}: el orden por severidad no es el de la forma anterior");
                    comparaciones++;
                }
                return 0;
            });
        }

        salida.WriteLine($"{comparaciones} comparaciones; ramas con coincidencias: {string.Join(", ", ramasConCoincidencias.Order())}");
        // Control positivo: cada rama que la siembra tiene se ha buscado con un texto que casa en ella (vacío ≠ igual).
        ramasPresentes.Should().Contain([AmbitoAplicacion.Trabajador, AmbitoAplicacion.Empresa], "la siembra del piloto tiene Documentos de Trabajador y de Empresa");
        ramasConCoincidencias.Should().BeEquivalentTo(ramasPresentes, "cada rama presente se busca con un texto que casa en ella");
        fallos.Should().BeEmpty();
    }

    /// <summary>
    /// Buscar solo estrecha lo que el usuario ya ve. En el contexto delegado del Operador CAE externo sobre cada
    /// Tenant beneficiario, el Gestor CAE con ese Tenant en su Asignación de Cartera encuentra Documentos propios
    /// (control positivo) y ninguno del otro Operador CAE externo, aunque sus titulares se llamen igual; y el Gestor
    /// CAE que no lo lleva en su cartera no encuentra nada que no vea ya sin buscar.
    /// </summary>
    [Fact]
    public async Task La_busqueda_de_Documentos_no_sale_de_la_cartera_ni_del_Operador_CAE_externo()
    {
        var fallos = new List<string>();
        foreach (var nombre in fixture.Piloto.Tenants.Keys.Order(StringComparer.Ordinal))
        {
            foreach (var lado in new[] { fixture.Piloto, fixture.Otro })
            {
                var otro = fixture.ElOtro(lado);
                var conCartera = lado.CorreoGestorDe(nombre);
                var sinCartera = conCartera == lado.CorreoGestoraPrimera
                    ? lado.PrefijoCorreo + CuentasPilotoOutbound.Locales.GestorSegundo
                    : lado.CorreoGestoraPrimera;

                var (universo, termino, encontrados) = await LeerConBusquedaAsync(lado, nombre, conCartera, null);
                if (termino is null)
                    continue;
                if (encontrados.Count == 0)
                    fallos.Add($"{lado.Rotulo}, {nombre}: «{termino}» no encuentra ningún Documento propio (control positivo)");
                if (!encontrados.IsSubsetOf(universo))
                    fallos.Add($"{lado.Rotulo}, {nombre}: «{termino}» devuelve Documentos que el Gestor CAE de la cartera no ve sin buscar");
                if (encontrados.Overlaps(otro.Ids))
                    fallos.Add($"{lado.Rotulo}→{otro.Rotulo}, {nombre}: «{termino}» devuelve Documentos del otro Operador CAE externo");

                // El Tenant de cartera compartida lo llevan los dos Gestores CAE: no hay Gestor CAE sin él.
                if (CatalogoPilotoOutbound.Tenants.Single(t => t.Nombre == nombre).Cartera == CarteraPilotoOutbound.Compartida)
                    continue;
                var (universoAjeno, _, encontradosAjeno) = await LeerConBusquedaAsync(lado, nombre, sinCartera, termino);
                if (!encontradosAjeno.IsSubsetOf(universoAjeno))
                    fallos.Add($"{lado.Rotulo}, {nombre}: «{termino}» enseña al Gestor CAE sin el Tenant en su cartera Documentos que no ve sin buscar");
                if (encontradosAjeno.Overlaps(otro.Ids))
                    fallos.Add($"{lado.Rotulo}→{otro.Rotulo}, {nombre}: «{termino}» enseña al Gestor CAE sin cartera Documentos del otro Operador CAE externo");
                salida.WriteLine($"{lado.Rotulo}, {nombre}, «{termino}»: {encontrados.Count} de {universo.Count} con cartera; {encontradosAjeno.Count} de {universoAjeno.Count} sin ella");
            }
        }

        fallos.Should().BeEmpty();
    }

    private const int TodoElTenant = 100_000;

    /// <summary>Universo sin buscar y resultado de buscar, como <paramref name="correo"/> en el ámbito del Tenant. Negar por autorización cuenta como «no ve nada».</summary>
    private Task<(HashSet<Guid> Universo, string? Termino, HashSet<Guid> Encontrados)> LeerConBusquedaAsync(
        LadoOperadorCae lado, string nombreTenant, string correo, string? termino) =>
        fixture.Arnes.ComoCuentaAsync(lado.NombreTenantOperador, correo, async sp =>
        {
            using (AmbitoTenantExplicito.Establecer(lado.Tenants[nombreTenant]))
            {
                var sender = sp.GetRequiredService<ISender>();
                try
                {
                    var universo = (await sender.Send(new ObtenerDocumentosQuery(null, null, null, TamanoPagina: TodoElTenant))).Elementos;
                    termino ??= universo.Where(d => d.Ambito == AmbitoAplicacion.Trabajador).OrderBy(d => d.Id)
                        .Select(d => PalabraMasLarga(d.PropietarioNombre)).FirstOrDefault();
                    if (termino is null)
                        return (universo.Select(d => d.Id).ToHashSet(), (string?)null, new HashSet<Guid>());
                    var encontrados = (await sender.Send(new ObtenerDocumentosQuery(null, null, termino, TamanoPagina: TodoElTenant))).Elementos;
                    return (universo.Select(d => d.Id).ToHashSet(), termino, encontrados.Select(d => d.Id).ToHashSet());
                }
                catch (UnauthorizedAccessException)
                {
                    return (new HashSet<Guid>(), termino, new HashSet<Guid>());
                }
            }
        });

    /// <summary>Textos de búsqueda sacados de los datos: uno por rama (por titular), uno que cruza el primer espacio del titular, uno por Tipo y unos fijos.</summary>
    private static IEnumerable<(string Rotulo, string Termino)> TerminosDeBusqueda(IReadOnlyList<DocumentoListaDto> universo)
    {
        foreach (var rama in universo.GroupBy(d => d.Ambito).OrderBy(g => g.Key))
        {
            var titular = rama.OrderBy(d => d.Id).First().PropietarioNombre;
            yield return ($"titular de {rama.Key}", PalabraMasLarga(titular));
            var espacio = titular.IndexOf(' ', StringComparison.Ordinal);
            if (espacio >= 2 && espacio + 3 <= titular.Length)
                yield return ($"titular de {rama.Key} a los dos lados del espacio", titular[(espacio - 2)..(espacio + 3)].ToLowerInvariant());
            // El Vehículo se pinta «Nombre (Matrícula)»: un texto que cruza « (» solo casa con la expresión unida.
            var parentesis = titular.IndexOf(" (", StringComparison.Ordinal);
            if (parentesis >= 2 && parentesis + 4 <= titular.Length)
                yield return ($"titular de {rama.Key} a los dos lados de « (»", titular[(parentesis - 2)..(parentesis + 4)].ToLowerInvariant());
        }
        if (universo.Count > 0)
            yield return ("Tipo de documento", PalabraMasLarga(universo.OrderBy(d => d.Id).First().TipoDocumentoNombre));
        yield return ("fijo", "ser");
        yield return ("fijo", "formacion");
        yield return ("fijo, sin coincidencias", "zzqxw");
        yield return ("fijo, comodín de LIKE", "%");
    }

    /// <summary>La palabra más larga del texto, sin acentos y en minúsculas: casa con él sin acentos ni mayúsculas.</summary>
    private static string PalabraMasLarga(string texto) =>
        TextoDeBusqueda.Normalizar(texto).Split([' ', '(', ')', ',', '.', '-'], StringSplitOptions.RemoveEmptyEntries)
            .OrderByDescending(p => p.Length).ThenBy(p => p, StringComparer.Ordinal)
            .DefaultIfEmpty(TextoDeBusqueda.Normalizar(texto)).First().ToLowerInvariant();

    [Fact]
    public async Task Desde_cada_Tenant_de_un_Operador_CAE_externo_ninguna_pantalla_devuelve_filas_ni_recuentos_del_otro_en_los_dos_sentidos()
    {
        var fallos = new List<string>();

        foreach (var nombre in fixture.Piloto.Tenants.Keys.Order(StringComparer.Ordinal))
        {
            foreach (var lado in new[] { fixture.Piloto, fixture.Otro })
            {
                var otro = fixture.ElOtro(lado);
                var sentido = $"{lado.Rotulo}→{otro.Rotulo}, {nombre}";
                var (fugas, propias, huellas) = await LeerTodoAsync(lado, nombre, otro.Ids);
                fallos.AddRange(fugas.Select(f => $"{sentido}: {f}"));

                // Control positivo por Tenant y por sentido: un Gestor CAE que no viera nada daría verde (vacío ≠ negativo).
                if (propias == 0)
                    fallos.Add($"{sentido}: el Gestor CAE de la cartera no ve ninguna fila propia");

                // Oráculo de los recuentos de chips: los de antes de clonar, cuando no había nada ajeno que contar.
                foreach (var (pantalla, esperada) in fixture.HuellasAntesDeClonar[nombre])
                {
                    var vista = huellas.GetValueOrDefault(pantalla);
                    if (vista != esperada)
                        fallos.Add($"{sentido}: los recuentos de «{pantalla}» eran «{esperada}» antes de clonar y ahora son «{vista ?? "nada"}»");
                }
            }
        }

        fixture.HuellasAntesDeClonar.Values.Sum(h => h.Count).Should().BeGreaterThan(fixture.Piloto.Tenants.Count * 10,
            "el oráculo cubre los listados y las cifras de cada Tenant beneficiario");
        fallos.Should().BeEmpty();
    }

    [Fact]
    public async Task Pedir_por_identificador_una_ficha_del_otro_Operador_CAE_externo_no_devuelve_nada()
    {
        var fallos = new List<string>();
        var propiasLeidas = 0;

        foreach (var nombre in fixture.Piloto.Tenants.Keys.Order(StringComparer.Ordinal))
        {
            foreach (var lado in new[] { fixture.Piloto, fixture.Otro })
            {
                var otro = fixture.ElOtro(lado);
                var fichaPropia = fixture.FichaDe(lado, nombre);
                var fichaAjena = fixture.FichaDe(otro, nombre);
                await fixture.ComoGestorEnAsync(lado, nombre, async sp =>
                {
                    var sender = sp.GetRequiredService<ISender>();
                    foreach (var pantalla in LecturasDePantalla.FichasPorId)
                    {
                        var lectura = LecturasDePantalla.DeTenant[pantalla];
                        var propia = await lectura(sender, fichaPropia);
                        sp.GetRequiredService<CaeManagerDbContext>().ChangeTracker.Clear();
                        // Control positivo: con el identificador propio la ficha sí se lee. Sin identificador en la
                        // ficha (un Tenant sin Conversaciones) no hay nada que pedir, ni propio ni ajeno.
                        if (!TieneId(fichaPropia, pantalla))
                            continue;
                        if (propia is null)
                            fallos.Add($"{lado.Rotulo}, {nombre}: «{pantalla}» no lee ni la ficha propia (control positivo)");
                        else
                            propiasLeidas++;

                        object? ajena;
                        try
                        {
                            ajena = await lectura(sender, fichaAjena);
                        }
                        catch (UnauthorizedAccessException excepcion)
                        {
                            // Negar por autorización tampoco devuelve nada. Cualquier otra excepción es un fallo del
                            // handler y deja el test en rojo: no se confunde con una negación.
                            salida.WriteLine($"{lado.Rotulo}, {nombre}, {pantalla}: {excepcion.GetType().Name}");
                            ajena = null;
                        }
                        sp.GetRequiredService<CaeManagerDbContext>().ChangeTracker.Clear();
                        if (ajena is not null)
                            fallos.Add($"{lado.Rotulo}→{otro.Rotulo}, {nombre}: «{pantalla}» devuelve la ficha ajena ({LecturasDePantalla.Json(ajena)[..Math.Min(120, LecturasDePantalla.Json(ajena).Length)]})");
                    }
                    return 0;
                });
            }
        }

        // El control positivo de verdad es por ficha (arriba); esto solo descarta que no se haya pedido ninguna.
        propiasLeidas.Should().BeGreaterThan(0, "cada Gestor CAE lee las fichas propias por identificador");
        fallos.Should().BeEmpty();
    }

    private static bool TieneId(IdsFicha ficha, string pantalla) => pantalla switch
    {
        "Centro 360" => ficha.Centro is not null,
        "Empresa 360" => ficha.Empresa is not null,
        "Trabajador 360" => ficha.Trabajador is not null,
        "Cliente 360" => ficha.Cliente is not null,
        "Conversación" => ficha.Conversacion is not null,
        _ => throw new ArgumentOutOfRangeException(nameof(pantalla), pantalla, "ficha por identificador sin traducir a su campo"),
    };

    [Fact]
    public async Task Mi_trabajo_y_el_Cuadro_de_direccion_no_cruzan_de_un_Operador_CAE_externo_al_otro()
    {
        var hoy = DiaDeNegocio.Hoy();
        foreach (var lado in new[] { fixture.Piloto, fixture.Otro })
        {
            var otro = fixture.ElOtro(lado);
            var miTrabajo = await fixture.Arnes.ComoCuentaAsync(lado.NombreTenantOperador, lado.CorreoGestoraPrimera,
                async sp => LecturasDePantalla.Json(await sp.GetRequiredService<ISender>().Send(new ObtenerMiTrabajoAgregadoQuery())));
            var cuadro = await fixture.Arnes.ComoCuentaAsync(lado.NombreTenantOperador, lado.PrefijoCorreo + fixture.CorreoDireccion,
                async sp => LecturasDePantalla.Json(await sp.GetRequiredService<ISender>().Send(new ObtenerCuadroDireccionQuery(hoy.Year, hoy.Month))));

            LecturasDePantalla.Guids(miTrabajo).Should().NotIntersectWith(otro.Ids, $"Mi trabajo de {lado.NombreTenantOperador} solo lleva Tenants de su cartera");
            LecturasDePantalla.Guids(miTrabajo).Should().IntersectWith(lado.Ids, $"Mi trabajo de {lado.NombreTenantOperador} lleva su propia cartera");
            LecturasDePantalla.Guids(cuadro).Should().NotIntersectWith(otro.Ids, $"el Cuadro de dirección de {lado.NombreTenantOperador} solo cuenta su cartera");
            LecturasDePantalla.Guids(cuadro).Should().IntersectWith(lado.Ids, $"el Cuadro de dirección de {lado.NombreTenantOperador} lleva sus Empresas gestionadas");
            (await fixture.CifrasCuadroAsync(lado)).Should().Be(fixture.CifrasCuadroAntesDeClonar,
                $"las cifras del Cuadro de dirección de {lado.NombreTenantOperador} son las de antes de existir el otro Operador CAE externo");
            // Mi trabajo solo lleva recuentos por Tenant: un recuento inflado con filas de la copia no traería ningún
            // identificador ajeno, así que se compara con sus cifras de antes de clonar.
            (await fixture.CifrasMiTrabajoAsync(lado)).Should().Be(fixture.CifrasMiTrabajoAntesDeClonar,
                $"los recuentos de Mi trabajo de {lado.NombreTenantOperador} son los de antes de existir el otro Operador CAE externo");
        }
    }

    [Fact]
    public async Task Sin_el_filtro_de_EF_la_RLS_sola_no_deja_ver_filas_del_otro_Operador_CAE_externo()
    {
        // Las pantallas pasan por el filtro global de EF y por la RLS a la vez: con las políticas rotas seguirían en
        // verde. Aquí se quita el filtro de EF y se pide expresamente el Tenant de la copia.
        var fallos = new List<string>();
        foreach (var nombre in fixture.Piloto.Tenants.Keys.Order(StringComparer.Ordinal))
        {
            foreach (var lado in new[] { fixture.Piloto, fixture.Otro })
            {
                var propio = lado.Tenants[nombre];
                var ajeno = fixture.ElOtro(lado).Tenants[nombre];
                var (empresasPropias, empresasAjenas, trabajadoresAjenos, documentosAjenos) = await fixture.Arnes.EnTenantAsync(propio, async (db, _) => (
                    await db.Empresas.IgnoreQueryFilters().CountAsync(e => e.TenantId == propio),
                    await db.Empresas.IgnoreQueryFilters().CountAsync(e => e.TenantId == ajeno),
                    await db.Trabajadores.IgnoreQueryFilters().CountAsync(t => t.TenantId == ajeno),
                    await db.Documentos.IgnoreQueryFilters().CountAsync(d => d.TenantId == ajeno)));

                if (empresasPropias == 0)
                    fallos.Add($"{lado.Rotulo}, {nombre}: la RLS no deja ver ni las Empresas propias (control positivo)");
                if (empresasAjenas + trabajadoresAjenos + documentosAjenos > 0)
                    fallos.Add($"{lado.Rotulo}, {nombre}: la RLS deja ver {empresasAjenas} Empresas, {trabajadoresAjenos} Trabajadores y {documentosAjenos} Documentos del otro Operador CAE externo");
            }
        }

        fallos.Should().BeEmpty();
    }

    [Fact]
    public async Task El_mismo_CIF_en_los_dos_Operadores_CAE_externos_son_registros_distintos_y_cada_Tenant_encuentra_solo_el_suyo()
    {
        var (nombre, cif) = await CifDeEmpresaAsync(soloEnUnTenant: false);
        var idPiloto = await EmpresaPorCifAsync(fixture.Piloto.Tenants[nombre], cif);
        var idOtro = await EmpresaPorCifAsync(fixture.Otro.Tenants[nombre], cif);
        idPiloto.Should().NotBeNull();
        idOtro.Should().NotBeNull().And.NotBe(idPiloto!.Value, "cada Tenant propietario tiene su propio registro de la Empresa");

        var vistaDesdePiloto = await fixture.ComoGestorEnAsync(fixture.Piloto, nombre,
            async sp => await sp.GetRequiredService<ISender>().Send(new BuscarEmpresaPorCifQuery(cif)));
        var vistaDesdeOtro = await fixture.ComoGestorEnAsync(fixture.Otro, nombre,
            async sp => await sp.GetRequiredService<ISender>().Send(new BuscarEmpresaPorCifQuery(cif)));

        vistaDesdePiloto.Should().NotBeNull("el Gestor CAE de la cartera encuentra la Empresa de su Tenant");
        vistaDesdeOtro.Should().NotBeNull("el Gestor CAE de la copia encuentra la Empresa de la copia");
        vistaDesdePiloto!.Id.Should().Be(idPiloto.Value);
        vistaDesdeOtro!.Id.Should().Be(idOtro!.Value);
    }

    [Fact]
    public async Task Buscar_por_CIF_no_revela_si_la_Empresa_existe_en_la_cartera_del_otro_Operador_CAE_externo()
    {
        // Un CIF que solo tiene una Empresa del piloto, buscado desde un Tenant beneficiario del otro Operador CAE
        // externo que no la tiene: el resultado tiene que ser el mismo que el de un CIF que no existe en ningún sitio.
        var (nombre, cif) = await CifDeEmpresaAsync(soloEnUnTenant: true);
        var otroTenant = fixture.Otro.Tenants.Keys.Where(t => t != nombre).Order(StringComparer.Ordinal).First();

        var existente = await fixture.ComoGestorEnAsync(fixture.Otro, otroTenant, async sp => await sp.GetRequiredService<ISender>().Send(new BuscarEmpresaPorCifQuery(cif)));
        var inexistente = await fixture.ComoGestorEnAsync(fixture.Otro, otroTenant, async sp => await sp.GetRequiredService<ISender>().Send(new BuscarEmpresaPorCifQuery("B99999990")));
        var enSuCopia = await fixture.ComoGestorEnAsync(fixture.Otro, nombre, async sp => await sp.GetRequiredService<ISender>().Send(new BuscarEmpresaPorCifQuery(cif)));

        existente.Should().BeNull("la Empresa existe en la cartera del otro Operador CAE externo, y eso no se puede deducir desde aquí");
        inexistente.Should().BeNull();
        enSuCopia.Should().NotBeNull("control positivo: el CIF sí se encuentra en el Tenant que lo tiene");
    }

    private async Task<(IReadOnlyList<string> Fugas, int Propias, IReadOnlyDictionary<string, string> Huellas)> LeerTodoAsync(
        LadoOperadorCae lado, string nombre, HashSet<Guid> ajenos)
    {
        var ficha = fixture.FichaDe(lado, nombre);
        return await fixture.ComoGestorEnAsync(lado, nombre, async sp =>
        {
            var fugas = new List<string>();
            var propias = 0;
            var huellas = new Dictionary<string, string>();
            var sender = sp.GetRequiredService<ISender>();
            foreach (var (pantalla, lectura) in LecturasDePantalla.DeTenant)
            {
                var resultado = await lectura(sender, ficha);
                var vistos = LecturasDePantalla.Guids(LecturasDePantalla.Json(resultado));
                var cruzados = vistos.Where(ajenos.Contains).ToList();
                if (cruzados.Count > 0)
                    fugas.Add($"{pantalla} devuelve {cruzados.Count} identificadores ajenos, p. ej. {cruzados[0]}");
                propias += vistos.Count(lado.Ids.Contains);
                if (LecturasDePantalla.Huella(pantalla, resultado) is { } huella)
                    huellas[pantalla] = huella;
                sp.GetRequiredService<CaeManagerDbContext>().ChangeTracker.Clear();
            }
            return ((IReadOnlyList<string>)fugas, propias, (IReadOnlyDictionary<string, string>)huellas);
        });
    }

    private static async Task<(int Sentencias, object? Resultado)> ContarAsync(IServiceProvider sp, Func<ISender, Task<object?>> lectura)
    {
        var sender = sp.GetRequiredService<ISender>();
        var contexto = sp.GetRequiredService<CaeManagerDbContext>();
        await lectura(sender); // Calentamiento: compilación de EF y memorias del ámbito, como en una sesión abierta.
        contexto.ChangeTracker.Clear();
        var (resultado, sentencias) = await ArnesPilotoOutbound.Contador.ContarAsync(() => lectura(sender));
        contexto.ChangeTracker.Clear();
        return (sentencias, resultado);
    }

    /// <summary>
    /// El nombre del Tenant del piloto y el CIF de una de sus Empresas (opcionalmente, una cuyo CIF no tiene ningún
    /// otro Tenant del piloto), elegidos de forma determinista: por CIF y, a igual CIF, por Tenant.
    /// </summary>
    private async Task<(string Tenant, string Cif)> CifDeEmpresaAsync(bool soloEnUnTenant)
    {
        await using var conexion = new NpgsqlConnection(fixture.Arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(
            """
            SELECT e."TenantId", e."Cif" FROM "Empresas" e
            WHERE e."TenantId" = ANY(@piloto) AND e."Cif" IS NOT NULL AND NOT e."EstaEliminado"
              AND (NOT @solo OR NOT EXISTS (SELECT 1 FROM "Empresas" o WHERE o."Cif" = e."Cif" AND o."TenantId" = ANY(@piloto) AND o."TenantId" <> e."TenantId"))
            ORDER BY e."Cif", e."TenantId", e."Id" LIMIT 1
            """, conexion);
        comando.Parameters.AddWithValue("piloto", fixture.Piloto.Tenants.Values.ToArray());
        comando.Parameters.AddWithValue("solo", soloEnUnTenant);
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue("el piloto siembra Empresas con CIF");
        var tenant = lector.GetGuid(0);
        return (fixture.Piloto.Tenants.Single(t => t.Value == tenant).Key, lector.GetString(1));
    }

    private async Task<Guid?> EmpresaPorCifAsync(Guid tenant, string cif)
    {
        await using var conexion = new NpgsqlConnection(fixture.Arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand("""SELECT "Id" FROM "Empresas" WHERE "TenantId" = @t AND "Cif" = @cif ORDER BY "Id" LIMIT 1""", conexion);
        comando.Parameters.AddWithValue("t", tenant);
        comando.Parameters.AddWithValue("cif", cif);
        return await comando.ExecuteScalarAsync() as Guid?;
    }
}
