using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Operaciones;

/// <summary>
/// <c>app_operacion_inbound_amparada(uuid)</c> (B-02b; decisión de Chris del 2026-11-16, opción A): una Asignación de
/// Operación Inbound externa se ampara con la línea Inbound vigente del Tenant TCE, sin DelegacionTenant, y la persona
/// del Operador CAE externo lo pregunta desde su Tenant de origen, donde la RLS de <c>ProductosContratados</c> no le
/// deja leer esa línea. Bajo la identidad de runtime, con el contexto firmado.
/// </summary>
public class FuncionAmparoInboundBajoRuntimeTests : IAsyncLifetime
{
    private const string Funcion = "public.app_operacion_inbound_amparada(uuid)";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _operador = new("Operador CAE externo de prueba");
    private readonly Tenant _ajeno = new("Tenant ajeno al par");
    private readonly Dictionary<string, (Guid Propietario, Guid Operacion)> _casos = [];

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);
        var ahora = DateTime.UtcNow;
        await using (var contexto = ContextoPropietarioDeLaBase(tenant: null))
        {
            contexto.Tenants.AddRange(_operador, _ajeno);
            foreach (var caso in new[] { "con línea", "sin línea", "línea futura", "línea finalizada", "outbound con línea" })
            {
                var propietario = new Tenant($"Tenant TCE de prueba ({caso})");
                contexto.Tenants.Add(propietario);
                var servicio = caso == "outbound con línea" ? ServicioCae.Outbound : ServicioCae.Inbound;
                var operacion = AsignacionOperacion.Externa(
                    propietario.Id, _operador.Id, servicio, AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora);
                if (servicio == ServicioCae.Outbound)
                    OperacionExternaAmparada.Agregar(contexto, operacion);
                else
                    contexto.AsignacionesOperacion.Add(operacion); // sin DelegacionTenant: Inbound no la tiene
                _casos[caso] = (propietario.Id, operacion.Id);
            }

            await contexto.SaveChangesAsync();
        }

        foreach (var (caso, desde) in new (string, DateTime)[]
                 {
                     ("con línea", ahora.AddDays(-30)), ("línea futura", ahora.AddDays(1)),
                     ("línea finalizada", ahora.AddDays(-30)), ("outbound con línea", ahora.AddDays(-30)),
                 })
        {
            await using var contexto = ContextoPropietarioDeLaBase(_casos[caso].Propietario);
            var linea = ProductoContratado.Contratar(LineaProductoContratado.Inbound, "cus_PagadorAmparoInbound",
                TramoTrabajadoresActivos.De1A10, ModalidadSuscripcionAlta.Anual, ahora.AddDays(-30), ahora);
            // Finalizada por el dominio, que pone a la vez el estado y la fecha de fin (CK_ProductosContratados_EstadoYVigencia).
            if (caso == "línea finalizada")
                linea.Finalizar(ahora.AddDays(-1), ahora);
            contexto.ProductosContratados.Add(linea);
            await contexto.SaveChangesAsync();
            // El alta no admite un inicio lejano en el futuro: se mueve después, como lo vería una línea programada.
            if (desde > ahora)
                await contexto.ProductosContratados.Where(p => p.Id == linea.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.VigenteDesdeUtc, desde));
        }
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Desde_el_Tenant_de_origen_del_Operador_CAE_solo_ampara_la_Inbound_con_la_linea_vigente()
    {
        async Task<bool> DesdeElOperador(string caso) =>
            await ComoRuntimeAsync(_operador.Id, $"SELECT {Llamada(_casos[caso].Operacion)};");

        (await DesdeElOperador("con línea")).Should().BeTrue("la línea Inbound vigente del Tenant TCE la ampara, sin DelegacionTenant");
        (await DesdeElOperador("sin línea")).Should().BeFalse();
        (await DesdeElOperador("línea futura")).Should().BeFalse("la línea todavía no ha empezado");
        (await DesdeElOperador("línea finalizada")).Should().BeFalse("cerrada la línea, ninguna operación Inbound se ampara");
        (await DesdeElOperador("outbound con línea")).Should().BeFalse("una operación Outbound no se ampara por la línea Inbound");
        (await ComoRuntimeAsync(_operador.Id, $"SELECT {Llamada(Guid.NewGuid())};")).Should().BeFalse("una operación que no existe");
    }

    [Fact]
    public async Task Control_el_Operador_CAE_no_lee_la_linea_del_Tenant_TCE_bajo_la_RLS()
    {
        var (propietario, _) = _casos["con línea"];

        (await ComoRuntimeAsync(propietario,
                """SELECT EXISTS (SELECT 1 FROM public."ProductosContratados" WHERE "Linea" = 'Inbound');"""))
            .Should().BeTrue("control: desde su Tenant, el Tenant TCE sí la ve");
        (await ComoRuntimeAsync(_operador.Id,
                $"""SELECT EXISTS (SELECT 1 FROM public."ProductosContratados" WHERE "TenantId" = '{propietario}');"""))
            .Should().BeFalse("la RLS no deja al Operador CAE externo leer la línea: por eso responde la función");
    }

    [Fact]
    public async Task Solo_responde_a_un_extremo_de_la_operacion()
    {
        var (propietario, operacion) = _casos["con línea"];

        (await ComoRuntimeAsync(propietario, $"SELECT {Llamada(operacion)};")).Should().BeTrue("control: el Tenant TCE es extremo");
        (await ComoRuntimeAsync(_ajeno.Id, $"SELECT {Llamada(operacion)};")).Should().BeFalse(
            "un Tenant que no es extremo de la operación no la vería bajo la RLS, así que la función tampoco le responde");
    }

    [Fact]
    public async Task La_consulta_de_EF_traduce_el_amparo_Inbound_a_la_funcion()
    {
        await using var contexto = ContextoPropietarioDeLaBase(tenant: null);
        var ids = _casos.Values.Select(c => c.Operacion).ToList();

        var amparadas = await ((CaeManager.Application.Operaciones.IOperacionesQueryContext)contexto).OperacionesInboundAmparadas
            .Where(o => ids.Contains(o.Id)).Select(o => o.Id).ToListAsync();

        amparadas.Should().BeEquivalentTo([_casos["con línea"].Operacion]);
    }

    [Fact]
    public async Task Bajo_runtime_desde_el_Tenant_de_origen_la_via_Inbound_elige_y_revalida_la_operacion_con_linea()
    {
        var usuario = Guid.NewGuid();
        await using var contexto = ContextoRuntime(_operador.Id, usuario);
        var ahora = DateTime.UtcNow;

        foreach (var caso in _casos.Keys)
        {
            var (propietario, operacion) = _casos[caso];
            var elegida = await TenantsBeneficiariosAutorizados.OperacionInboundQueAutorizaAsync(
                contexto, usuario, _operador.Id, propietario, "Administrador", ahora, default);
            var servicio = await TenantsBeneficiariosAutorizados.ServicioQueAutorizaLaOperacionAsync(
                contexto, contexto, usuario, _operador.Id, propietario, operacion, "Administrador", ahora, default);

            if (caso == "con línea")
            {
                elegida.Should().Be(operacion, "la operación Inbound con la línea vigente se elige sin DelegacionTenant");
                servicio.Should().Be(ServicioCae.Inbound);
            }
            else if (caso == "outbound con línea")
            {
                elegida.Should().BeNull("una Outbound no entra en la vía Inbound");
                servicio.Should().Be(ServicioCae.Outbound, "control: la Outbound amparada por su origen sigue por su vía");
            }
            else
            {
                elegida.Should().BeNull(caso);
                servicio.Should().BeNull(caso);
            }
        }
    }

    [Fact]
    public async Task Bajo_runtime_el_Gestor_CAE_abre_con_papel_Inbound_solo_con_su_cartera_bajo_la_operacion()
    {
        var gestora = Guid.NewGuid();
        var (propietario, operacionId) = _casos["con línea"];
        await using (var propio = ContextoPropietarioDeLaBase(tenant: null))
        {
            var operacion = await propio.AsignacionesOperacion.SingleAsync(o => o.Id == operacionId);
            propio.AsignacionesCartera.Add(AsignacionCartera.Externa(
                operacion, gestora, "GestorCae", AmbitoAsignacion.Universal, DateTime.UtcNow.AddDays(-1), null, DateTime.UtcNow));
            await propio.SaveChangesAsync();
        }

        var ahora = DateTime.UtcNow;
        await using (var contexto = ContextoRuntime(_operador.Id, gestora))
        {
            (await TenantsBeneficiariosAutorizados.OperacionInboundQueAutorizaAsync(
                    contexto, gestora, _operador.Id, propietario, "GestorCae", ahora, default))
                .Should().Be(operacionId, "la cartera vigente de la Gestora CAE bajo la operación Inbound amparada");
        }

        var otraPersona = Guid.NewGuid();
        await using (var contexto = ContextoRuntime(_operador.Id, otraPersona))
        {
            (await TenantsBeneficiariosAutorizados.OperacionInboundQueAutorizaAsync(
                    contexto, otraPersona, _operador.Id, propietario, "GestorCae", ahora, default))
                .Should().BeNull("sin cartera, un Gestor CAE no abre la operación");
        }
    }

    [Fact]
    public async Task Es_SECURITY_DEFINER_con_search_path_fijo_solo_devuelve_un_booleano_y_no_concede_la_tabla()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand($"""
            SELECT p.prosecdef, p.prorettype::regtype::text, p.proretset, p.proconfig, p.provolatile::text,
                   p.proowner = (SELECT c.relowner FROM pg_class c WHERE c.oid = 'public."ProductosContratados"'::regclass),
                   COALESCE((SELECT bool_or(a.grantee = 0) FROM aclexplode(p.proacl) a), true),
                   has_function_privilege('cae_app_runtime', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_soporte', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_aprovisionamiento', p.oid, 'EXECUTE'),
                   p.pronargs
            FROM pg_proc p WHERE p.oid = '{Funcion}'::regprocedure;
            """, conexion);

        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();
        lector.GetBoolean(0).Should().BeTrue("sin SECURITY DEFINER no leería la línea del Tenant TCE");
        lector.GetString(1).Should().Be("boolean");
        lector.GetBoolean(2).Should().BeFalse("no devuelve filas");
        lector.GetFieldValue<string[]>(3).Should().Equal(["search_path=pg_catalog, pg_temp"]);
        lector.GetString(4).Should().Be("v", "compara el inicio de la línea con clock_timestamp(), como el gate DN-7");
        lector.GetBoolean(5).Should().BeTrue("la función lee con el propietario de las tablas");
        lector.GetBoolean(6).Should().BeFalse("PUBLIC no la ejecuta");
        lector.GetBoolean(7).Should().BeTrue();
        lector.GetBoolean(8).Should().BeTrue();
        lector.GetBoolean(9).Should().BeFalse("Aprovisionamiento no elige ni revalida contextos");
        lector.GetInt16(10).Should().Be(1, "solo recibe el Id de la operación");
    }

    private static string Llamada(Guid operacion) => $"public.app_operacion_inbound_amparada('{operacion}')";

    private async Task<bool> ComoRuntimeAsync(Guid tenant, string sql)
    {
        await using var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion));
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();
        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: tenant, local: true, transaccion: transaccion);

        await using (var identidad = new NpgsqlCommand("SELECT session_user::text || '/' || current_user::text;", conexion, transaccion))
            ((string)(await identidad.ExecuteScalarAsync())!).Should().Be("cae_app_runtime/cae_app_runtime");

        await using var comando = new NpgsqlCommand(sql, conexion, transaccion);
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    /// <summary>Contexto de runtime con el contexto firmado de una persona de <paramref name="tenant"/>, sin selección.</summary>
    private CaeManagerDbContext ContextoRuntime(Guid tenant, Guid usuario)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion),
                npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(tenantActual, new SinTenantSeleccionado(),
                    new CurrentUserServiceFalso(usuario, "Administrador", tenantOrigenId: tenant),
                    BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class SinTenantSeleccionado : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private CaeManagerDbContext ContextoPropietarioDeLaBase(Guid? tenant)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
