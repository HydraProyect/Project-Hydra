using CaeManager.Application.Clientes;
using CaeManager.Application.Clientes.Commands.RestaurarCliente;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Commands.RestaurarEmpresa;
using CaeManager.Application.Subcontratas.Commands.RestaurarSubcontrata;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Importacion;
using CaeManager.Application.Tests.Notificaciones;
using CaeManager.Application.Tests.Operaciones;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using EmpresaRepositorioFalso = CaeManager.Application.Tests.Clientes.EmpresaRepositorioFalso;

namespace CaeManager.Application.Tests.Empresas;

/// <summary>
/// Issue #2118, fuera del candado A-1: los escritores que comprueban la Asignación de Cartera y después esperan el candado
/// de Empresa propia (D-P11) o el compartido de cartera tienen que volver a decidir bajo él, sin el alcance memoizado de
/// antes de la espera. <see cref="AlcanceMemoizadoRevocableFalso"/> responde con la memoización hasta que alguien invalida;
/// el candado en memoria revoca la cartera al tomarse, como haría otra operación confirmada durante la espera.
/// </summary>
public class CarteraRevocadaBajoElCandadoDeEmpresaPropiaTests
{
    public static TheoryData<string, string> Restauraciones => new()
    {
        { "Empresa", "Empresa.NoEncontrada" },
        { "Cliente", "Cliente.NoEncontrado" },
        { "Subcontrata", "Subcontrata.NoEncontrada" },
    };

    [Theory]
    [MemberData(nameof(Restauraciones))]
    public async Task Restaurar_una_empresa_cuya_cartera_se_revoca_mientras_espera_el_candado_no_la_restaura(
        string comando, string codigo)
    {
        var (empresas, eliminada, tenantId) = ConEliminada(comando);
        var alcance = new AlcanceMemoizadoRevocableFalso(EnCartera(eliminada.Id));
        var candado = new LimitesDePrueba.CandadoEnMemoria { AlTomarlo = alcance.Revocar };
        var uow = new UnitOfWorkFalso();

        var resultado = await Restaurar(comando, eliminada.Id, empresas, tenantId, uow, alcance,
            LimitesDePrueba.Sobre(empresas, new AlertaOperativaFalsa(), tenantId, candado));

        candado.Ejecuciones.Should().Be(1);
        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(codigo);
        eliminada.EstaEliminado.Should().BeTrue("fuera de la cartera no se restaura nada");
        uow.VecesGuardado.Should().Be(0);
        alcance.Invalidaciones.Should().BeGreaterThan(0);
    }

