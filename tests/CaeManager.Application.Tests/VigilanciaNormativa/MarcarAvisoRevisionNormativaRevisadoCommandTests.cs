using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plataforma;
using CaeManager.Application.VigilanciaNormativa.Commands.MarcarAvisoRevisionNormativaRevisado;
using CaeManager.Domain.VigilanciaNormativa;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.VigilanciaNormativa;

/// <summary>
/// H-3/DEC-8: la lectura del aviso es difundida a toda la jerarquía del
/// tenant, pero <b>resolverlo</b> —decidir si toca al catálogo de
/// formatos— sigue siendo exclusivo del Actor de Plataforma TALVEG. Esta es
/// la mitad de autorización de la propiedad "ningún tenant puede escribir
/// el catálogo global": vive aquí, no en el esquema de la tabla (que no
/// tiene TenantId por diseño, pero eso es alcance, no protección).
/// </summary>
public class MarcarAvisoRevisionNormativaRevisadoCommandTests
{
    private static AvisoRevisionNormativa CrearAviso() => new(
        "BOE-A-2026-17626", new DateOnly(2026, 8, 13),
        "Real Decreto 171/2004, de coordinación de actividades empresariales.",
        "https://www.boe.es/diario_boe/txt.php?id=BOE-A-2026-17626", "RD 171/2004", DateTime.UtcNow);

    [Fact]
    public async Task Sin_AdminPlataforma_global_no_se_puede_revisar_el_aviso()
    {
        var repositorio = new AvisoRevisionNormativaRepositorioFalso();
        var aviso = CrearAviso();
        repositorio.Agregar(aviso);
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new MarcarAvisoRevisionNormativaRevisadoCommandHandler(
            repositorio, AutorizacionAdminPlataformaFalsa.SinNada(),
            new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", Guid.NewGuid()), unitOfWork);

        var resultado = await handler.Handle(
            new MarcarAvisoRevisionNormativaRevisadoCommand(aviso.Id, null), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("AvisoRevisionNormativa.NoAutorizado");
        aviso.Revisado.Should().BeFalse("el gate debe negar ANTES de tocar el agregado");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Prueba de sensibilidad: una concesión ACOTADA a un tenant tampoco
    /// vale. Si el handler consultara PuedeSobreTenantAsync en vez de
    /// PuedeGlobalmenteAsync, este test seguiría en verde con un booleano
    /// simple — por eso se afirma también qué método se consultó.
    /// </summary>
    [Fact]
    public async Task Una_concesion_acotada_a_un_tenant_tampoco_autoriza_revisar_el_catalogo_global()
    {
        var repositorio = new AvisoRevisionNormativaRepositorioFalso();
        var aviso = CrearAviso();
        repositorio.Agregar(aviso);
        var autorizacion = AutorizacionAdminPlataformaFalsa.AcotadaA(Guid.NewGuid());

        var handler = new MarcarAvisoRevisionNormativaRevisadoCommandHandler(
            repositorio, autorizacion,
            new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", Guid.NewGuid()), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new MarcarAvisoRevisionNormativaRevisadoCommand(aviso.Id, null), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("AvisoRevisionNormativa.NoAutorizado");
        autorizacion.SeConsultoLoGlobal.Should().BeTrue(
            "el catálogo no es de ningún tenant: la única pregunta válida es la global, nunca PuedeSobreTenantAsync");
        aviso.Revisado.Should().BeFalse();
    }

    [Fact]
    public async Task Con_AdminPlataforma_global_se_revisa_el_aviso_y_se_guarda()
    {
        var repositorio = new AvisoRevisionNormativaRepositorioFalso();
        var aviso = CrearAviso();
        repositorio.Agregar(aviso);
        var usuarioId = Guid.NewGuid();
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new MarcarAvisoRevisionNormativaRevisadoCommandHandler(
            repositorio, AutorizacionAdminPlataformaFalsa.Global(),
            new CurrentUserServiceFalso(usuarioId, "Administrador", Guid.NewGuid()), unitOfWork);

        var resultado = await handler.Handle(
            new MarcarAvisoRevisionNormativaRevisadoCommand(aviso.Id, "No afecta al catálogo."), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        aviso.Revisado.Should().BeTrue();
        aviso.RevisadoPorUsuarioId.Should().Be(usuarioId);
        aviso.NotasRevision.Should().Be("No afecta al catálogo.");
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Aviso_inexistente_falla_incluso_con_autorizacion_global()
    {
        var repositorio = new AvisoRevisionNormativaRepositorioFalso();

        var handler = new MarcarAvisoRevisionNormativaRevisadoCommandHandler(
            repositorio, AutorizacionAdminPlataformaFalsa.Global(),
            new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", Guid.NewGuid()), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new MarcarAvisoRevisionNormativaRevisadoCommand(Guid.NewGuid(), null), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("AvisoRevisionNormativa.NoEncontrado");
    }
}
