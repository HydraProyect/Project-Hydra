using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Commands.CrearDelegacionTenant;
using CaeManager.Application.Tenants.Queries.AutorizarOperadorCaeExterno;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Operaciones;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// Incremento 1b del aprovisionamiento (opción 1′, decisión del propietario
/// 2026-09-22): el Administrador de un Tenant propietario YA existente autoriza a
/// un Operador CAE externo con <see cref="CrearDelegacionTenantCommand"/>, que el
/// Actor de Plataforma TALVEG solo puede preseleccionar.
///
/// <para>
/// La autoridad se modela con <see cref="AutorizacionDelegacionFalsa.AdministradorDe"/>:
/// solo dice «sí» cuando le preguntan por el Tenant del que la persona es
/// Administrador, igual que la implementación real. Cada rechazo por identidad
/// (Gestor CAE, Administrador de otro Tenant, Actor de Plataforma) se prueba contra
/// Identity real en <c>AutorizarOperadorCaeExternoRlsTests</c> (integración): este
/// fichero prueba qué hace la capa Application con la respuesta.
/// </para>
/// </summary>
public class AutorizarOperadorCaeExternoTests
{
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly Tenant _propietario = new("Refrielectric");
    private readonly Tenant _operador = new("ArcoSPA", PerfilVocabularioTenant.Consultora);
    private readonly Tenant _otroOperador = new("Prevención Norte", PerfilVocabularioTenant.Consultora);
    private readonly Tenant _otroPropietario = new("Laboratorios Dexter");
    private readonly Tenant _plataforma = CrearPlataformaConPerfilConsultora();

    private readonly TenantsQueryContextFalso _tenants = new();
    private readonly DelegacionTenantRepositorioFalso _vinculos = new();
    private readonly AsignacionesOperativasWriterFalso _writer = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();

    public AutorizarOperadorCaeExternoTests() =>
        _tenants.ListaTenants.AddRange([_propietario, _operador, _otroOperador, _otroPropietario, _plataforma]);

    /// <summary>
    /// Con perfil Consultora a propósito: así solo la guarda de plataforma puede
    /// rechazarlo; sin esto, la de perfil lo taparía y la mutación no se vería.
    /// </summary>
    private static Tenant CrearPlataformaConPerfilConsultora()
    {
        var plataforma = new Tenant("TALVEG", PerfilVocabularioTenant.Consultora);
        plataforma.MarcarComoPlataforma();
        return plataforma;
    }

    private CrearDelegacionTenantCommandHandler HandlerComo(AutorizacionDelegacionFalsa autorizacion) =>
        new(_vinculos, _tenants, _writer, autorizacion, new CurrentUserServiceFalso(_usuario), _unitOfWork);

