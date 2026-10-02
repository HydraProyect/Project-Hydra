using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// D-7 (2026-10-02): el reparto de la Asignación de Cartera por Cliente empresarial está retirado, y la
/// migración <c>ConvierteCarterasPorClienteATenantEntero</c> convierte una sola vez los datos que ya
/// existen en staging y producción: la cartera de un Gestor CAE es siempre el Tenant entero.
///
/// <para>
/// Lo que se prueba es el contrato de la conversión sobre una base que <b>ya tiene</b> carteras por
/// Cliente empresarial —el estado previo se construye con las entidades de dominio, que todavía las
/// admiten, y se migra hacia delante—: quién gana la universal, quién no, qué se cierra y con qué
/// motivo, que la conversión nunca alarga una caducidad ni concede un rol de Propiedad ni convierte
/// bajo una operación acotada, que no toca lo que no es por Cliente empresarial, y que es idempotente.
/// El criterio «nadie pierde ni gana alcance indebidamente» se comprueba aquí contra las filas y,
/// sobre el servicio de alcance, en <c>AlcanceTrasRetirarElRepartoPorClienteTests</c>.
/// </para>
/// </summary>
public class ConvierteCarterasPorClienteATenantEnteroTests : IAsyncLifetime
{
    private const string MigracionDelCambio = "20261002185604_ConvierteCarterasPorClienteATenantEntero";

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    private readonly Tenant _interno = new("Tenant interno de la conversión");
    private readonly Tenant _propietario = new("Tenant propietario de la conversión");
    private readonly Tenant _operador = new("Operador CAE externo de la conversión");
    private readonly Tenant _ajeno = new("Tenant ajeno de la conversión");

    // Tenant interno (operación raíz).
    private readonly Guid _dosClientes = Guid.NewGuid();
    private readonly Guid _yaTieneUniversal = Guid.NewGuid();
    private readonly Guid _conCaducidad = Guid.NewGuid();
    private readonly Guid _indefinidaYConCaducidad = Guid.NewGuid();
    private readonly Guid _caducadaSinCerrar = Guid.NewGuid();
    private readonly Guid _soloHistorico = Guid.NewGuid();
    private readonly Guid _programada = Guid.NewGuid();

    // Tenant propietario con Operador CAE externo.
    private readonly Guid _gestorExterno = Guid.NewGuid();
    private readonly Guid _coordinadorExterno = Guid.NewGuid();
    private readonly Guid _propiedadExterna = Guid.NewGuid();
    private readonly Guid _bajoOperacionAcotada = Guid.NewGuid();

    // Tenant ajeno: ya tiene su universal y ninguna por Cliente empresarial.
    private readonly Guid _gestorAjeno = Guid.NewGuid();

    private DateTime _caducidadFutura;

