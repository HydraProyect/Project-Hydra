using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Plataforma.Commands.ConcederPrivilegio;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Plataforma;
using FluentAssertions;

namespace CaeManager.Application.Tests.Plataforma.ConcederPrivilegio;

/// <summary>Doble mínimo de <see cref="IPlataformaWriter"/>: solo captura lo que se le añade.</summary>
public class PlataformaWriterFalso : IPlataformaWriter
{
    public SesionPrivilegiada? SesionAnadida { get; private set; }
    public ConcesionPrivilegio? ConcesionAnadida { get; private set; }

    public void AnadirSesion(SesionPrivilegiada sesion) => SesionAnadida = sesion;
    public void AnadirConcesion(ConcesionPrivilegio concesion) => ConcesionAnadida = concesion;
}

/// <summary>Directorio que solo sabe el Tenant de cada cuenta; null si no la ve.</summary>
public class DirectorioTenantDeCuenta(IReadOnlyDictionary<Guid, Guid> tenantPorCuenta) : IDirectorioUsuariosService
{
    public Task<bool> EsVisibleEnTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult<Guid?>(tenantPorCuenta.TryGetValue(usuarioId, out var tenant) ? tenant : null);

    public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(
        IReadOnlyCollection<Guid> usuarioIds, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<bool> EsCuentaActivaConRolAsync(
        Guid usuarioId, Guid tenantId, string rol, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

public class ConcederPrivilegioCommandHandlerTests
{
    private static readonly Guid Concedente = Guid.NewGuid();
    private static readonly Guid Beneficiario = Guid.NewGuid();
    private static readonly Guid TenantObjetivo = Guid.NewGuid();
    private static readonly Guid TenantDelConcedente = Guid.NewGuid();

    private static ConcederPrivilegioCommand Comando(
        Guid? beneficiario = null, Guid? tenant = null,
        CapacidadPrivilegio capacidad = CapacidadPrivilegio.Aprovisionamiento) =>
        new(beneficiario ?? Beneficiario, tenant ?? TenantObjetivo, DiasDeVigencia: 30,
            Motivo: "Aprovisionamiento inicial de Refrielectric", Capacidad: capacidad);

    private static ConcederPrivilegioCommandHandler Handler(
        out PlataformaWriterFalso writer,
        out UnitOfWorkFalso unitOfWork,
        bool dobleFactor = true,
        AutorizacionAdminPlataformaFalsa? autorizacion = null,
        Guid? tenantOrigenConcedente = null,
        IReadOnlyDictionary<Guid, Guid>? tenantPorCuenta = null)
    {
        writer = new PlataformaWriterFalso();
        unitOfWork = new UnitOfWorkFalso();
        var currentUser = new CurrentUserServiceFalso(
            usuarioId: Concedente, tenantOrigenId: tenantOrigenConcedente ?? TenantDelConcedente,
            tieneDobleFactorActivo: dobleFactor);
        // Por defecto el beneficiario es de la casa del concedente.
        var directorio = new DirectorioTenantDeCuenta(
            tenantPorCuenta ?? new Dictionary<Guid, Guid> { [Beneficiario] = TenantDelConcedente });

        return new ConcederPrivilegioCommandHandler(
            writer, autorizacion ?? AutorizacionAdminPlataformaFalsa.AcotadaA(TenantObjetivo), currentUser,
            directorio, unitOfWork);
    }

    [Fact]
    public async Task Rechaza_RestablecimientoSegundoFactor_a_una_cuenta_de_otro_Tenant()
    {
        var handler = Handler(out var writer, out var unitOfWork,
            tenantPorCuenta: new Dictionary<Guid, Guid> { [Beneficiario] = TenantObjetivo });

        var resultado = await handler.Handle(
            Comando(capacidad: CapacidadPrivilegio.RestablecimientoSegundoFactor), CancellationToken.None);

        resultado.Error!.Codigo.Should().Be("ConcesionPrivilegio.BeneficiarioNoEsDePlataforma");
        writer.ConcesionAnadida.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Rechaza_RestablecimientoSegundoFactor_a_una_cuenta_que_no_se_ve()
    {
        var handler = Handler(out var writer, out _, tenantPorCuenta: new Dictionary<Guid, Guid>());

        var resultado = await handler.Handle(
            Comando(capacidad: CapacidadPrivilegio.RestablecimientoSegundoFactor), CancellationToken.None);

        resultado.Error!.Codigo.Should().Be("ConcesionPrivilegio.BeneficiarioNoEsDePlataforma");
        writer.ConcesionAnadida.Should().BeNull();
    }

    [Fact]
    public async Task Concede_Aprovisionamiento_cuando_el_concedente_tiene_AdminPlataforma_sobre_el_tenant()
    {
        var handler = Handler(out var writer, out var unitOfWork);

        var resultado = await handler.Handle(Comando(), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        writer.ConcesionAnadida.Should().NotBeNull();
        writer.ConcesionAnadida!.UsuarioPlataformaId.Should().Be(Beneficiario);
        writer.ConcesionAnadida.Capacidad.Should().Be(CapacidadPrivilegio.Aprovisionamiento);
        writer.ConcesionAnadida.ConcedidaPorUsuarioId.Should().Be(Concedente);
        writer.ConcesionAnadida.EsAlcanceGlobal.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Concede_RestablecimientoSegundoFactor_acotado_al_tenant_objetivo()
    {
        var handler = Handler(out var writer, out _);

        var resultado = await handler.Handle(
            Comando(capacidad: CapacidadPrivilegio.RestablecimientoSegundoFactor), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        writer.ConcesionAnadida!.Capacidad.Should().Be(CapacidadPrivilegio.RestablecimientoSegundoFactor,
            "la capacidad concedida es la pedida, no Aprovisionamiento fijo");
        writer.ConcesionAnadida.EsAlcanceGlobal.Should().BeFalse();
        writer.ConcesionAnadida.CubreEn(TenantObjetivo, DateTime.UtcNow).Should().BeTrue();
        writer.ConcesionAnadida.ConcedidaPorUsuarioId.Should().Be(Concedente);
    }

    [Fact]
    public async Task Rechaza_si_el_beneficiario_es_el_propio_concedente()
    {
        var handler = Handler(out var writer, out _);

        var resultado = await handler.Handle(Comando(beneficiario: Concedente), CancellationToken.None);

        resultado.EsExitoso.Should().BeFalse();
        resultado.Error!.Codigo.Should().Be("ConcesionPrivilegio.BeneficiarioEsConcedente");
        writer.ConcesionAnadida.Should().BeNull();
    }

    [Fact]
    public async Task Rechaza_sin_doble_factor()
    {
        var handler = Handler(out var writer, out _, dobleFactor: false);

        var resultado = await handler.Handle(Comando(), CancellationToken.None);

        resultado.EsExitoso.Should().BeFalse();
        resultado.Error!.Codigo.Should().Be("ConcesionPrivilegio.SinDobleFactor");
        writer.ConcesionAnadida.Should().BeNull();
    }

    [Fact]
    public async Task Rechaza_si_el_concedente_no_tiene_AdminPlataforma_sobre_ese_tenant()
    {
        var handler = Handler(out var writer, out _, autorizacion: AutorizacionAdminPlataformaFalsa.SinNada());

        var resultado = await handler.Handle(Comando(), CancellationToken.None);

        resultado.EsExitoso.Should().BeFalse();
        resultado.Error!.Codigo.Should().Be("ConcesionPrivilegio.NoAutorizado");
        writer.ConcesionAnadida.Should().BeNull();
    }

    [Fact]
    public async Task Rechaza_si_el_concedente_solo_tiene_AdminPlataforma_sobre_otro_tenant()
    {
        var otroTenant = Guid.NewGuid();
        var handler = Handler(out var writer, out _, autorizacion: AutorizacionAdminPlataformaFalsa.AcotadaA(otroTenant));

        var resultado = await handler.Handle(Comando(), CancellationToken.None);

        resultado.EsExitoso.Should().BeFalse();
        resultado.Error!.Codigo.Should().Be("ConcesionPrivilegio.NoAutorizado");
        writer.ConcesionAnadida.Should().BeNull();
    }

    [Fact]
    public async Task Rechaza_si_el_tenant_objetivo_es_el_del_propio_concedente()
    {
        var handler = Handler(
            out var writer, out _,
            autorizacion: AutorizacionAdminPlataformaFalsa.AcotadaA(TenantDelConcedente),
            tenantOrigenConcedente: TenantDelConcedente);

        var resultado = await handler.Handle(Comando(tenant: TenantDelConcedente), CancellationToken.None);

        resultado.EsExitoso.Should().BeFalse();
        resultado.Error!.Codigo.Should().Be("ConcesionPrivilegio.TenantPropio");
        writer.ConcesionAnadida.Should().BeNull();
    }

    [Fact]
    public async Task Concesion_global_del_concedente_tambien_autoriza()
    {
        var handler = Handler(out var writer, out _, autorizacion: AutorizacionAdminPlataformaFalsa.Global());

        var resultado = await handler.Handle(Comando(), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        writer.ConcesionAnadida.Should().NotBeNull();
    }
}
