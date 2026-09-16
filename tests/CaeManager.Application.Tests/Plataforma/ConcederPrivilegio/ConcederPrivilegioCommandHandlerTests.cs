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

public class ConcederPrivilegioCommandHandlerTests
{
    private static readonly Guid Concedente = Guid.NewGuid();
    private static readonly Guid Beneficiario = Guid.NewGuid();
    private static readonly Guid TenantObjetivo = Guid.NewGuid();
    private static readonly Guid TenantDelConcedente = Guid.NewGuid();

    private static ConcederPrivilegioCommand Comando(Guid? beneficiario = null, Guid? tenant = null) =>
        new(beneficiario ?? Beneficiario, tenant ?? TenantObjetivo, DiasDeVigencia: 30, Motivo: "Aprovisionamiento inicial de Refrielectric");

    private static ConcederPrivilegioCommandHandler Handler(
        out PlataformaWriterFalso writer,
        out UnitOfWorkFalso unitOfWork,
        bool dobleFactor = true,
        AutorizacionAdminPlataformaFalsa? autorizacion = null,
        Guid? tenantOrigenConcedente = null)
    {
        writer = new PlataformaWriterFalso();
        unitOfWork = new UnitOfWorkFalso();
        var currentUser = new CurrentUserServiceFalso(
            usuarioId: Concedente, tenantOrigenId: tenantOrigenConcedente ?? TenantDelConcedente,
            tieneDobleFactorActivo: dobleFactor);

        return new ConcederPrivilegioCommandHandler(
            writer, autorizacion ?? AutorizacionAdminPlataformaFalsa.AcotadaA(TenantObjetivo), currentUser, unitOfWork);
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
