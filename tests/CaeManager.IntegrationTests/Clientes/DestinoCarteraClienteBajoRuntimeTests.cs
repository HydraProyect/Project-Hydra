using CaeManager.Application.Clientes;
using CaeManager.Application.Clientes.Commands.ReasignarEjecutivoCliente;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Clientes;

/// <summary>
/// Destino de la cartera de un Cliente empresarial (revisión Codex de la PR #931)
/// contra PostgreSQL real, autenticando como <c>cae_app_runtime</c> —RLS siempre
/// aplica— con el <see cref="TenantRlsConnectionInterceptor"/> real. El handler,
/// el <see cref="DirectorioUsuariosTenant"/>, el <see cref="AsignacionesOperativasWriter"/>
/// y el <see cref="AlcanceDatosService"/> son los de producción.
///
/// <para>
/// Lo que solo esta capa prueba: que el rol efectivo se lee de Identity para una
/// cuenta propia y de la Asignación de Operador Delegado para la de un Operador CAE
/// externo, que una cuenta de otro Tenant sin delegación no es alcanzable, que la
/// desactivación se lee de la base y no del contexto, y que un rechazo no deja nada
/// escrito.
/// </para>
/// </summary>
public class DestinoCarteraClienteBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    private readonly Tenant _propietario = new("Tenant propietario de prueba");
    private readonly Tenant _operadorExterno = new("Operador CAE externo de prueba");
    private readonly Tenant _ajeno = new("Tenant ajeno de prueba");

    private readonly Guid _administrador = Guid.NewGuid();
    private readonly Guid _coordinador = Guid.NewGuid();
    private readonly Guid _otroCoordinador = Guid.NewGuid();
    private readonly Guid _gestorActual = Guid.NewGuid();
    private readonly Guid _gestorDelCoordinador = Guid.NewGuid();
    private readonly Guid _gestorDeOtroCoordinador = Guid.NewGuid();
    private readonly Guid _gestorDesactivado = Guid.NewGuid();
    private readonly Guid _consulta = Guid.NewGuid();
    private readonly Guid _gestorDelegado = Guid.NewGuid();
    private readonly Guid _otroGestorDelegado = Guid.NewGuid();
    private readonly Guid _coordinadorDelegado = Guid.NewGuid();
    private readonly Guid _consultaDelegada = Guid.NewGuid();
    private readonly Guid _gestorAjeno = Guid.NewGuid();
    private readonly Guid _gestorOperadorSinAsignacion = Guid.NewGuid();

    private Guid _clienteId;

    public async Task InitializeAsync()
    {
        await using var contexto = ContextoPropietario();
        await contexto.Database.MigrateAsync();

        var ahora = DateTime.UtcNow;
        contexto.Tenants.AddRange(_propietario, _operadorExterno, _ajeno);

        // Los roles los siembran las migraciones.
        var roles = await contexto.Roles.AsNoTracking().ToDictionaryAsync(r => r.Name!, r => r.Id);

        void Cuenta(Guid id, Guid tenantId, string rol, Guid? coordinador = null, bool desactivada = false)
        {
            var usuario = new ApplicationUser
            {
                Id = id,
                TenantId = tenantId,
                UserName = $"{id:N}@caemanager.local",
                NormalizedUserName = $"{id:N}@CAEMANAGER.LOCAL",
                Email = $"{id:N}@caemanager.local",
                NormalizedEmail = $"{id:N}@CAEMANAGER.LOCAL",
                NombreCompleto = rol,
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString(),
                ConcurrencyStamp = Guid.NewGuid().ToString(),
                CoordinadorUsuarioId = coordinador,
            };
            if (desactivada) usuario.Desactivar();
            contexto.Users.Add(usuario);
            contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = id, RoleId = roles[rol] });
        }

        Cuenta(_administrador, _propietario.Id, "Administrador");
        Cuenta(_coordinador, _propietario.Id, "CoordinadorCae");
        Cuenta(_otroCoordinador, _propietario.Id, "CoordinadorCae");
        Cuenta(_gestorActual, _propietario.Id, "GestorCae", _coordinador);
        Cuenta(_gestorDelCoordinador, _propietario.Id, "GestorCae", _coordinador);
        Cuenta(_gestorDeOtroCoordinador, _propietario.Id, "GestorCae", _otroCoordinador);
        Cuenta(_gestorDesactivado, _propietario.Id, "GestorCae", _coordinador, desactivada: true);
        Cuenta(_consulta, _propietario.Id, "Consulta");
        // Las cuentas del Operador CAE externo son GestorCae en SU organización: el rol
        // con el que operan aquí lo da la Asignación de Operador Delegado, no Identity.
        Cuenta(_gestorDelegado, _operadorExterno.Id, "GestorCae", _coordinadorDelegado);
        Cuenta(_otroGestorDelegado, _operadorExterno.Id, "GestorCae", _coordinadorDelegado);
        Cuenta(_coordinadorDelegado, _operadorExterno.Id, "GestorCae");
        Cuenta(_consultaDelegada, _operadorExterno.Id, "GestorCae");
        Cuenta(_gestorAjeno, _ajeno.Id, "GestorCae");
        Cuenta(_gestorOperadorSinAsignacion, _operadorExterno.Id, "GestorCae");

        var raiz = AsignacionOperacion.Raiz(_propietario.Id, ServicioCae.Outbound, ahora.AddDays(-1), ahora);
        contexto.AsignacionesOperacion.Add(raiz);
        contexto.AsignacionesOperacion.Add(AsignacionOperacion.Externa(
            _propietario.Id, _operadorExterno.Id, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            vigenciaDesde: ahora.AddDays(-1), vigenciaHasta: null, ahora));

        var delegacion = new DelegacionTenant(_operadorExterno.Id, _propietario.Id);
        contexto.DelegacionesTenant.Add(delegacion);
        contexto.AsignacionesOperadorDelegadoConRevocadas.AddRange(
            new AsignacionOperadorDelegado(delegacion.Id, _gestorDelegado, "GestorCae"),
            new AsignacionOperadorDelegado(delegacion.Id, _otroGestorDelegado, "GestorCae"),
            new AsignacionOperadorDelegado(delegacion.Id, _coordinadorDelegado, "CoordinadorCae"),
            new AsignacionOperadorDelegado(delegacion.Id, _consultaDelegada, "Consulta"));

        var cliente = Empresa.CrearComoCliente("Cadena Industrial Iberia", "B12345674", false, null, _gestorActual);
        contexto.Empresas.Add(cliente);
        contexto.AsignacionesCartera.Add(AsignacionCartera.Interna(
            raiz, _gestorActual, AmbitoAsignacion.DeRelacionCliente(cliente.Id), ahora.AddDays(-1), vigenciaHasta: null, ahora, null));

        await contexto.SaveChangesAsync();
        _clienteId = cliente.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── El puerto de lectura ──────────────────────────────────────────────

    [Fact]
    public async Task El_directorio_lee_el_rol_efectivo_de_Identity_o_de_la_delegacion_y_la_desactivacion_de_la_base()
    {
        await using var contexto = ContextoRuntime(_administrador, "Administrador");
        var directorio = Directorio(contexto);

        (await directorio.ObtenerAsync(_gestorDelCoordinador)).Should()
            .Be(new DestinoCartera(true, "GestorCae", _coordinador, EsOperadorDelegado: false));
        (await directorio.ObtenerAsync(_gestorDesactivado))!.Activa.Should().BeFalse();
        (await directorio.ObtenerAsync(_consulta))!.RolEfectivo.Should().Be("Consulta");
        (await directorio.ObtenerAsync(_gestorDelegado)).Should()
            .Be(new DestinoCartera(true, "GestorCae", _coordinadorDelegado, EsOperadorDelegado: true));
        (await directorio.ObtenerAsync(_coordinadorDelegado))!.RolEfectivo.Should().Be(
            "CoordinadorCae", "el rol de Identity en su organización es GestorCae; aquí opera como Coordinador CAE");
        (await directorio.ObtenerAsync(_gestorAjeno)).Should().BeNull("otro Tenant sin delegación vigente no es alcanzable");
        (await directorio.ObtenerAsync(_gestorOperadorSinAsignacion)).Should().BeNull(
            "la delegación del Operador CAE externo no alcanza a quien no tiene Asignación de Operador Delegado");
        (await directorio.ObtenerAsync(Guid.NewGuid())).Should().BeNull();
    }

    /// <summary>
    /// Defensa en profundidad: bajo <c>cae_app_runtime</c> la RLS de <c>AspNetUsers</c> ya
    /// esconde la cuenta de otro Tenant, así que el test anterior no puede observar el
    /// filtro de delegación del propio directorio (una mutación que lo quitaba seguía en
    /// verde). Sobre una conexión sin RLS, ese filtro es lo único que queda.
    /// </summary>
    [Fact]
    public async Task Sin_RLS_el_directorio_sigue_sin_alcanzar_una_cuenta_de_otro_Tenant_sin_delegacion()
    {
        await using var contexto = ContextoPropietario();
        var directorio = Directorio(contexto);

        (await contexto.Users.AsNoTracking().AnyAsync(u => u.Id == _gestorAjeno)).Should().BeTrue("control: sin RLS la fila se ve");
        (await directorio.ObtenerAsync(_gestorAjeno)).Should().BeNull();
        (await directorio.ObtenerAsync(_gestorOperadorSinAsignacion)).Should().BeNull();
        (await directorio.ObtenerAsync(_gestorDelegado))!.RolEfectivo.Should().Be("GestorCae", "control positivo");
    }

    [Fact]
    public async Task La_desactivacion_se_lee_de_la_base_aunque_el_contexto_tenga_la_cuenta_rastreada_de_antes()
    {
        await using var contexto = ContextoRuntime(_administrador, "Administrador");
        (await contexto.Users.SingleAsync(u => u.Id == _gestorDelCoordinador)).EstaDesactivada(DateTimeOffset.UtcNow)
            .Should().BeFalse("control: la cuenta se rastrea activa");

        await using (var otro = ContextoPropietario())
        {
            (await otro.Users.SingleAsync(u => u.Id == _gestorDelCoordinador)).Desactivar();
            await otro.SaveChangesAsync();
        }

        (await Directorio(contexto).ObtenerAsync(_gestorDelCoordinador))!.Activa.Should().BeFalse();
    }

    // ── El handler, de extremo a extremo ──────────────────────────────────

    public static TheoryData<string, string> DestinosRechazados => new()
    {
        { nameof(_gestorDesactivado), "Cliente.DestinoInactivo" },
        { nameof(_coordinador), "Cliente.DestinoNoEsGestorCae" },
        { nameof(_consulta), "Cliente.DestinoNoEsGestorCae" },
        { nameof(_administrador), "Cliente.DestinoNoEsGestorCae" },
        { nameof(_coordinadorDelegado), "Cliente.DestinoNoEsGestorCae" },
        { nameof(_consultaDelegada), "Cliente.DestinoNoEsGestorCae" },
        { nameof(_gestorAjeno), "Cliente.DestinoNoAlcanzable" },
        { nameof(_gestorOperadorSinAsignacion), "Cliente.DestinoNoAlcanzable" },
    };

    [Theory]
    [MemberData(nameof(DestinosRechazados))]
    public async Task Un_Administrador_no_puede_pasar_la_cartera_a_quien_no_es_un_Gestor_CAE_activo_alcanzable(
        string destino, string codigoEsperado)
    {
        var resultado = await ReasignarAsync(_administrador, "Administrador", Cuenta(destino));

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(codigoEsperado);
        await AfirmarSinCambiosAsync();
    }

    [Theory]
    [InlineData(nameof(_gestorDelCoordinador), false)]
    [InlineData(nameof(_gestorDelegado), true)]
    public async Task Un_Administrador_pasa_la_cartera_a_un_Gestor_CAE_propio_o_delegado(string destino, bool externa)
    {
        var destinoId = Cuenta(destino);

        var resultado = await ReasignarAsync(_administrador, "Administrador", destinoId);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);
        await using var contexto = ContextoPropietario();
        (await contexto.Empresas.AsNoTracking().SingleAsync(e => e.Id == _clienteId)).EjecutivoUsuarioId.Should().Be(destinoId);
        var vigentes = await contexto.AsignacionesCartera.AsNoTracking()
            .Where(c => c.AmbitoRelacionClienteId == _clienteId && c.Estado == EstadoAsignacion.Vigente)
            .ToListAsync();
        vigentes.Should().ContainSingle().Which.UsuarioId.Should().Be(destinoId);
        var operacion = await contexto.AsignacionesOperacion.AsNoTracking().SingleAsync(o => o.Id == vigentes[0].AsignacionOperacionId);
        operacion.EsRaiz.Should().Be(!externa);
    }

    [Fact]
    public async Task Un_Coordinador_CAE_solo_pasa_la_cartera_a_un_Gestor_CAE_a_su_cargo()
    {
        var fuera = await ReasignarAsync(_coordinador, "CoordinadorCae", _gestorDeOtroCoordinador);
        fuera.EsFallido.Should().BeTrue();
        fuera.Error.Codigo.Should().Be("Cliente.DestinoFueraDeAlcance");
        await AfirmarSinCambiosAsync();

        var dentro = await ReasignarAsync(_coordinador, "CoordinadorCae", _gestorDelCoordinador);
        dentro.EsExitoso.Should().BeTrue(dentro.EsFallido ? dentro.Error.Codigo : null);
    }

    /// <summary>
    /// Outbound: el Coordinador CAE de un Operador CAE externo, operando el Tenant
    /// propietario por delegación, reparte entre los Gestores CAE de su organización que
    /// le reportan. Y un Coordinador CAE del Tenant propietario no puede ceder el Cliente
    /// empresarial a un Gestor CAE del Operador CAE externo: no le reporta (D-001).
    /// </summary>
    [Fact]
    public async Task Outbound_el_Coordinador_CAE_delegado_reparte_entre_sus_Gestores_CAE_y_el_propio_no_cede_a_uno_externo()
    {
        var cedido = await ReasignarAsync(_coordinador, "CoordinadorCae", _gestorDelegado);
        cedido.EsFallido.Should().BeTrue();
        cedido.Error.Codigo.Should().Be("Cliente.DestinoFueraDeAlcance");
        await AfirmarSinCambiosAsync();

        (await ReasignarAsync(_administrador, "Administrador", _gestorDelegado)).EsExitoso.Should().BeTrue("preparación");

        var resultado = await ReasignarAsync(_coordinadorDelegado, "CoordinadorCae", _otroGestorDelegado, _operadorExterno.Id);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);
        await using var contexto = ContextoPropietario();
        (await contexto.AsignacionesCartera.AsNoTracking()
                .Where(c => c.AmbitoRelacionClienteId == _clienteId && c.Estado == EstadoAsignacion.Vigente)
                .Select(c => c.UsuarioId)
                .ToListAsync())
            .Should().Equal(_otroGestorDelegado);
    }

    [Fact]
    public async Task El_writer_rechaza_por_su_cuenta_a_un_Coordinador_CAE_delegado_como_destino()
    {
        await using (var contexto = ContextoRuntime(_administrador, "Administrador"))
        {
            var accion = () => Writer(contexto).ReasignarCarteraClienteAsync(_clienteId, _coordinadorDelegado);

            (await accion.Should().ThrowAsync<UnauthorizedAccessException>()).Which.Message.Should().Contain("GestorCae");
        }

        // Control positivo, en un contexto limpio (el anterior conserva la cartera
        // que cerró antes de lanzar): el mismo camino con el Gestor CAE delegado sí escribe.
        await using (var contexto = ContextoRuntime(_administrador, "Administrador"))
        {
            await Writer(contexto).ReasignarCarteraClienteAsync(_clienteId, _gestorDelegado);
            await contexto.SaveChangesAsync();
        }

        await using var comprobacion = ContextoPropietario();
        (await comprobacion.AsignacionesCartera.AsNoTracking()
                .Where(c => c.AmbitoRelacionClienteId == _clienteId && c.Estado == EstadoAsignacion.Vigente)
                .Select(c => c.UsuarioId)
                .ToListAsync())
            .Should().Equal(_gestorDelegado);
    }

    private AsignacionesOperativasWriter Writer(CaeManagerDbContext contexto) =>
        new(contexto, new TenantActualAmbiental { TenantId = _propietario.Id }, Usuario(_administrador, "Administrador"));

    // ── Arnés ─────────────────────────────────────────────────────────────

    private Guid Cuenta(string campo) =>
        (Guid)typeof(DestinoCarteraClienteBajoRuntimeTests)
            .GetField(campo, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(this)!;

    private async Task<Domain.Common.Result> ReasignarAsync(Guid actor, string rol, Guid destino, Guid? tenantOrigen = null)
    {
        await using var contexto = ContextoRuntime(actor, rol, tenantOrigen);
        var usuario = Usuario(actor, rol, tenantOrigen);
        var tenantActual = new TenantActualAmbiental { TenantId = _propietario.Id };
        var reasignador = new ReasignadorCarteraCliente(
            new EmpresaRepository(contexto), new ConfiguracionIaDocumentoClienteRepository(contexto),
            new NotificacionUsuarioRepository(contexto), usuario,
            new AlcanceDatosService(contexto, usuario, tenantActual, new SesionPrivilegiadaAusente()),
            new AsignacionesOperativasWriter(contexto, tenantActual, usuario),
            Directorio(contexto), new BloqueoCarteraUsuario(contexto));
        var handler = new ReasignarEjecutivoClienteCommandHandler(
            reasignador, contexto, usuario, contexto, new TransaccionDeComando(contexto));

        return await handler.Handle(new ReasignarEjecutivoClienteCommand(_clienteId, destino), CancellationToken.None);
    }

    private async Task AfirmarSinCambiosAsync()
    {
        await using var contexto = ContextoPropietario();
        (await contexto.Empresas.AsNoTracking().SingleAsync(e => e.Id == _clienteId)).EjecutivoUsuarioId.Should().Be(_gestorActual);
        (await contexto.AsignacionesCartera.AsNoTracking()
                .Where(c => c.AmbitoRelacionClienteId == _clienteId && c.Estado == EstadoAsignacion.Vigente)
                .Select(c => c.UsuarioId)
                .ToListAsync())
            .Should().Equal(_gestorActual);
        (await contexto.NotificacionesUsuario.AsNoTracking().CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// <c>userManager</c> va a null a propósito: <see cref="DirectorioUsuariosTenant.ObtenerAsync"/>
    /// no lo toca. Si algún día lo hiciera, el test estalla con un NRE, no da un verde silencioso.
    /// </summary>
    private DirectorioUsuariosTenant Directorio(CaeManagerDbContext contexto) =>
        new(null!, contexto, new TenantActualAmbiental { TenantId = _propietario.Id }, new PuertaAccesoDatos(), contexto);

    private CurrentUserServiceFalso Usuario(Guid usuarioId, string rol, Guid? tenantOrigen = null) =>
        new(usuarioId, rol, tenantOrigenId: tenantOrigen ?? _propietario.Id);

    private CaeManagerDbContext ContextoRuntime(Guid usuarioId, string rol, Guid? tenantOrigen = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _propietario.Id };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion),
                npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(
                    tenantActual, new SinTenantSeleccionado(), Usuario(usuarioId, rol, tenantOrigen), BaseDatosPostgresDePruebas.FirmanteContextoRls),
                new ConcurrenciaOptimistaInterceptor())
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private CaeManagerDbContext ContextoPropietario()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _propietario.Id };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class SinTenantSeleccionado : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
