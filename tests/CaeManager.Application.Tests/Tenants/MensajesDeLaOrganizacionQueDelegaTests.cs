using CaeManager.Application.Tenants.Commands.CrearAsignacionOperadorDelegado;
using CaeManager.Application.Tenants.Commands.CrearClienteDelegante;
using CaeManager.Application.Tenants.Commands.CrearDelegacionTenant;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Operaciones;
using CaeManager.Application.Tests.Plataforma;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// Decisión de Chris (2026-10-03): en los mensajes de pantalla de las altas de delegación, «Cliente
/// Delegante» / «este Cliente» se sustituye por el nombre de la organización que delega (el Tenant
/// propietario). Dos reglas que estos tests fijan a la vez:
/// <list type="bullet">
/// <item>Donde el nombre es legítimo, el mensaje lo lleva (<c>YaActiva</c>: la autoridad ya está
/// confirmada; <c>SinPermiso</c> del alta: es el nombre que escribió quien la pide).</item>
/// <item>Donde la autoridad aún no se ha confirmado, el mensaje NO lleva el nombre ni nada que lo
/// distinga: quien no es Administrador del Tenant propietario no puede averiguar, por el texto del error,
/// qué identificadores de tenant son organizaciones reales (comentario de <c>CrearDelegacionTenantCommand</c>).</item>
/// </list>
/// Y ninguno de los textos vuelve a decir «Cliente» a secas.
/// </summary>
public class MensajesDeLaOrganizacionQueDelegaTests
{
    private const string NombrePropietario = "Hostelería Los Pinos";

    private readonly Guid _usuario = Guid.NewGuid();
    private readonly Tenant _propietario = new(NombrePropietario);
    private readonly Tenant _operador = CrearOperadorCaeExterno("Prevención Norte");
    private readonly TenantsQueryContextFalso _tenants = new();
    private readonly DelegacionTenantRepositorioFalso _vinculos = new();
    private readonly AsignacionesOperativasWriterFalso _writer = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();

    public MensajesDeLaOrganizacionQueDelegaTests() =>
        _tenants.ListaTenants.AddRange([_propietario, _operador]);

    private static Tenant CrearOperadorCaeExterno(string nombre)
    {
        var tenant = new Tenant(nombre, PerfilVocabularioTenant.Consultora);
        tenant.HabilitarComoOperadorCaeExterno();
        return tenant;
    }

    private CrearDelegacionTenantCommandHandler HandlerDeDelegacion(AutorizacionDelegacionFalsa autorizacion) =>
        new(_vinculos, _tenants, _writer, autorizacion, new CurrentUserServiceFalso(_usuario), _unitOfWork);

    // ── CrearDelegacionTenant ───────────────────────────────────────────────