    /// <summary>La migración inmediatamente anterior a la del cambio: así la constante no caduca cuando entre otra posterior.</summary>
    private string MigracionAnterior
    {
        get
        {
            using var contexto = NuevoContexto(null);
            var migraciones = contexto.Database.GetMigrations().ToList();
            var indice = migraciones.IndexOf(MigracionDelCambio);
            indice.Should().BeGreaterThan(0, "la migración del cambio existe en el ensamblado y no es la línea base");
            return migraciones[indice - 1];
        }
    }

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadena);

        // Se deshace hasta la migración anterior a la del cambio: la base queda como estaba en staging y
        // producción antes de desplegarlo.
        await using (var contexto = NuevoContexto(null))
            await contexto.GetService<IMigrator>().MigrateAsync(MigracionAnterior);

        await using (var contexto = NuevoContexto(null))
        {
            contexto.Tenants.AddRange(_interno, _propietario, _operador, _ajeno);
            await contexto.SaveChangesAsync();
        }

        var ahora = DateTime.UtcNow;
        _caducidadFutura = ahora.AddDays(10);

        await SembrarInternoAsync(ahora);
        await SembrarExternoAsync(ahora);
        await SembrarAjenoAsync(ahora);
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    // ── El estado previo, para que ninguna aserción de abajo sea verde por vacío ──

    [Fact]
    public async Task Control_positivo_la_base_previa_tiene_carteras_por_Cliente_empresarial_vigentes()
    {
        await using var contexto = NuevoContexto(null);

        (await contexto.AsignacionesCartera.CountAsync(c => c.AmbitoRelacionClienteId != null && c.Estado == EstadoAsignacion.Vigente))
            .Should().BeGreaterThan(8);
        (await contexto.AsignacionesCartera.CountAsync(c => c.UsuarioId == _dosClientes && c.AmbitoRelacionClienteId != null))
            .Should().Be(2);
    }

    [Fact]
    public async Task El_instrumento_ve_todos_los_Tenants_porque_la_tabla_no_tiene_RLS_forzada()
    {
        // La migración la ejecuta el propietario de la tabla. Si AsignacionesCartera tuviera FORCE ROW LEVEL
        // SECURITY, ese propietario pasaría por las políticas y el SELECT/UPDATE de la conversión vería 0
        // filas sin ruido (la trampa de F1 del plan de migración). Este control fija el supuesto.
        await using var contexto = NuevoContexto(null);
        var conexion = contexto.Database.GetDbConnection();
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE relname = 'AsignacionesCartera'";
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();
        lector.GetBoolean(0).Should().BeTrue("la RLS está activada");
        lector.GetBoolean(1).Should().BeFalse("pero no forzada: el propietario no pasa por la política");
    }

    // ── La conversión ──────────────────────────────────────────────────────

    [Fact]
    public async Task Quien_tenia_carteras_por_Cliente_empresarial_pasa_a_tener_una_sola_universal_y_las_otras_se_cierran()
    {
        await MigrarAsync();

        await using var contexto = NuevoContexto(null);
        var del = await contexto.AsignacionesCartera.Where(c => c.UsuarioId == _dosClientes).ToListAsync();

        var universales = del.Where(c => c.Ambito.EsUniversal).ToList();
        universales.Should().ContainSingle();
        universales[0].Estado.Should().Be(EstadoAsignacion.Vigente);
        universales[0].Rol.Should().BeNull("una cartera interna no lleva rol propio");
        universales[0].VigenciaHasta.Should().BeNull();
        universales[0].PropietarioTenantId.Should().Be(_interno.Id);
        universales[0].OperadorTenantId.Should().Be(_interno.Id);

        var porCliente = del.Where(c => c.AmbitoRelacionClienteId != null).ToList();
        porCliente.Should().HaveCount(2, "el histórico se conserva: se cierra, no se borra");
        porCliente.Should().OnlyContain(c => c.Estado == EstadoAsignacion.Cerrada && c.MotivoCierre == MotivoCierreAsignacion.Reorganizada);
    }

    [Fact]
    public async Task Si_ya_tenia_una_universal_vigente_no_se_abre_otra_y_se_cierra_la_de_Cliente_empresarial()
    {
        await MigrarAsync();

        await using var contexto = NuevoContexto(null);
        var del = await contexto.AsignacionesCartera.Where(c => c.UsuarioId == _yaTieneUniversal).ToListAsync();

        del.Count(c => c.Ambito.EsUniversal && c.Estado == EstadoAsignacion.Vigente).Should().Be(1);
        del.Should().Contain(c => c.AmbitoRelacionClienteId != null && c.Estado == EstadoAsignacion.Cerrada);
    }

    [Fact]
    public async Task La_conversion_nunca_alarga_una_caducidad()
    {
        await MigrarAsync();

        await using var contexto = NuevoContexto(null);

        var conCaducidad = await contexto.AsignacionesCartera
            .SingleAsync(c => c.UsuarioId == _conCaducidad && c.AmbitoRelacionClienteId == null && c.Estado == EstadoAsignacion.Vigente);
        conCaducidad.VigenciaHasta.Should().BeCloseTo(_caducidadFutura, TimeSpan.FromSeconds(1));

        var indefinida = await contexto.AsignacionesCartera
            .SingleAsync(c => c.UsuarioId == _indefinidaYConCaducidad && c.AmbitoRelacionClienteId == null && c.Estado == EstadoAsignacion.Vigente);
        indefinida.VigenciaHasta.Should().BeNull("si alguna del grupo era indefinida, la universal también");
    }

    [Fact]
    public async Task Una_cartera_caducada_programada_o_solo_historica_no_se_convierte()
    {
        await MigrarAsync();

        await using var contexto = NuevoContexto(null);
        foreach (var usuario in new[] { _caducadaSinCerrar, _programada, _soloHistorico })
        {
            var del = await contexto.AsignacionesCartera.Where(c => c.UsuarioId == usuario).ToListAsync();
            del.Should().NotBeEmpty("control: el usuario sí tenía carteras");
            del.Should().NotContain(c => c.Ambito.EsUniversal, "lo que no concedía alcance hoy no pasa a concederlo");
            del.Should().OnlyContain(c => c.Estado == EstadoAsignacion.Cerrada);
        }
    }

    [Fact]
    public async Task Bajo_un_Operador_CAE_externo_la_universal_conserva_el_rol_y_cuelga_de_la_misma_operacion()
    {
        await MigrarAsync();

        await using var contexto = NuevoContexto(null);

        foreach (var (usuario, rol) in new[] { (_gestorExterno, Roles.GestorCae), (_coordinadorExterno, Roles.CoordinadorCae) })
        {
            var anterior = await contexto.AsignacionesCartera.SingleAsync(c => c.UsuarioId == usuario && c.AmbitoRelacionClienteId != null);
            var universal = await contexto.AsignacionesCartera.SingleAsync(c => c.UsuarioId == usuario && c.AmbitoRelacionClienteId == null && c.AmbitoCentroId == null && c.AmbitoTrabajadorId == null && c.AmbitoProyectoId == null);

            universal.Rol.Should().Be(rol);
            universal.Estado.Should().Be(EstadoAsignacion.Vigente);
            universal.AsignacionOperacionId.Should().Be(anterior.AsignacionOperacionId);
            universal.PropietarioTenantId.Should().Be(_propietario.Id);
            universal.OperadorTenantId.Should().Be(_operador.Id);
            anterior.Estado.Should().Be(EstadoAsignacion.Cerrada);
        }
    }

    [Fact]
    public async Task Un_rol_de_Propiedad_no_se_convierte_nunca_y_su_cartera_se_cierra()
    {
        await MigrarAsync();

        await using var contexto = NuevoContexto(null);
        var del = await contexto.AsignacionesCartera.Where(c => c.UsuarioId == _propiedadExterna).ToListAsync();

        del.Should().ContainSingle("solo la fila original");
        del[0].Estado.Should().Be(EstadoAsignacion.Cerrada);
        del[0].MotivoCierre.Should().Be(MotivoCierreAsignacion.Reorganizada);
        (await contexto.AsignacionesCartera.AnyAsync(c => c.Rol == Roles.Administrador && c.AmbitoRelacionClienteId == null && c.Estado == EstadoAsignacion.Vigente))
            .Should().BeFalse("una cartera no concede Administrador (decisión del 2026-09-23)");
    }

    [Fact]
    public async Task Bajo_una_operacion_acotada_a_un_Cliente_empresarial_se_cierra_sin_convertir()
    {
        // Una universal bajo una operación acotada a X daría solo X, pero la cartera por Cliente Y bajo esa
        // operación no daba nada: convertirla concedería lo que no concedía.
        await MigrarAsync();

        await using var contexto = NuevoContexto(null);
        var del = await contexto.AsignacionesCartera.Where(c => c.UsuarioId == _bajoOperacionAcotada).ToListAsync();

        del.Should().ContainSingle();
        del[0].Estado.Should().Be(EstadoAsignacion.Cerrada);
    }

    [Fact]
    public async Task No_cambia_nada_para_quien_no_tenia_cartera_por_Cliente_empresarial_ni_en_otros_Tenants()
    {
        var antes = await InstantaneaAsync(_gestorAjeno);

        await MigrarAsync();

        (await InstantaneaAsync(_gestorAjeno)).Should().BeEquivalentTo(antes, "la fila de otro Tenant ni se toca ni cambia de versión");

        await using var verificacion = NuevoContexto(null);
        (await verificacion.AsignacionesCartera.CountAsync(c => c.PropietarioTenantId == _ajeno.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Tras_convertir_ninguna_cartera_por_Cliente_empresarial_queda_viva()
    {
        await MigrarAsync();

        await using var contexto = NuevoContexto(null);
        (await contexto.AsignacionesCartera.CountAsync(c => c.AmbitoRelacionClienteId != null && c.Estado != EstadoAsignacion.Cerrada))
            .Should().Be(0);
    }

    [Fact]
    public async Task La_conversion_es_idempotente()
    {
        await MigrarAsync();
        var primera = await TodasAsync();

        // Down no hace nada: deshacer y volver a aplicar ejecuta otra vez el SQL sobre los datos ya convertidos.
        await using (var contexto = NuevoContexto(null))
        {
            var migrador = contexto.GetService<IMigrator>();
            await migrador.MigrateAsync(MigracionAnterior);
            await migrador.MigrateAsync();
        }

        var segunda = await TodasAsync();
        segunda.Should().BeEquivalentTo(primera, "una segunda ejecución no encuentra nada que convertir");
    }

    [Fact]
    public async Task Down_no_repone_el_reparto_por_Cliente_empresarial()
    {
        await MigrarAsync();
        await using (var contexto = NuevoContexto(null))
            await contexto.GetService<IMigrator>().MigrateAsync(MigracionAnterior);

        await using var verificacion = NuevoContexto(null);
        (await verificacion.AsignacionesCartera.CountAsync(c => c.AmbitoRelacionClienteId != null && c.Estado != EstadoAsignacion.Cerrada))
            .Should().Be(0, "el modo retirado no se repone: las universales abiertas y las cerradas quedan como están");
    }

    // ── Estado previo ──────────────────────────────────────────────────────

    private async Task SembrarInternoAsync(DateTime ahora)
    {
        await using var contexto = NuevoContexto(_interno.Id);
        var raiz = AsignacionOperacion.Raiz(_interno.Id, ServicioCae.Outbound, ahora.AddDays(-60), ahora.AddDays(-60));
        contexto.AsignacionesOperacion.Add(raiz);

        // IX_AsignacionesCartera_ResponsableRelacionVigente: un solo responsable vigente por Cliente
        // empresarial, así que cada cartera por Cliente empresarial lleva el suyo.
        var clientes = Enumerable.Range(0, 10).Select(i => Cliente(contexto, $"Cliente interno {i}")).ToList();
        await contexto.SaveChangesAsync();

        AsignacionCartera Interna(Guid usuario, int cliente, DateTime desde, DateTime? hasta = null) =>
            AsignacionCartera.Interna(raiz, usuario, AmbitoAsignacion.DeRelacionCliente(clientes[cliente].Id), desde, hasta, ahora);

        contexto.AsignacionesCartera.AddRange(
            Interna(_dosClientes, 0, ahora.AddDays(-30)),
            Interna(_dosClientes, 1, ahora.AddDays(-20)),
            Interna(_yaTieneUniversal, 2, ahora.AddDays(-30)),
            AsignacionCartera.Interna(raiz, _yaTieneUniversal, AmbitoAsignacion.Universal, ahora.AddDays(-5), null, ahora),
            Interna(_conCaducidad, 3, ahora.AddDays(-30), _caducidadFutura),
            Interna(_indefinidaYConCaducidad, 4, ahora.AddDays(-30), ahora.AddDays(5)),
            Interna(_indefinidaYConCaducidad, 5, ahora.AddDays(-30)),
            Interna(_caducadaSinCerrar, 6, ahora.AddDays(-30), ahora.AddDays(-1)),
            Interna(_programada, 7, ahora.AddDays(5), ahora.AddDays(50)));

        var historica = Interna(_soloHistorico, 8, ahora.AddDays(-40));
        historica.Cerrar(MotivoCierreAsignacion.Revocada, ahora.AddDays(-10));
        contexto.AsignacionesCartera.Add(historica);

        await contexto.SaveChangesAsync();
    }

    private async Task SembrarExternoAsync(DateTime ahora)
    {
        await using var contexto = NuevoContexto(_propietario.Id);
        var externa = AsignacionOperacion.Externa(
            _propietario.Id, _operador.Id, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora.AddDays(-60), null, ahora.AddDays(-60));
        contexto.AsignacionesOperacion.Add(externa);

        var clientes = Enumerable.Range(0, 4).Select(i => Cliente(contexto, $"Cliente externo {i}")).ToList();
        await contexto.SaveChangesAsync();

        var acotada = AsignacionOperacion.Externa(
            _propietario.Id, _operador.Id, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(clientes[3].Id),
            ahora.AddDays(-60), null, ahora.AddDays(-60));
        contexto.AsignacionesOperacion.Add(acotada);
        await contexto.SaveChangesAsync();

        AsignacionCartera Externa(AsignacionOperacion operacion, Guid usuario, string rol, int cliente) =>
            AsignacionCartera.Externa(
                operacion, usuario, rol, AmbitoAsignacion.DeRelacionCliente(clientes[cliente].Id), ahora.AddDays(-30), null, ahora);

        contexto.AsignacionesCartera.AddRange(
            Externa(externa, _gestorExterno, Roles.GestorCae, 0),
            Externa(externa, _coordinadorExterno, Roles.CoordinadorCae, 1),
            Externa(externa, _propiedadExterna, Roles.Administrador, 2),
            Externa(acotada, _bajoOperacionAcotada, Roles.GestorCae, 3));

        await contexto.SaveChangesAsync();
    }

    private async Task SembrarAjenoAsync(DateTime ahora)
    {
        await using var contexto = NuevoContexto(_ajeno.Id);
        var raiz = AsignacionOperacion.Raiz(_ajeno.Id, ServicioCae.Outbound, ahora.AddDays(-60), ahora.AddDays(-60));
        contexto.AsignacionesOperacion.Add(raiz);
        await contexto.SaveChangesAsync();

        contexto.AsignacionesCartera.Add(
            AsignacionCartera.Interna(raiz, _gestorAjeno, AmbitoAsignacion.Universal, ahora.AddDays(-5), null, ahora));
        await contexto.SaveChangesAsync();
    }

    private static int _secuenciaCif;

    private static Empresa Cliente(CaeManagerDbContext contexto, string razonSocial)
    {
        var cliente = Empresa.CrearComoCliente(razonSocial, DatosPruebaSeeder.GenerarCifValido(8_000_000 + Interlocked.Increment(ref _secuenciaCif)), false, null, null);
        contexto.Empresas.Add(cliente);
        return cliente;
    }

    // ── Arnés ──────────────────────────────────────────────────────────────

    private async Task MigrarAsync()
    {
        await using var contexto = NuevoContexto(null);
        await contexto.GetService<IMigrator>().MigrateAsync();
    }

    private async Task<List<(Guid Id, string Estado, string? Motivo, Guid Version, DateTime? Hasta)>> InstantaneaAsync(Guid usuario)
    {
        await using var contexto = NuevoContexto(null);
        var filas = await contexto.AsignacionesCartera.Where(c => c.UsuarioId == usuario).ToListAsync();
        return filas.Select(c => (c.Id, c.Estado.ToString(), c.MotivoCierre?.ToString(), c.Version, c.VigenciaHasta)).ToList();
    }

    private async Task<List<(Guid Usuario, string Estado, bool Universal, string? Motivo)>> TodasAsync()
    {
        await using var contexto = NuevoContexto(null);
        var filas = await contexto.AsignacionesCartera.ToListAsync();
        return filas
            .Select(c => (c.UsuarioId, c.Estado.ToString(), c.Ambito.EsUniversal, c.MotivoCierre?.ToString()))
            .OrderBy(x => x.UsuarioId).ThenBy(x => x.Item2).ThenBy(x => x.Item3).ThenBy(x => x.Item4)
            .ToList();
    }

    private CaeManagerDbContext NuevoContexto(Guid? tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual), new ConcurrenciaOptimistaInterceptor())
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
