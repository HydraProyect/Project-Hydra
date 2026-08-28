using CaeManager.Application.Blindaje42.Commands.SolicitarCertificacionTgss;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Blindaje42;

public class SolicitarCertificacionTgssCommandHandlerTests
{
    private static readonly DateOnly Hoy = DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly Guid UsuarioId = Guid.NewGuid();

    private sealed class Contexto
    {
        public SolicitudCertificacionTgssRepositorioFalso Repositorio { get; } = new();
        public EmpresasQueryContextFalso EmpresasContext { get; } = new();
        public UnitOfWorkFalso UnitOfWork { get; } = new();

        public SolicitarCertificacionTgssCommandHandler CrearHandler(
            AlcanceDatosServiceFalso? alcance = null, CurrentUserServiceFalso? currentUser = null) =>
            new(Repositorio, EmpresasContext, alcance ?? new AlcanceDatosServiceFalso(),
                currentUser ?? new CurrentUserServiceFalso(UsuarioId), UnitOfWork);
    }

    private static (Guid EmpresaId, Guid ClienteId) RelacionarEmpresaYCliente(
        Contexto contexto, DateTime? vigenciaDesde = null, DateTime? vigenciaHasta = null)
    {
        var empresa = new Empresa("Contratista de prueba", "B12345674");
        var clienteId = Guid.NewGuid();
        contexto.EmpresasContext.ListaEmpresas.Add(empresa);

        var ahora = DateTime.UtcNow;
        var relacion = RelacionEmpresarial.Crear(empresa.Id, clienteId, vigenciaDesde ?? ahora.AddYears(-1));
        if (vigenciaHasta is { } fin) relacion.Cerrar(fin);

        contexto.EmpresasContext.ListaRelacionesEmpresariales.Add(relacion);
        return (empresa.Id, clienteId);
    }

    [Fact]
    public async Task Falla_cuando_la_empresa_y_el_cliente_no_estan_relacionados()
    {
        var contexto = new Contexto();
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(
            new SolicitarCertificacionTgssCommand(Guid.NewGuid(), Guid.NewGuid(), Hoy, null), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("CertificacionTgss.SinRelacion");
        contexto.Repositorio.Solicitudes.Should().BeEmpty();
    }

    [Fact]
    public async Task Falla_cuando_el_cliente_esta_fuera_de_la_cartera()
    {
        var contexto = new Contexto();
        var (empresaId, clienteId) = RelacionarEmpresaYCliente(contexto);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false);
        var handler = contexto.CrearHandler(alcance);

        var resultado = await handler.Handle(
            new SolicitarCertificacionTgssCommand(empresaId, clienteId, Hoy, null), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("CertificacionTgss.ClienteNoEncontrado");
    }

    [Fact]
    public async Task Registra_la_solicitud_cuando_la_relacion_esta_vigente()
    {
        var contexto = new Contexto();
        var (empresaId, clienteId) = RelacionarEmpresaYCliente(contexto);
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(
            new SolicitarCertificacionTgssCommand(empresaId, clienteId, Hoy, "Enviada por burofax."), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        contexto.Repositorio.Solicitudes.Should().ContainSingle(s =>
            s.EmpresaId == empresaId && s.ClienteId == clienteId && s.SolicitadaPorUsuarioId == UsuarioId);
        contexto.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    /// <summary>
    /// La responsabilidad solidaria del art. 42.2 ET dura 3 años DESPUÉS de
    /// terminar el encargo — una solicitud registrada tras cerrarse la
    /// RelacionEmpresarial sigue siendo legítima. No exigir VigenciaHasta ==
    /// null es la decisión de diseño que esto verifica.
    /// </summary>
    [Fact]
    public async Task Registra_la_solicitud_aunque_la_relacion_ya_este_cerrada()
    {
        var contexto = new Contexto();
        var (empresaId, clienteId) = RelacionarEmpresaYCliente(
            contexto, vigenciaDesde: Hoy.ToDateTime(TimeOnly.MinValue).AddYears(-2), vigenciaHasta: Hoy.ToDateTime(TimeOnly.MinValue).AddYears(-1));
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(
            new SolicitarCertificacionTgssCommand(empresaId, clienteId, Hoy, null), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task Falla_cuando_la_relacion_empieza_despues_de_la_fecha_de_solicitud()
    {
        var contexto = new Contexto();
        var (empresaId, clienteId) = RelacionarEmpresaYCliente(
            contexto, vigenciaDesde: Hoy.ToDateTime(TimeOnly.MinValue).AddDays(1));
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(
            new SolicitarCertificacionTgssCommand(empresaId, clienteId, Hoy, null), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("CertificacionTgss.SinRelacion");
    }
}
