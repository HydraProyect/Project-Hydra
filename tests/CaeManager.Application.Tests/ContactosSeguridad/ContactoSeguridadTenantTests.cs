using CaeManager.Application.Common;
using CaeManager.Application.ContactosSeguridad;
using CaeManager.Application.ContactosSeguridad.Commands.ProponerContactoSeguridad;
using CaeManager.Application.ContactosSeguridad.Commands.RechazarContactoSeguridad;
using CaeManager.Application.ContactosSeguridad.Commands.ValidarContactoSeguridad;
using CaeManager.Application.ContactosSeguridad.Queries.ObtenerContactoSeguridadDeMiOrganizacion;
using CaeManager.Application.ContactosSeguridad.Queries.ObtenerContactoSeguridadVigente;
using CaeManager.Application.ContactosSeguridad.Queries.ObtenerContactosSeguridadPropuestos;
using CaeManager.Application.ContactosSeguridad.Queries.ResolverDestinatariosAvisoSeguridad;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using FluentAssertions;

namespace CaeManager.Application.Tests.ContactosSeguridad;

/// <summary>
/// Contacto de seguridad del Tenant beneficiario (aviso de violaciones de seguridad, incremento 3a): el
/// Administrador del Tenant propone, un Actor de Plataforma TALVEG con la concesión AdminPlataforma global
/// valida o rechaza, y nadie más. La RLS y la función de resolución repiten estas barreras; aquí se fija la
/// de Application.
/// </summary>
public class ContactoSeguridadTenantTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid Usuario = Guid.NewGuid();

    // ── Proponer ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task El_Administrador_del_Tenant_propone_y_queda_Propuesto()
    {
        var repo = new RepositorioFalso();
        var uow = new UnitOfWorkFalso();

        var resultado = await Proponer(repo, uow, tenantActual: TenantA, administradorDe: TenantA)
            .Handle(new ProponerContactoSeguridadCommand("Marta Ruiz", "marta@ejemplo.test", null), default);

        resultado.EsExitoso.Should().BeTrue();
        repo.Agregados.Should().ContainSingle();
        var propuesto = repo.Agregados[0];
        propuesto.Estado.Should().Be(EstadoContactoSeguridad.Propuesto);
        propuesto.PropuestoPorUsuarioId.Should().Be(Usuario);
        uow.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Quien_no_es_Administrador_del_Tenant_actual_no_propone()
    {
        var repo = new RepositorioFalso();
        var uow = new UnitOfWorkFalso();

        // Administrador de B con A seleccionado (p. ej. por la vía de Operación): no es Administrador de A.
        var resultado = await Proponer(repo, uow, tenantActual: TenantA, administradorDe: TenantB)
            .Handle(new ProponerContactoSeguridadCommand("Marta Ruiz", "marta@ejemplo.test", null), default);

        resultado.Error.Should().Be(ErroresContactoSeguridad.SoloAdministradorDelTenant);
        repo.Agregados.Should().BeEmpty();
        uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_Tenant_actual_no_se_propone()
    {
        var repo = new RepositorioFalso();

        var resultado = await Proponer(repo, new UnitOfWorkFalso(), tenantActual: null, administradorDe: TenantA)
            .Handle(new ProponerContactoSeguridadCommand("Marta Ruiz", "marta@ejemplo.test", null), default);

        resultado.Error.Should().Be(ErroresContactoSeguridad.SoloAdministradorDelTenant);
        repo.Agregados.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_propuesta_nueva_sustituye_a_la_pendiente_y_no_toca_la_validada()
    {
        var pendiente = ContactoSeguridadTenant.Proponer("Antigua", "antigua@ejemplo.test", null, Usuario, DateTime.UtcNow.AddDays(-1));
        var validada = ContactoSeguridadTenant.Proponer("Vigente", "vigente@ejemplo.test", null, Usuario, DateTime.UtcNow.AddDays(-9));
        typeof(ContactoSeguridadTenant).GetProperty(nameof(ContactoSeguridadTenant.Estado))!
            .SetValue(validada, EstadoContactoSeguridad.Validado);
        var repo = new RepositorioFalso { PendienteYVigente = [pendiente, validada] };

        var resultado = await Proponer(repo, new UnitOfWorkFalso(), tenantActual: TenantA, administradorDe: TenantA)
            .Handle(new ProponerContactoSeguridadCommand("Nueva", "nueva@ejemplo.test", "+34 600 000 000"), default);

        resultado.EsExitoso.Should().BeTrue();
        pendiente.Estado.Should().Be(EstadoContactoSeguridad.Sustituido);
        pendiente.SustitucionEnUtc.Should().NotBeNull();
        validada.Estado.Should().Be(EstadoContactoSeguridad.Validado, "el vigente sigue recibiendo avisos hasta validar el nuevo");
        repo.Agregados.Should().ContainSingle().Which.Telefono.Should().Be("+34 600 000 000");
    }

    [Theory]
    [InlineData("", "marta@ejemplo.test", null, nameof(ProponerContactoSeguridadCommand.Nombre))]
    [InlineData("   ", "marta@ejemplo.test", null, nameof(ProponerContactoSeguridadCommand.Nombre))]
    [InlineData("Marta", "", null, nameof(ProponerContactoSeguridadCommand.Correo))]
    [InlineData("Marta", "no-es-un-correo", null, nameof(ProponerContactoSeguridadCommand.Correo))]
    [InlineData("Marta", "marta@ejemplo.test", "abc", nameof(ProponerContactoSeguridadCommand.Telefono))]
    [InlineData("Marta", "marta@ejemplo.test", "12", nameof(ProponerContactoSeguridadCommand.Telefono))]
    public void El_validador_de_la_propuesta_rechaza_datos_no_validos(string nombre, string correo, string? telefono, string campo)
    {
        var resultado = new ProponerContactoSeguridadCommandValidator()
            .Validate(new ProponerContactoSeguridadCommand(nombre, correo, telefono));

        resultado.Errors.Should().Contain(e => e.PropertyName == campo);
    }

    [Fact]
    public void El_validador_de_la_propuesta_acepta_datos_validos_y_limita_longitudes()
    {
        var validador = new ProponerContactoSeguridadCommandValidator();

        validador.Validate(new ProponerContactoSeguridadCommand("Marta Ruiz", "marta@ejemplo.test", "+34 (600) 00-00-00"))
            .IsValid.Should().BeTrue();
        validador.Validate(new ProponerContactoSeguridadCommand("Marta Ruiz", "marta@ejemplo.test", null))
            .IsValid.Should().BeTrue();
        validador.Validate(new ProponerContactoSeguridadCommand(
                new string('a', ContactoSeguridadTenant.LongitudMaximaNombre + 1), "marta@ejemplo.test", null))
            .Errors.Should().Contain(e => e.PropertyName == nameof(ProponerContactoSeguridadCommand.Nombre));
    }

    // ── Validar y rechazar ────────────────────────────────────────────────────

    [Fact]
    public async Task El_Actor_de_Plataforma_con_concesion_global_valida()
    {
        var plataforma = new PlataformaFalsa();
        var contacto = Guid.NewGuid();

        var resultado = await Validar(plataforma, AutorizacionAdminPlataformaFalsa.Global())
            .Handle(new ValidarContactoSeguridadCommand(contacto), default);

        resultado.EsExitoso.Should().BeTrue();
        plataforma.Resoluciones.Should().ContainSingle().Which.Should().Be((contacto, (string?)null));
    }

    [Fact]
    public async Task Una_concesion_acotada_no_valida_aunque_cubra_el_Tenant()
    {
        var plataforma = new PlataformaFalsa();

        var resultado = await Validar(plataforma, AutorizacionAdminPlataformaFalsa.AcotadaA(TenantA))
            .Handle(new ValidarContactoSeguridadCommand(Guid.NewGuid()), default);

        resultado.Error.Should().Be(ErroresContactoSeguridad.SoloActorDePlataforma);
        plataforma.Resoluciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_concesion_no_se_valida()
    {
        var plataforma = new PlataformaFalsa();

        var resultado = await Validar(plataforma, AutorizacionAdminPlataformaFalsa.SinNada())
            .Handle(new ValidarContactoSeguridadCommand(Guid.NewGuid()), default);

        resultado.Error.Should().Be(ErroresContactoSeguridad.SoloActorDePlataforma);
        plataforma.Resoluciones.Should().BeEmpty();
    }

    [Fact]
    public async Task En_Sesion_Privilegiada_no_se_valida_ni_se_rechaza()
    {
        var plataforma = new PlataformaFalsa();
        var sesion = new SesionPrivilegiadaFalsa(new SesionPrivilegiadaActiva(
            Guid.NewGuid(), Guid.NewGuid(), TenantA, CapacidadPrivilegio.AdminPlataforma, null));

        var validar = await new ValidarContactoSeguridadCommandHandler(
                new CurrentUserServiceFalso(Usuario), sesion, AutorizacionAdminPlataformaFalsa.Global(), plataforma)
            .Handle(new ValidarContactoSeguridadCommand(Guid.NewGuid()), default);
        var rechazar = await new RechazarContactoSeguridadCommandHandler(
                new CurrentUserServiceFalso(Usuario), sesion, AutorizacionAdminPlataformaFalsa.Global(), plataforma)
            .Handle(new RechazarContactoSeguridadCommand(Guid.NewGuid(), "Motivo"), default);

        validar.Error.Should().Be(ErroresContactoSeguridad.SoloActorDePlataforma);
        rechazar.Error.Should().Be(ErroresContactoSeguridad.SoloActorDePlataforma);
        plataforma.Resoluciones.Should().BeEmpty();
    }

    [Fact]
    public async Task El_Actor_de_Plataforma_con_concesion_global_rechaza_con_el_motivo_recortado()
    {
        var plataforma = new PlataformaFalsa();
        var contacto = Guid.NewGuid();

        var resultado = await Rechazar(plataforma, AutorizacionAdminPlataformaFalsa.Global())
            .Handle(new RechazarContactoSeguridadCommand(contacto, "  La persona no pertenece a la organización  "), default);

        resultado.EsExitoso.Should().BeTrue();
        plataforma.Resoluciones.Should().ContainSingle()
            .Which.Should().Be((contacto, (string?)"La persona no pertenece a la organización"));
    }

    [Fact]
    public async Task Rechazar_sin_concesion_global_no_llega_a_la_base()
    {
        var plataforma = new PlataformaFalsa();

        var resultado = await Rechazar(plataforma, AutorizacionAdminPlataformaFalsa.AcotadaA(TenantA))
            .Handle(new RechazarContactoSeguridadCommand(Guid.NewGuid(), "Motivo"), default);

        resultado.Error.Should().Be(ErroresContactoSeguridad.SoloActorDePlataforma);
        plataforma.Resoluciones.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void El_motivo_del_rechazo_es_obligatorio(string motivo)
    {
        new RechazarContactoSeguridadCommandValidator()
            .Validate(new RechazarContactoSeguridadCommand(Guid.NewGuid(), motivo))
            .Errors.Should().Contain(e => e.PropertyName == nameof(RechazarContactoSeguridadCommand.Motivo));
    }

    [Fact]
    public void El_motivo_del_rechazo_tiene_longitud_maxima_y_la_propuesta_es_obligatoria()
    {
        var validador = new RechazarContactoSeguridadCommandValidator();

        validador.Validate(new RechazarContactoSeguridadCommand(Guid.NewGuid(),
                new string('m', ContactoSeguridadTenant.LongitudMaximaMotivoRechazo + 1)))
            .Errors.Should().Contain(e => e.PropertyName == nameof(RechazarContactoSeguridadCommand.Motivo));
        validador.Validate(new RechazarContactoSeguridadCommand(Guid.Empty, "Motivo"))
            .Errors.Should().Contain(e => e.PropertyName == nameof(RechazarContactoSeguridadCommand.ContactoId));
        new ValidarContactoSeguridadCommandValidator().Validate(new ValidarContactoSeguridadCommand(Guid.Empty))
            .IsValid.Should().BeFalse();
    }

    // ── Consultas ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task La_cola_de_propuestas_solo_se_entrega_con_concesion_global()
    {
        var plataforma = new PlataformaFalsa
        {
            Propuestos = [new ContactoSeguridadPropuestoDto(Guid.NewGuid(), TenantA, "Org A", "Marta", "m@ejemplo.test", null, DateTime.UtcNow)],
        };

        (await new ObtenerContactosSeguridadPropuestosQueryHandler(new CurrentUserServiceFalso(Usuario),
                AutorizacionAdminPlataformaFalsa.Global(), plataforma)
            .Handle(new ObtenerContactosSeguridadPropuestosQuery(), default)).Should().HaveCount(1);
        (await new ObtenerContactosSeguridadPropuestosQueryHandler(new CurrentUserServiceFalso(Usuario),
                AutorizacionAdminPlataformaFalsa.AcotadaA(TenantA), plataforma)
            .Handle(new ObtenerContactosSeguridadPropuestosQuery(), default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Los_destinatarios_solo_se_resuelven_con_concesion_global_y_sin_ids_repetidos()
    {
        var plataforma = new PlataformaFalsa();

        var denegado = await new ResolverDestinatariosAvisoSeguridadQueryHandler(new CurrentUserServiceFalso(Usuario),
                AutorizacionAdminPlataformaFalsa.AcotadaA(TenantA), plataforma)
            .Handle(new ResolverDestinatariosAvisoSeguridadQuery([TenantA]), default);
        denegado.EsFallido.Should().BeTrue();
        plataforma.TenantsResueltos.Should().BeNull();

        var concedido = await new ResolverDestinatariosAvisoSeguridadQueryHandler(new CurrentUserServiceFalso(Usuario),
                AutorizacionAdminPlataformaFalsa.Global(), plataforma)
            .Handle(new ResolverDestinatariosAvisoSeguridadQuery([TenantA, TenantA, Guid.Empty, TenantB]), default);
        concedido.EsExitoso.Should().BeTrue();
        plataforma.TenantsResueltos.Should().BeEquivalentTo([TenantA, TenantB]);
    }

    [Fact]
    public async Task El_contacto_vigente_de_otro_Tenant_no_se_entrega_sin_concesion_global()
    {
        var plataforma = new PlataformaFalsa
        {
            Vigente = new ContactoSeguridadDto(Guid.NewGuid(), "Marta", "m@ejemplo.test", null, DateTime.UtcNow, DateTime.UtcNow, null),
        };

        var ajeno = await new ObtenerContactoSeguridadVigenteQueryHandler(new TenantActualFijo(TenantA),
                new CurrentUserServiceFalso(Usuario), new AdministradorFalso(TenantA),
                AutorizacionAdminPlataformaFalsa.AcotadaA(TenantB), new RepositorioFalso(), plataforma)
            .Handle(new ObtenerContactoSeguridadVigenteQuery(TenantB), default);
        var global = await new ObtenerContactoSeguridadVigenteQueryHandler(new TenantActualFijo(TenantA),
                new CurrentUserServiceFalso(Usuario), new AdministradorFalso(null),
                AutorizacionAdminPlataformaFalsa.Global(), new RepositorioFalso(), plataforma)
            .Handle(new ObtenerContactoSeguridadVigenteQuery(TenantB), default);

        ajeno.Should().BeNull();
        global.Should().NotBeNull();
    }

    [Fact]
    public async Task Mi_organizacion_solo_la_ve_el_Administrador_y_oculta_un_rechazo_ya_superado()
    {
        var rechazado = ContactoSeguridadTenant.Proponer("Rechazada", "r@ejemplo.test", null, Usuario, DateTime.UtcNow.AddDays(-3));
        typeof(ContactoSeguridadTenant).GetProperty(nameof(ContactoSeguridadTenant.Estado))!
            .SetValue(rechazado, EstadoContactoSeguridad.Rechazado);
        typeof(ContactoSeguridadTenant).GetProperty(nameof(ContactoSeguridadTenant.ResueltoEnUtc))!
            .SetValue(rechazado, DateTime.UtcNow.AddDays(-2));
        var pendiente = ContactoSeguridadTenant.Proponer("Nueva", "n@ejemplo.test", null, Usuario, DateTime.UtcNow.AddDays(-1));
        var repo = new RepositorioFalso { PendienteYVigente = [pendiente], UltimoRechazado = rechazado };

        var noAdmin = await new ObtenerContactoSeguridadDeMiOrganizacionQueryHandler(new TenantActualFijo(TenantA),
                new CurrentUserServiceFalso(Usuario), new AdministradorFalso(TenantB), repo)
            .Handle(new ObtenerContactoSeguridadDeMiOrganizacionQuery(), default);
        var admin = await new ObtenerContactoSeguridadDeMiOrganizacionQueryHandler(new TenantActualFijo(TenantA),
                new CurrentUserServiceFalso(Usuario), new AdministradorFalso(TenantA), repo)
            .Handle(new ObtenerContactoSeguridadDeMiOrganizacionQuery(), default);

        noAdmin.Should().BeNull();
        admin!.Pendiente!.Nombre.Should().Be("Nueva");
        admin.UltimoRechazo.Should().BeNull("la propuesta pendiente es posterior al rechazo");
    }

    // ── Dobles ────────────────────────────────────────────────────────────────

    private static ProponerContactoSeguridadCommandHandler Proponer(
        RepositorioFalso repo, UnitOfWorkFalso uow, Guid? tenantActual, Guid? administradorDe) =>
        new(new TenantActualFijo(tenantActual), new CurrentUserServiceFalso(Usuario),
            new AdministradorFalso(administradorDe), repo, uow);

    private static ValidarContactoSeguridadCommandHandler Validar(PlataformaFalsa plataforma, IAutorizacionAdminPlataforma autorizacion) =>
        new(new CurrentUserServiceFalso(Usuario), new SesionPrivilegiadaFalsa(null), autorizacion, plataforma);

    private static RechazarContactoSeguridadCommandHandler Rechazar(PlataformaFalsa plataforma, IAutorizacionAdminPlataforma autorizacion) =>
        new(new CurrentUserServiceFalso(Usuario), new SesionPrivilegiadaFalsa(null), autorizacion, plataforma);

    private sealed class RepositorioFalso : IContactoSeguridadTenantRepository
    {
        public IReadOnlyList<ContactoSeguridadTenant> PendienteYVigente { get; init; } = [];
        public ContactoSeguridadTenant? UltimoRechazado { get; init; }
        public List<ContactoSeguridadTenant> Agregados { get; } = [];

        public Task<IReadOnlyList<ContactoSeguridadTenant>> ObtenerPendienteYVigenteAsync(CancellationToken cancellationToken) =>
            Task.FromResult(PendienteYVigente);

        public Task<ContactoSeguridadTenant?> ObtenerUltimoRechazadoAsync(CancellationToken cancellationToken) =>
            Task.FromResult(UltimoRechazado);

        public void Agregar(ContactoSeguridadTenant contacto) => Agregados.Add(contacto);
    }

    private sealed class PlataformaFalsa : IContactosSeguridadPlataforma
    {
        public IReadOnlyList<ContactoSeguridadPropuestoDto> Propuestos { get; init; } = [];
        public ContactoSeguridadDto? Vigente { get; init; }
        public List<(Guid ContactoId, string? Motivo)> Resoluciones { get; } = [];
        public IReadOnlyCollection<Guid>? TenantsResueltos { get; private set; }

        public Task<IReadOnlyList<ContactoSeguridadPropuestoDto>> ObtenerPropuestosAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Propuestos);

        public Task<ContactoSeguridadDto?> ObtenerVigenteAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(Vigente);

        public Task<Result> ResolverAsync(Guid contactoId, string? motivoRechazo, CancellationToken cancellationToken)
        {
            Resoluciones.Add((contactoId, motivoRechazo));
            return Task.FromResult(Result.Exito());
        }

        public Task<Result<IReadOnlyList<DestinatariosAvisoSeguridadTenant>>> ResolverDestinatariosAsync(
            IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken)
        {
            TenantsResueltos = tenantIds;
            return Task.FromResult(Result.Exito<IReadOnlyList<DestinatariosAvisoSeguridadTenant>>([]));
        }
    }

    private sealed class AdministradorFalso(Guid? tenantDelAdministrador) : IAdministradorDelTenantPropietario
    {
        public Task<bool> EsAdministradorEnBaseAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenantDelAdministrador == tenantId);
    }

    private sealed class TenantActualFijo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class SesionPrivilegiadaFalsa(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);
    }
}