    [Fact]
    public async Task Una_delegacion_ya_activa_nombra_a_la_organizacion_que_delega()
    {
        _vinculos.Agregar(new DelegacionTenant(_operador.Id, _propietario.Id));

        var resultado = await HandlerDeDelegacion(AutorizacionDelegacionFalsa.AdministradorDe(_propietario.Id)).Handle(
            new CrearDelegacionTenantCommand(_operador.Id, _propietario.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.YaActiva");
        resultado.Error.Mensaje.Should().Contain($"«{NombrePropietario}»");
        resultado.Error.Mensaje.Should().NotContain("Cliente");
    }

    [Fact]
    public async Task Sin_autoridad_el_mensaje_no_nombra_a_la_organizacion_que_delega()
    {
        var resultado = await HandlerDeDelegacion(AutorizacionDelegacionFalsa.AdministradorDe(Guid.NewGuid())).Handle(
            new CrearDelegacionTenantCommand(_operador.Id, _propietario.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoAutorizado");
        resultado.Error.Mensaje.Should().NotContain(NombrePropietario,
            "quien no tiene autoridad no debe poder averiguar por el texto qué tenant es real");
        resultado.Error.Mensaje.Should().Contain("organización que delega").And.NotContain("Cliente");
    }

    [Fact]
    public async Task La_organizacion_que_delega_no_encontrada_se_dice_sin_cliente_a_secas()
    {
        // Con autoridad para ese Id, pero sin ningún Tenant detrás.
        var desconocido = Guid.NewGuid();
        var sinTenant = await HandlerDeDelegacion(AutorizacionDelegacionFalsa.AdministradorDe(desconocido)).Handle(
            new CrearDelegacionTenantCommand(_operador.Id, desconocido), CancellationToken.None);

        sinTenant.Error.Codigo.Should().Be("DelegacionTenant.ClienteNoEncontrado");
        sinTenant.Error.Mensaje.Should().Contain("organización que delega").And.NotContain("Cliente");
    }

    [Fact]
    public async Task El_tenant_de_plataforma_como_organizacion_que_delega_no_se_nombra()
    {
        var plataforma = new Tenant("TALVEG");
        plataforma.MarcarComoPlataforma();
        _tenants.ListaTenants.Add(plataforma);

        var resultado = await HandlerDeDelegacion(AutorizacionDelegacionFalsa.AdministradorDe(plataforma.Id)).Handle(
            new CrearDelegacionTenantCommand(_operador.Id, plataforma.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.ClienteNoEncontrado");
        resultado.Error.Mensaje.Should().NotContain("TALVEG").And.NotContain("Cliente");
    }

    [Fact]
    public void El_validador_pide_la_organizacion_que_delega_sin_cliente_a_secas()
    {
        var errores = new CrearDelegacionTenantCommandValidator()
            .Validate(new CrearDelegacionTenantCommand(_operador.Id, Guid.Empty))
            .Errors.Select(e => e.ErrorMessage).ToList();

        errores.Should().Contain("Selecciona la organización que delega.");
        errores.Should().NotContain(m => m.Contains("Cliente"));
    }

    // ── CrearAsignacionOperadorDelegado ─────────────────────────────────────

    [Fact]
    public async Task Asignar_un_operador_sin_autoridad_no_nombra_a_la_organizacion_que_delega()
    {
        var delegacion = new DelegacionTenant(_operador.Id, _propietario.Id);
        var delegaciones = new DelegacionTenantRepositorioFalso();
        delegaciones.Agregar(delegacion);

        var handler = new CrearAsignacionOperadorDelegadoCommandHandler(
            new AsignacionOperadorDelegadoRepositorioFalso(), delegaciones,
            new DirectorioUsuariosServiceFalso(esVisible: true, tenantDelUsuario: _operador.Id),
            _writer, AutorizacionDelegacionFalsa.AdministradorDe(Guid.NewGuid()),
            new CurrentUserServiceFalso(_usuario), _unitOfWork, _tenants);

        var resultado = await handler.Handle(
            new CrearAsignacionOperadorDelegadoCommand(delegacion.Id, Guid.NewGuid(), "GestorCae"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("AsignacionOperadorDelegado.NoAutorizado");
        resultado.Error.Mensaje.Should().NotContain(NombrePropietario);
        resultado.Error.Mensaje.Should().Contain("organización que delega").And.NotContain("Cliente");
    }

    [Fact]
    public async Task Asignar_un_operador_sobre_el_tenant_de_plataforma_dice_lo_mismo_que_la_denegacion()
    {
        var plataforma = new Tenant("TALVEG");
        plataforma.MarcarComoPlataforma();
        _tenants.ListaTenants.Add(plataforma);
        var delegacion = new DelegacionTenant(_operador.Id, plataforma.Id);
        var delegaciones = new DelegacionTenantRepositorioFalso();
        delegaciones.Agregar(delegacion);

        CrearAsignacionOperadorDelegadoCommandHandler Handler(AutorizacionDelegacionFalsa autorizacion) => new(
            new AsignacionOperadorDelegadoRepositorioFalso(), delegaciones,
            new DirectorioUsuariosServiceFalso(esVisible: true, tenantDelUsuario: _operador.Id),
            _writer, autorizacion, new CurrentUserServiceFalso(_usuario), _unitOfWork, _tenants);

        var orden = new CrearAsignacionOperadorDelegadoCommand(delegacion.Id, Guid.NewGuid(), "GestorCae");
        var porPlataforma = await Handler(new AutorizacionDelegacionFalsa(autoriza: true)).Handle(orden, CancellationToken.None);
        var porAutoridad = await Handler(AutorizacionDelegacionFalsa.AdministradorDe(Guid.NewGuid())).Handle(orden, CancellationToken.None);

        porPlataforma.Error.Mensaje.Should().Be(porAutoridad.Error.Mensaje,
            "no se revela si la delegación existe ni que el tenant es el de plataforma");
        porPlataforma.Error.Mensaje.Should().NotContain("Cliente").And.NotContain("TALVEG");
    }

    // ── CrearClienteDelegante ───────────────────────────────────────────────

    [Fact]
    public async Task El_alta_sin_permiso_nombra_la_organizacion_que_se_pedia_dar_de_alta()
    {
        var handler = new CrearClienteDeleganteCommandHandler(
            tenantRepositorio: null!, delegacionRepositorio: null!, asignacionRepositorio: null!,
            parametroSistemaRepositorio: null!, AutorizacionAdminPlataformaFalsa.SinNada(),
            new CurrentUserServiceFalso(_usuario, tenantOrigenId: _operador.Id),
            asignacionesWriter: null!, unitOfWork: null!);

        var resultado = await handler.Handle(
            new CrearClienteDeleganteCommand("Constructora Boreal"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ClienteDelegante.SinPermiso");
        resultado.Error.Mensaje.Should().Contain("«Constructora Boreal»").And.NotContain("Cliente");
    }

    [Fact]
    public void El_validador_del_alta_pide_el_nombre_de_la_organizacion()
    {
        var errores = new CrearClienteDeleganteCommandValidator()
            .Validate(new CrearClienteDeleganteCommand(""))
            .Errors.Select(e => e.ErrorMessage).ToList();

        errores.Should().Contain("El nombre de la organización es obligatorio.");
        errores.Should().NotContain(m => m.Contains("Cliente"));
    }
}