    [Theory]
    [MemberData(nameof(Restauraciones))]
    public async Task Restaurar_una_empresa_que_sigue_en_la_cartera_tras_el_candado_la_restaura(string comando, string _)
    {
        var (empresas, eliminada, tenantId) = ConEliminada(comando);
        var alcance = new AlcanceMemoizadoRevocableFalso(EnCartera(eliminada.Id));
        var uow = new UnitOfWorkFalso();

        var resultado = await Restaurar(comando, eliminada.Id, empresas, tenantId, uow, alcance,
            LimitesDePrueba.Sobre(empresas, new AlertaOperativaFalsa(), tenantId));

        resultado.EsExitoso.Should().BeTrue();
        eliminada.EstaEliminado.Should().BeFalse();
        uow.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Crear_una_empresa_cuyo_Cliente_empresarial_sale_de_la_cartera_mientras_espera_el_candado_no_la_crea()
    {
        var empresas = new EmpresasQueryContextFalso();
        var cliente = Empresa.CrearComoCliente("Cliente en cartera, S.A.", "A58818501", false, null, null);
        empresas.ListaEmpresas.Add(cliente);
        var alcance = new AlcanceMemoizadoRevocableFalso(EnCartera(cliente.Id));
        var candado = new LimitesDePrueba.CandadoEnMemoria { AlTomarlo = alcance.Revocar };
        var repo = new EmpresaRepositorioFalso();
        var uow = new UnitOfWorkFalso();

        var resultado = await new CrearEmpresaCommandHandler(repo,
                new EscenarioImportacionCombinada.RelacionesRegistradas { Permitir = true }, empresas, alcance, uow,
                LimitesDePrueba.Sobre(empresas, new AlertaOperativaFalsa(), candado: candado), alcance)
            .Handle(new CrearEmpresaCommand("Nueva identidad", "B12345674", [cliente.Id]), default);

        candado.Ejecuciones.Should().Be(1);
        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Empresa.ClienteNoEncontrado");
        repo.Empresas.Should().BeEmpty();
        uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Crear_una_empresa_con_su_Cliente_empresarial_en_la_cartera_tras_el_candado_la_crea()
    {
        var empresas = new EmpresasQueryContextFalso();
        var cliente = Empresa.CrearComoCliente("Cliente en cartera, S.A.", "A58818501", false, null, null);
        empresas.ListaEmpresas.Add(cliente);
        var alcance = new AlcanceMemoizadoRevocableFalso(EnCartera(cliente.Id));
        var repo = new EmpresaRepositorioFalso();
        var uow = new UnitOfWorkFalso();

        var resultado = await new CrearEmpresaCommandHandler(repo,
                new EscenarioImportacionCombinada.RelacionesRegistradas { Permitir = true }, empresas, alcance, uow,
                LimitesDePrueba.Sobre(empresas, new AlertaOperativaFalsa()), alcance)
            .Handle(new CrearEmpresaCommand("Nueva identidad", "B12345674", [cliente.Id]), default);

        resultado.EsExitoso.Should().BeTrue();
        repo.Empresas.Should().ContainSingle();
    }

    [Fact]
    public async Task Reasignar_el_Gestor_CAE_de_referencia_de_un_Cliente_que_sale_de_la_cartera_mientras_espera_el_candado_no_lo_cambia()
    {
        var gestorAnteriorId = Guid.NewGuid();
        var gestorNuevoId = Guid.NewGuid();
        var cliente = Empresa.CrearComoCliente("Cadena Industrial Iberia", "B12345674", false, null, gestorAnteriorId);
        var repositorio = new EmpresaRepositorioFalso();
        repositorio.Agregar(cliente);
        var alcance = new AlcanceMemoizadoRevocableFalso(EnCartera(cliente.Id));
        var bloqueo = new BloqueoCarteraUsuarioFalso { AlTomarElCompartido = alcance.Revocar };
        var notificaciones = new NotificacionUsuarioRepositorioFalso();

        var resultado = await Reasignador(repositorio, notificaciones, alcance, bloqueo)
            .ReasignarAsync(cliente.Id, gestorNuevoId, default);

        bloqueo.Compartidos.Should().NotBeEmpty();
        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Should().Be(ReasignadorCarteraCliente.ClienteNoEncontrado);
        cliente.EjecutivoUsuarioId.Should().Be(gestorAnteriorId);
        notificaciones.Notificaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Reasignar_el_Gestor_CAE_de_referencia_de_un_Cliente_que_sigue_en_la_cartera_tras_el_candado_lo_cambia()
    {
        var gestorNuevoId = Guid.NewGuid();
        var cliente = Empresa.CrearComoCliente("Cadena Industrial Iberia", "B12345674", false, null, Guid.NewGuid());
        var repositorio = new EmpresaRepositorioFalso();
        repositorio.Agregar(cliente);
        var alcance = new AlcanceMemoizadoRevocableFalso(EnCartera(cliente.Id));

        var resultado = await Reasignador(repositorio, new NotificacionUsuarioRepositorioFalso(), alcance, new BloqueoCarteraUsuarioFalso())
            .ReasignarAsync(cliente.Id, gestorNuevoId, default);

        resultado.EsExitoso.Should().BeTrue();
        cliente.EjecutivoUsuarioId.Should().Be(gestorNuevoId);
    }

    private static ReasignadorCarteraCliente Reasignador(
        EmpresaRepositorioFalso repositorio, NotificacionUsuarioRepositorioFalso notificaciones,
        AlcanceMemoizadoRevocableFalso alcance, BloqueoCarteraUsuarioFalso bloqueo)
    {
        var actorId = Guid.NewGuid();
        return new ReasignadorCarteraCliente(repositorio, new ConfiguracionIaDocumentoClienteRepositorioFalso(), notificaciones,
            new CurrentUserServiceFalso(actorId, "CoordinadorCae"), alcance,
            new DirectorioDestinosCarteraFalso(new DestinoCartera(true, "GestorCae", actorId, false)), bloqueo, alcance);
    }

    /// <summary>La Empresa (en cualquiera de sus papeles) dentro de la Asignación de Cartera, y nada más.</summary>
    private static AlcanceDatosServiceFalso EnCartera(Guid id) => new(
        tieneAccesoTotal: false, clienteIdsVisibles: [id], empresaIdsVisibles: [id], empresaIdsParaGestion: [id],
        subcontrataIdsVisibles: [id], subcontrataIdsParaGestion: [id]);

    private static (EmpresasQueryContextFalso Empresas, Empresa Eliminada, Guid TenantId) ConEliminada(string comando)
    {
        var tenantId = Guid.NewGuid();
        var empresa = comando switch
        {
            "Cliente" => Empresa.CrearComoCliente("Restaurable, S.A.", "B12345674", false, null, null),
            "Subcontrata" => Empresa.CrearComoSubcontrata("Restaurable, S.L.", "B12345674", "Básico"),
            _ => new Empresa("Restaurable, S.L.", "B12345674"),
        };
        typeof(Empresa).GetProperty(nameof(Empresa.EsPropia))!.SetValue(empresa, false);
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(empresa, tenantId);
        empresa.MarcarComoEliminado(Guid.NewGuid());
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(empresa);
        return (empresas, empresa, tenantId);
    }

    private static async Task<Result> Restaurar(string comando, Guid id, EmpresasQueryContextFalso empresas, Guid tenantId,
        UnitOfWorkFalso uow, AlcanceMemoizadoRevocableFalso alcance, ILimiteEmpresaPropia limite)
    {
        var tenant = new LimitesDePrueba.TenantActualFijo(tenantId);
        return comando switch
        {
            "Empresa" => await new RestaurarEmpresaCommandHandler(empresas, tenant, alcance, uow, limite, alcance)
                .Handle(new RestaurarEmpresaCommand(id), default),
            "Cliente" => await new RestaurarClienteCommandHandler(empresas, tenant, alcance, uow, limite, alcance)
                .Handle(new RestaurarClienteCommand(id), default),
            "Subcontrata" => await new RestaurarSubcontrataCommandHandler(empresas, tenant, alcance, uow, limite, alcance)
                .Handle(new RestaurarSubcontrataCommand(id), default),
            _ => throw new ArgumentOutOfRangeException(nameof(comando)),
        };
    }
}