    private void NoSeEscribioNada()
    {
        _vinculos.Delegaciones.Should().BeEmpty();
        _writer.OperacionesAbiertas.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    // ── El comando: quién ejecuta y a quién se autoriza ─────────────────────

    [Fact]
    public async Task El_administrador_del_tenant_propietario_autoriza_al_operador_cae_externo()
    {
        var resultado = await HandlerComo(AutorizacionDelegacionFalsa.AdministradorDe(_propietario.Id)).Handle(
            new CrearDelegacionTenantCommand(_operador.Id, _propietario.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var vinculo = _vinculos.Delegaciones.Should().ContainSingle().Subject;
        vinculo.TenantConsultoraId.Should().Be(_operador.Id);
        vinculo.TenantClienteId.Should().Be(_propietario.Id, "la propiedad de los datos no cambia: cambia quién opera");
        vinculo.Proposito.Should().Be(PropositoDelegacion.OperadorExterno);
        _writer.OperacionesAbiertas.Should().Equal((_propietario.Id, _operador.Id));
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task El_administrador_del_operador_no_puede_autorizarse_a_si_mismo()
    {
        // La parte que RECIBE el acceso tiene el mismo rol, pero en el Tenant
        // equivocado: ADR-004 § 12.2.
        var resultado = await HandlerComo(AutorizacionDelegacionFalsa.AdministradorDe(_operador.Id)).Handle(
            new CrearDelegacionTenantCommand(_operador.Id, _propietario.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoAutorizado");
        NoSeEscribioNada();
    }

    [Fact]
    public async Task El_administrador_de_otro_tenant_propietario_no_autoriza_por_este()
    {
        var resultado = await HandlerComo(AutorizacionDelegacionFalsa.AdministradorDe(_otroPropietario.Id)).Handle(
            new CrearDelegacionTenantCommand(_operador.Id, _propietario.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoAutorizado");
        NoSeEscribioNada();
    }

    [Fact]
    public async Task Rechaza_como_operador_a_un_tenant_propietario_que_no_es_operador_cae_externo()
    {
        var resultado = await HandlerComo(AutorizacionDelegacionFalsa.AdministradorDe(_propietario.Id)).Handle(
            new CrearDelegacionTenantCommand(_otroPropietario.Id, _propietario.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.ConsultoraNoEncontrada");
        NoSeEscribioNada();
    }

    [Fact]
    public async Task Rechaza_como_operador_al_tenant_de_plataforma()
    {
        // TALVEG no es Operador CAE por defecto (ADR-011 § 1): ni siquiera el
        // Administrador del Tenant propietario puede convertirlo en uno por esta vía.
        var resultado = await HandlerComo(AutorizacionDelegacionFalsa.AdministradorDe(_propietario.Id)).Handle(
            new CrearDelegacionTenantCommand(_plataforma.Id, _propietario.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.ConsultoraNoEncontrada");
        NoSeEscribioNada();
    }

    [Fact]
    public async Task Rechaza_como_cliente_delegante_al_tenant_de_plataforma_aunque_la_autorizacion_diga_que_si()
    {
        // Defensa en profundidad (hallazgo de Codex, alto): aunque la autorización
        // compartida devolviera que sí por el motivo reflexivo de arriba, este comando
        // —quien escribe de verdad— no debe crear una DelegacionTenant cuyo Cliente
        // Delegante sea el Tenant de plataforma. No depende de que la consulta se
        // comporte bien.
        var resultado = await HandlerComo(AutorizacionDelegacionFalsa.AdministradorDe(_plataforma.Id)).Handle(
            new CrearDelegacionTenantCommand(_operador.Id, _plataforma.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.ClienteNoEncontrado");
        NoSeEscribioNada();
    }

    [Fact]
    public async Task Rechaza_con_mensaje_si_otro_operador_ya_opera_el_tenant_propietario()
    {
        _tenants.ListaDelegacionesTenant.Add(new DelegacionTenant(_otroOperador.Id, _propietario.Id));

        var resultado = await HandlerComo(AutorizacionDelegacionFalsa.AdministradorDe(_propietario.Id)).Handle(
            new CrearDelegacionTenantCommand(_operador.Id, _propietario.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.OtroOperadorVigente");
        NoSeEscribioNada();
    }

    [Fact]
    public async Task Un_operador_anterior_revocado_o_el_soporte_de_plataforma_no_bloquean()
    {
        var revocado = new DelegacionTenant(_otroOperador.Id, _propietario.Id);
        revocado.Desactivar();
        _tenants.ListaDelegacionesTenant.AddRange([revocado, DelegacionTenant.ParaSoporte(_plataforma.Id, _propietario.Id)]);

        var resultado = await HandlerComo(AutorizacionDelegacionFalsa.AdministradorDe(_propietario.Id)).Handle(
            new CrearDelegacionTenantCommand(_operador.Id, _propietario.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue("control positivo de la guarda anterior: solo un Operador ACTIVO bloquea");
    }

    [Fact]
    public async Task El_otro_operador_de_otro_tenant_propietario_no_bloquea()
    {
        _tenants.ListaDelegacionesTenant.Add(new DelegacionTenant(_otroOperador.Id, _otroPropietario.Id));

        var resultado = await HandlerComo(AutorizacionDelegacionFalsa.AdministradorDe(_propietario.Id)).Handle(
            new CrearDelegacionTenantCommand(_operador.Id, _propietario.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    // ── La consulta del Tenant propietario autorizante ──────────────────────

    private AutorizarOperadorCaeExternoQueriesHandler ConsultasComo(
        AutorizacionDelegacionFalsa autorizacion, Guid? tenantOrigen, ITenantsQueryContext? tenants = null) =>
        new(tenants ?? _tenants, autorizacion, new CurrentUserServiceFalso(_usuario, tenantOrigenId: tenantOrigen));

    [Fact]
    public async Task El_tenant_autorizante_es_el_de_origen_si_la_persona_lo_administra()
    {
        var autorizacion = AutorizacionDelegacionFalsa.AdministradorDe(_propietario.Id);

        var tenant = await ConsultasComo(autorizacion, _propietario.Id)
            .Handle(new ObtenerTenantPropietarioAutorizanteQuery(), CancellationToken.None);

        tenant.Should().Be(_propietario.Id);
        autorizacion.UltimoTenantConsultado.Should().Be(_propietario.Id);
    }

    [Fact]
    public async Task Sin_ser_administrador_del_tenant_de_origen_no_hay_tenant_autorizante()
    {
        // Administrador de OTRO Tenant, pero su cuenta pertenece a este: la
        // respuesta es no, igual que en el comando.
        var tenant = await ConsultasComo(AutorizacionDelegacionFalsa.AdministradorDe(_otroPropietario.Id), _propietario.Id)
            .Handle(new ObtenerTenantPropietarioAutorizanteQuery(), CancellationToken.None);

        tenant.Should().BeNull();
    }

    [Fact]
    public async Task Sin_tenant_de_origen_no_hay_tenant_autorizante()
    {
        var tenant = await ConsultasComo(new AutorizacionDelegacionFalsa(autoriza: true), tenantOrigen: null)
            .Handle(new ObtenerTenantPropietarioAutorizanteQuery(), CancellationToken.None);

        tenant.Should().BeNull();
    }

    [Fact]
    public async Task El_actor_de_plataforma_no_es_su_propio_tenant_autorizante_aunque_administre_su_tenant()
    {
        // Hallazgo de Codex (alto) sobre el incremento 1b: esta consulta pregunta la
        // autoridad de forma REFLEXIVA, contra el propio tenant de origen de quien
        // pregunta — ningún llamador anterior de PuedeGestionarDelegacionesAsync lo
        // hacía. Si ese tenant de origen es el de plataforma, "administra su propio
        // tenant" (AdministradorDe(_plataforma.Id) autoriza exactamente cuando le
        // preguntan por _plataforma.Id) coincide por construcción con la respuesta que
        // la autorización compartida daría — sin que EsPlataforma entre en juego. La
        // guarda tiene que cortar ANTES de delegar en esa autorización.
        var autorizacionQueSiempreDiceQueSi = AutorizacionDelegacionFalsa.AdministradorDe(_plataforma.Id);

        var tenant = await ConsultasComo(autorizacionQueSiempreDiceQueSi, tenantOrigen: _plataforma.Id)
            .Handle(new ObtenerTenantPropietarioAutorizanteQuery(), CancellationToken.None);

        tenant.Should().BeNull("TALVEG nunca es Tenant propietario de un Operador CAE externo (ADR-011 § 1)");
    }

    // ── La consulta del candidato (preselección y buscador) ─────────────────

    [Fact]
    public async Task La_preseleccion_por_id_resuelve_el_operador_con_solo_id_y_nombre()
    {
        var candidato = await ConsultasComo(AutorizacionDelegacionFalsa.AdministradorDe(_propietario.Id), _propietario.Id)
            .Handle(new BuscarOperadorCaeExternoAutorizableQuery(_operador.Id, null), CancellationToken.None);

        candidato.Should().Be(new OperadorCaeExternoAutorizableDto(_operador.Id, "ArcoSPA"));
    }

    [Fact]
    public async Task Sin_autoridad_la_busqueda_no_lee_el_catalogo_de_tenants()
    {
        // El contexto lanza si se lee: la autoridad corta ANTES de tocar el
        // catálogo global, así que ni por tiempos ni por errores se aprende qué
        // Ids existen.
        var candidato = await ConsultasComo(
                AutorizacionDelegacionFalsa.AdministradorDe(_otroPropietario.Id), _propietario.Id, new ContextoQueNoDebeLeerse())
            .Handle(new BuscarOperadorCaeExternoAutorizableQuery(_operador.Id, null), CancellationToken.None);

        candidato.Should().BeNull();
    }

    [Theory]
    [InlineData("arcospa")]
    [InlineData("  ARCOSPA ")]
    public async Task El_buscador_encuentra_por_nombre_exacto_sin_distinguir_mayusculas(string nombre)
    {
        var candidato = await ConsultasComo(AutorizacionDelegacionFalsa.AdministradorDe(_propietario.Id), _propietario.Id)
            .Handle(new BuscarOperadorCaeExternoAutorizableQuery(null, nombre), CancellationToken.None);

        candidato?.TenantId.Should().Be(_operador.Id);
        candidato.Should().NotBeNull();
    }

    [Theory]
    [InlineData("Arco")]
    [InlineData("a")]
    [InlineData("")]
    [InlineData(null)]
    public async Task El_buscador_no_enumera_por_fragmentos_ni_en_vacio(string? nombre)
    {
        var candidato = await ConsultasComo(AutorizacionDelegacionFalsa.AdministradorDe(_propietario.Id), _propietario.Id)
            .Handle(new BuscarOperadorCaeExternoAutorizableQuery(null, nombre), CancellationToken.None);

        candidato.Should().BeNull();
    }

    [Fact]
    public async Task No_ofrece_como_operador_un_tenant_propietario_ni_la_plataforma_ni_el_propio_tenant()
    {
        // El propio Tenant se prueba desde el Administrador de un Operador CAE
        // externo: su Tenant SÍ es elegible, pero no puede autorizarse a sí mismo.
        var comoOperador = ConsultasComo(AutorizacionDelegacionFalsa.AdministradorDe(_operador.Id), _operador.Id);
        var comoPropietario = ConsultasComo(AutorizacionDelegacionFalsa.AdministradorDe(_propietario.Id), _propietario.Id);

        (await comoPropietario.Handle(new BuscarOperadorCaeExternoAutorizableQuery(_otroPropietario.Id, null), CancellationToken.None))
            .Should().BeNull("un Tenant propietario no es Operador CAE externo");
        (await comoPropietario.Handle(new BuscarOperadorCaeExternoAutorizableQuery(_plataforma.Id, null), CancellationToken.None))
            .Should().BeNull("TALVEG no es Operador CAE por defecto");
        (await comoOperador.Handle(new BuscarOperadorCaeExternoAutorizableQuery(_operador.Id, null), CancellationToken.None))
            .Should().BeNull("un Tenant no se autoriza a sí mismo");
        (await comoOperador.Handle(new BuscarOperadorCaeExternoAutorizableQuery(_otroOperador.Id, null), CancellationToken.None))
            .Should().NotBeNull("control positivo: el mismo Administrador sí resuelve a otro Operador");
    }

    private sealed class ContextoQueNoDebeLeerse : ITenantsQueryContext
    {
        private static IQueryable<T> Prohibido<T>() => throw new InvalidOperationException("Lectura del catálogo antes de autorizar.");
        public IQueryable<Tenant> Tenants => Prohibido<Tenant>();
        public IQueryable<DelegacionTenant> DelegacionesTenant => Prohibido<DelegacionTenant>();
        public IQueryable<AsignacionOperadorDelegado> AsignacionesOperadorDelegado => Prohibido<AsignacionOperadorDelegado>();
        public IQueryable<RegistroActividadSoporte> RegistrosActividadSoporte => Prohibido<RegistroActividadSoporte>();
    }
}
