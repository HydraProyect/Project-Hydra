using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Configuracion.Commands.GuardarVistaRecordada;
using CaeManager.Application.Configuracion.Commands.OlvidarVistaRecordada;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.KitUsuarioExperto;

/// <summary>
/// La vista recordada de un listado es del Usuario EN un Tenant (decisión del
/// 2026-10-08): un Gestor CAE con Asignación de Cartera sobre dos Tenants
/// recuerda en cada uno su vista, y otro Usuario del mismo Tenant no la ve.
/// Vive en la fila reservada de <see cref="FiltroGuardado"/>, así que hereda su
/// aislamiento; estos tests lo comprueban para ESA fila y para sus casos de uso.
///
/// <para>
/// Como en <see cref="FiltrosGuardadosPorTenantBajoRlsTests"/>, lectura y
/// escritura van autenticadas como <c>cae_app_runtime</c> con el interceptor de
/// sesión RLS: bajo el rol propietario PostgreSQL no aplica la política. Las dos
/// barreras se prueban por separado y no se prestan evidencia: la política RLS
/// con el filtro de EF desactivado, y el filtro de EF bajo el rol propietario,
/// donde RLS no actúa.
/// </para>
///
/// <para>
/// Cada contexto de runtime hace de una pestaña (un circuito): el contexto vive
/// lo que el circuito y no se vacía entre guardados, que es justo la condición en
/// la que una fila rastreada se queda vieja.
/// </para>
/// </summary>
public class VistaRecordadaPorTenantBajoRlsTests : IAsyncLifetime
{
    private const string Pantalla = PantallasConVistaRecordada.Clientes;

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _gestorCae = Guid.NewGuid();
    private readonly Guid _otroUsuario = Guid.NewGuid();
    private readonly List<CaeManagerDbContext> _contextos = [];
    private TenantActualDeLaPeticion _tenantDeLaPeticion = null!;
    private TenantActualPorAmbito _tenantPorAmbito = null!;
    private CaeManagerDbContext _propietario = null!;
    private CaeManagerDbContext _pestana = null!;
    private Guid _tenantOrigen;
    private Guid _tenantA;
    private Guid _tenantB;

    public async Task InitializeAsync()
    {
        _tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), _tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var origen = new Tenant("Operador CAE externo de prueba");
        var a = new Tenant("Tenant propietario A");
        var b = new Tenant("Tenant propietario B");
        _propietario.Tenants.AddRange(origen, a, b);
        await _propietario.SaveChangesAsync();
        _tenantOrigen = origen.Id;
        _tenantA = a.Id;
        _tenantB = b.Id;

        _tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantOrigen);
        _pestana = CrearPestana(_gestorCae);
    }

    public async Task DisposeAsync()
    {
        foreach (var contexto in _contextos)
            await contexto.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Fact]
    public async Task Un_Gestor_CAE_con_dos_Tenants_recuerda_en_cada_uno_su_vista()
    {
        (await GuardarAsync(_pestana, _gestorCae, _tenantA, "{\"de\":\"A\"}")).EsExitoso.Should().BeTrue();
        (await GuardarAsync(_pestana, _gestorCae, _tenantB, "{\"de\":\"B\"}")).EsExitoso.Should().BeTrue();

        (await ObtenerAsync(_pestana, _gestorCae, _tenantA)).Should().Be("{\"de\":\"A\"}");
        (await ObtenerAsync(_pestana, _gestorCae, _tenantB)).Should().Be("{\"de\":\"B\"}");
        (await ObtenerAsync(_pestana, _gestorCae, _tenantOrigen)).Should().BeNull("en su Tenant de origen no ha guardado ninguna");

        var filas = await FilasDeVistaAsync();
        filas.Select(f => (f.TenantId, f.ValoresJson)).Should().BeEquivalentTo(
            new[] { (_tenantA, "{\"de\":\"A\"}"), (_tenantB, "{\"de\":\"B\"}") },
            "el interceptor sella cada fila con el Tenant en el que se guardó, no con el de origen del usuario");
    }

    [Fact]
    public async Task Otro_Usuario_del_mismo_Tenant_no_recupera_ni_pisa_la_vista_del_primero()
    {
        await GuardarAsync(_pestana, _gestorCae, _tenantA, "{\"de\":\"gestor\"}");
        var pestanaDelOtro = CrearPestana(_otroUsuario);

        (await ObtenerAsync(pestanaDelOtro, _otroUsuario, _tenantA)).Should().BeNull("la vista es del Usuario, no del Tenant");

        (await GuardarAsync(pestanaDelOtro, _otroUsuario, _tenantA, "{\"de\":\"otro\"}")).EsExitoso.Should().BeTrue();

        (await ObtenerAsync(_pestana, _gestorCae, _tenantA)).Should().Be("{\"de\":\"gestor\"}");
        (await ObtenerAsync(pestanaDelOtro, _otroUsuario, _tenantA)).Should().Be("{\"de\":\"otro\"}");
        (await FilasDeVistaAsync()).Should().HaveCount(2);
    }

    /// <summary>
    /// Control del instrumento y de la política a la vez: con el filtro global de
    /// EF desactivado, lo único que puede ocultar la fila reservada es RLS.
    /// </summary>
    [Fact]
    public async Task La_politica_RLS_oculta_en_un_Tenant_la_vista_recordada_en_otro_aunque_se_ignore_el_filtro_de_EF()
    {
        await GuardarAsync(_pestana, _gestorCae, _tenantB, "{\"de\":\"B\"}");
        _pestana.ChangeTracker.Clear();

        using (AmbitoTenantExplicito.Establecer(_tenantA))
            (await _pestana.FiltrosGuardados.IgnoreQueryFilters().CountAsync(f => f.Nombre == FiltroGuardado.NombreVistaRecordada)).Should().Be(0,
                "app.tenant_id es el Tenant A y la política aislamiento_tenant oculta la fila del Tenant B");

        using (AmbitoTenantExplicito.Establecer(_tenantB))
            (await _pestana.FiltrosGuardados.IgnoreQueryFilters().CountAsync(f => f.Nombre == FiltroGuardado.NombreVistaRecordada)).Should().Be(1,
                "control positivo: la fila existe y desde su Tenant se ve");
    }

    /// <summary>
    /// La otra barrera, sola: bajo el rol propietario RLS no actúa, así que lo
    /// único que separa los Tenants es el filtro global de EF.
    /// </summary>
    [Fact]
    public async Task El_filtro_global_de_EF_tampoco_devuelve_la_vista_de_otro_Tenant_donde_RLS_no_actua()
    {
        await GuardarAsync(_pestana, _gestorCae, _tenantB, "{\"de\":\"B\"}");

        using (AmbitoTenantExplicito.Establecer(_tenantA))
        {
            (await _propietario.FiltrosGuardados.IgnoreQueryFilters().CountAsync(f => f.Nombre == FiltroGuardado.NombreVistaRecordada))
                .Should().Be(1, "control del instrumento: bajo el rol propietario la fila del Tenant B SÍ se ve sin el filtro de EF");

            (await new ObtenerVistaRecordadaQueryHandler(_propietario, new CurrentUserServiceFalso(_gestorCae), _tenantPorAmbito)
                    .Handle(new ObtenerVistaRecordadaQuery(Pantalla), CancellationToken.None))
                .Should().BeNull("el filtro global acota al Tenant A");
            (await new FiltroGuardadoRepository(_propietario).ObtenerVistaRecordadaAsync(_gestorCae, Pantalla))
                .Should().BeNull("la lectura del repositorio, la que precede a toda escritura, también va acotada");
        }
    }

    [Fact]
    public async Task Guardar_varias_veces_deja_una_sola_fila_con_la_ultima_vista()
    {
        await GuardarAsync(_pestana, _gestorCae, _tenantA, "{\"n\":1}");
        await GuardarAsync(_pestana, _gestorCae, _tenantA, "{\"n\":2}");
        _pestana.ChangeTracker.Clear();
        (await GuardarAsync(_pestana, _gestorCae, _tenantA, "{\"n\":3}")).EsExitoso.Should().BeTrue(
            "con el contexto vacío la fila reservada se materializa de la base y se sustituye");

        (await FilasDeVistaAsync()).Should().ContainSingle().Which.ValoresJson.Should().Be("{\"n\":3}");
    }

    /// <summary>
    /// La carrera de verdad contra el índice único: esta pestaña no vio fila y va a
    /// insertar, pero otra pestaña del mismo Usuario inserta antes. El 23505 no
    /// sube: el handler reintenta como sustitución y gana la última.
    /// </summary>
    [Fact]
    public async Task Dos_pestanas_que_insertan_a_la_vez_no_duplican_ni_lanzan_y_gana_la_ultima()
    {
        var otraPestana = CrearPestana(_gestorCae);
        var guardado = new GuardadoConOtraPestanaPorDelante(_pestana,
            antesDelPrimerGuardado: async () =>
                (await GuardarAsync(otraPestana, _gestorCae, _tenantA, "{\"de\":\"otra pestaña\"}")).EsExitoso.Should().BeTrue());

        var resultado = await GuardarAsync(_pestana, _gestorCae, _tenantA, "{\"de\":\"esta pestaña\"}", guardado);

        guardado.Fallos.Should().ContainSingle("control del instrumento: el primer guardado tiene que haber chocado de verdad")
            .Which.Should().BeOfType<DbUpdateException>()
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        resultado.EsExitoso.Should().BeTrue();
        (await FilasDeVistaAsync()).Should().ContainSingle().Which.ValoresJson.Should().Be("{\"de\":\"esta pestaña\"}");
    }

    /// <summary>
    /// El contexto de una pestaña sigue rastreando la fila que guardó. Si otra
    /// pestaña la cambia y esta vuelve a poner el valor que ya conocía, comparar
    /// contra la foto vieja no emitiría ningún UPDATE y se quedaría el de la otra.
    /// </summary>
    [Fact]
    public async Task Volver_a_un_valor_que_esta_pestana_ya_conocia_tambien_sustituye_el_de_la_otra()
    {
        var otraPestana = CrearPestana(_gestorCae);
        await GuardarAsync(_pestana, _gestorCae, _tenantA, "{\"de\":\"esta pestaña\"}");
        await GuardarAsync(otraPestana, _gestorCae, _tenantA, "{\"de\":\"otra pestaña\"}");

        (await GuardarAsync(_pestana, _gestorCae, _tenantA, "{\"de\":\"esta pestaña\"}")).EsExitoso.Should().BeTrue();

        (await FilasDeVistaAsync()).Should().ContainSingle().Which.ValoresJson.Should().Be("{\"de\":\"esta pestaña\"}", "gana la última escritura");
    }

    [Fact]
    public async Task Olvidar_borra_la_vista_de_ese_Tenant_y_es_idempotente()
    {
        await GuardarAsync(_pestana, _gestorCae, _tenantA, "{\"de\":\"A\"}");
        await GuardarAsync(_pestana, _gestorCae, _tenantB, "{\"de\":\"B\"}");

        (await OlvidarAsync(_pestana, _gestorCae, _tenantA)).EsExitoso.Should().BeTrue();
        (await OlvidarAsync(_pestana, _gestorCae, _tenantA)).EsExitoso.Should().BeTrue("sin vista, lo pedido ya se cumple");

        (await ObtenerAsync(_pestana, _gestorCae, _tenantA)).Should().BeNull();
        (await FilasDeVistaAsync()).Should().ContainSingle().Which.TenantId.Should().Be(_tenantB, "la del otro Tenant no se toca");
    }

    [Fact]
    public async Task Olvidar_una_vista_que_otra_pestana_ya_borro_es_un_exito_y_guardar_despues_la_crea_de_nuevo()
    {
        var otraPestana = CrearPestana(_gestorCae);
        await GuardarAsync(_pestana, _gestorCae, _tenantA, "{\"n\":1}");
        (await OlvidarAsync(otraPestana, _gestorCae, _tenantA)).EsExitoso.Should().BeTrue();

        (await OlvidarAsync(_pestana, _gestorCae, _tenantA)).EsExitoso.Should().BeTrue("esta pestaña aún rastrea la fila que la otra borró");
        (await GuardarAsync(_pestana, _gestorCae, _tenantA, "{\"n\":2}")).EsExitoso.Should().BeTrue();

        (await FilasDeVistaAsync()).Should().ContainSingle().Which.ValoresJson.Should().Be("{\"n\":2}");
    }

    /// <summary>
    /// Los casos de uso de los filtros con nombre no alcanzan la fila reservada,
    /// tampoco contra la base real (la exclusión del listado se traduce a SQL).
    /// </summary>
    [Fact]
    public async Task El_listado_de_filtros_y_su_borrado_por_Id_no_alcanzan_la_vista_recordada()
    {
        await GuardarAsync(_pestana, _gestorCae, _tenantA, "{\"de\":\"vista\"}");
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantA);
        var usuario = new CurrentUserServiceFalso(_gestorCae, tenantOrigenId: _tenantOrigen);
        (await new GuardarFiltroCommandHandler(usuario, _tenantDeLaPeticion, new FiltroGuardadoRepository(_pestana), _pestana)
            .Handle(new GuardarFiltroCommand(Pantalla, "Vencidos", "{\"de\":\"filtro\"}"), CancellationToken.None)).EsExitoso.Should().BeTrue();
        var idDeLaVista = (await _propietario.FiltrosGuardados.IgnoreQueryFilters().SingleAsync(f => f.Nombre == FiltroGuardado.NombreVistaRecordada)).Id;

        var listado = await new ObtenerFiltrosGuardadosQueryHandler(_pestana, usuario)
            .Handle(new ObtenerFiltrosGuardadosQuery(Pantalla), CancellationToken.None);
        var borrado = await new EliminarFiltroGuardadoCommandHandler(usuario, new FiltroGuardadoRepository(_pestana), _pestana)
            .Handle(new EliminarFiltroGuardadoCommand(idDeLaVista), CancellationToken.None);

        listado.Select(f => f.Nombre).Should().Equal("Vencidos");
        borrado.EsFallido.Should().BeTrue();
        borrado.Error.Codigo.Should().Be("FiltroGuardado.NoEncontrado");
        (await FilasDeVistaAsync()).Should().ContainSingle("sigue existiendo");
    }

    private async Task<Result> GuardarAsync(
        CaeManagerDbContext pestana, Guid usuarioId, Guid tenant, string valoresJson, IUnitOfWork? guardado = null)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenant);
        return await new GuardarVistaRecordadaCommandHandler(
                new CurrentUserServiceFalso(usuarioId, tenantOrigenId: _tenantOrigen), _tenantDeLaPeticion,
                new FiltroGuardadoRepository(pestana), guardado ?? pestana, pestana,
                NullLogger<GuardarVistaRecordadaCommandHandler>.Instance)
            .Handle(new GuardarVistaRecordadaCommand(Pantalla, valoresJson), CancellationToken.None);
    }

    private async Task<Result> OlvidarAsync(CaeManagerDbContext pestana, Guid usuarioId, Guid tenant)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenant);
        return await new OlvidarVistaRecordadaCommandHandler(
                new CurrentUserServiceFalso(usuarioId, tenantOrigenId: _tenantOrigen), _tenantDeLaPeticion,
                new FiltroGuardadoRepository(pestana), pestana, pestana)
            .Handle(new OlvidarVistaRecordadaCommand(Pantalla), CancellationToken.None);
    }

    private async Task<string?> ObtenerAsync(CaeManagerDbContext pestana, Guid usuarioId, Guid tenant)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenant);
        return await new ObtenerVistaRecordadaQueryHandler(
                pestana, new CurrentUserServiceFalso(usuarioId, tenantOrigenId: _tenantOrigen), _tenantDeLaPeticion)
            .Handle(new ObtenerVistaRecordadaQuery(Pantalla), CancellationToken.None);
    }

    /// <summary>Las filas reservadas que hay de verdad en la base, de cualquier Tenant, leídas como propietario.</summary>
    private async Task<List<FiltroGuardado>> FilasDeVistaAsync() =>
        await _propietario.FiltrosGuardados.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.Nombre == FiltroGuardado.NombreVistaRecordada).ToListAsync();

    /// <summary>
    /// Un contexto autenticado como <c>cae_app_runtime</c> con la sesión RLS de
    /// ese Usuario: hace de una pestaña suya.
    /// </summary>
    private CaeManagerDbContext CrearPestana(Guid usuarioId)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(_tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(
                    _tenantDeLaPeticion, new SinClienteActivo(),
                    new CurrentUserServiceFalso(usuarioId, tenantOrigenId: _tenantOrigen),
                    BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        var contexto = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), _tenantDeLaPeticion);
        _contextos.Add(contexto);
        return contexto;
    }

    /// <summary>
    /// Guarda con el contexto de esta pestaña, pero justo antes del primer guardado
    /// deja que otra pestaña termine el suyo. Apunta los guardados que fallan.
    /// </summary>
    private sealed class GuardadoConOtraPestanaPorDelante(IUnitOfWork interno, Func<Task> antesDelPrimerGuardado) : IUnitOfWork
    {
        private bool _otraPestanaYaGuardo;

        public List<Exception> Fallos { get; } = [];

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (!_otraPestanaYaGuardo)
            {
                _otraPestanaYaGuardo = true;
                await antesDelPrimerGuardado();
            }

            try
            {
                return await interno.SaveChangesAsync(cancellationToken);
            }
            catch (Exception excepcion)
            {
                Fallos.Add(excepcion);
                throw;
            }
        }
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    /// <summary>Como el <c>TenantActual</c> real: primero el ámbito explícito, luego el Tenant de la sesión.</summary>
    private sealed class TenantActualDeLaPeticion(Guid tenantDeLaSesion) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantDeLaSesion;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
