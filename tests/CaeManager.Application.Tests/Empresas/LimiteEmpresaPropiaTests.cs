using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Clientes.Commands.RestaurarCliente;
using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Empresas;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Commands.RestaurarEmpresa;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacion;
using CaeManager.Application.Subcontratas.Commands.CrearSubcontrata;
using CaeManager.Application.Subcontratas.Commands.RestaurarSubcontrata;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Importacion;
using CaeManager.Application.Tests.Operaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using EmpresaRepositorioFalso = CaeManager.Application.Tests.Clientes.EmpresaRepositorioFalso;

namespace CaeManager.Application.Tests.Empresas;

/// <summary>
/// Decisión de producto D-P11 (2026-10-30, ajustada el 2026-11-12): todo Tenant propietario, con el Producto
/// Contratado P o con O (Consultora), tiene una sola Empresa propia. El límite no lee el Producto Contratado, así
/// que un Tenant O queda rechazado igual que uno P. Un test por escritor de la tabla de inventario: alta unitaria,
/// las tres importaciones (la maestra vive en <c>EjecutarImportacionMaestraTests</c>, cuyo arnés es privado) y las
/// tres restauraciones.
/// </summary>
public class LimiteEmpresaPropiaTests
{
    private const string CodigoLimite = "Empresa.LimiteEmpresaPropia";

    private static Empresa PropiaActiva() => new("Organización propia, S.L.", "A58818501");

    // ---------------------------------------------------------------- CrearEmpresaCommand

    [Fact]
    public async Task CrearEmpresa_con_una_Empresa_propia_activa_se_rechaza_sin_guardar_y_avisa_a_Soporte_TALVEG()
    {
        var tenantId = Guid.NewGuid();
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(ConTenant(PropiaActiva(), tenantId));
        var alertas = new AlertaOperativaFalsa();
        var repo = new EmpresaRepositorioFalso();
        var uow = new UnitOfWorkFalso();

        var resultado = await CrearEmpresa(repo, empresas, uow, LimitesDePrueba.Sobre(empresas, alertas, tenantId));

        resultado.EsExitoso.Should().BeFalse();
        resultado.Error.Codigo.Should().Be(CodigoLimite);
        // Sin Producto Contratado ni ampliación de grupo en el texto: el Tenant TEC participante no tiene ninguno de
        // los dos (EP-1), y la ampliación todavía no existe.
        resultado.Error.Mensaje.Should().Contain("Empresa propia").And.Contain("solo puede tener una")
            .And.NotContain("Producto Contratado").And.NotContain("ampliación");
        uow.VecesGuardado.Should().Be(0);
        repo.Empresas.Should().BeEmpty();
        var aviso = alertas.Alertas.Should().ContainSingle().Which;
        aviso.Nivel.Should().Be(NivelAlertaOperativa.Aviso);
        aviso.Mensaje.Should().Contain(tenantId.ToString())
            .And.NotContain("Nueva identidad", "el aviso no lleva la razón social intentada")
            .And.NotContain("B12345674", "ni el CIF intentado");
    }

    /// <summary>
    /// El recuento es del Tenant actual aunque el contexto devuelva filas de otro: el doble no filtra por Tenant,
    /// así que este test cae si el límite deja de escribir la condición de Tenant y se fía solo del filtro global.
    /// </summary>
    [Fact]
    public async Task CrearEmpresa_permitida_cuando_la_Empresa_propia_activa_es_de_otro_Tenant()
    {
        var tenantId = Guid.NewGuid();
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(ConTenant(PropiaActiva(), Guid.NewGuid()));
        var alertas = new AlertaOperativaFalsa();
        var repo = new EmpresaRepositorioFalso();
        var uow = new UnitOfWorkFalso();

        var resultado = await CrearEmpresa(repo, empresas, uow, LimitesDePrueba.Sobre(empresas, alertas, tenantId));

        resultado.EsExitoso.Should().BeTrue();
        repo.Empresas.Should().ContainSingle().Which.EsPropia.Should().BeTrue();
        alertas.Alertas.Should().BeEmpty();
    }

    /// <summary>
    /// EP-1 (2026-11-12): el Tenant TEC participante (Inbound, gratuito, sin Producto Contratado P) también tiene una
    /// sola Empresa propia, y para él no hay ampliación de grupo. El límite no lee el Producto Contratado: un Tenant
    /// sin ninguno, como el de este test, queda rechazado igual que uno P u O.
    /// </summary>
    [Fact]
    public async Task CrearEmpresa_en_un_Tenant_sin_Producto_Contratado_TEC_participante_rechaza_la_segunda_Empresa_propia()
    {
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(PropiaActiva());
        var repo = new EmpresaRepositorioFalso();
        var uow = new UnitOfWorkFalso();

        var resultado = await CrearEmpresa(repo, empresas, uow, LimitesDePrueba.Sobre(empresas, new AlertaOperativaFalsa()));

        resultado.Error.Codigo.Should().Be(CodigoLimite);
        repo.Empresas.Should().BeEmpty();
    }
    public static TheoryData<string> CasosPermitidos => new()
    {
        "primera Empresa propia (alta autónoma)",
        "solo Empresas contraparte",
        "la Empresa propia en la Papelera",
    };

    [Theory]
    [MemberData(nameof(CasosPermitidos))]
    public async Task CrearEmpresa_permitida_sin_otra_Empresa_propia_activa(string caso)
    {
        var empresas = new EmpresasQueryContextFalso();
        switch (caso)
        {
            case "solo Empresas contraparte":
                empresas.ListaEmpresas.Add(Empresa.CrearComoCliente("Cliente empresarial, S.A.", "B12345674", false, null, null));
                empresas.ListaEmpresas.Add(Empresa.CrearComoSubcontrata("Subcontrata, S.L.", null, "Básico"));
                break;
            case "la Empresa propia en la Papelera":
                var eliminada = PropiaActiva();
                eliminada.MarcarComoEliminado(Guid.NewGuid());
                empresas.ListaEmpresas.Add(eliminada);
                break;
        }
        var alertas = new AlertaOperativaFalsa();
        var repo = new EmpresaRepositorioFalso();
        var uow = new UnitOfWorkFalso();

        var resultado = await CrearEmpresa(repo, empresas, uow, LimitesDePrueba.Sobre(empresas, alertas));

        resultado.EsExitoso.Should().BeTrue(caso);
        uow.VecesGuardado.Should().Be(1);
        repo.Empresas.Should().ContainSingle().Which.EsPropia.Should().BeTrue();
        alertas.Alertas.Should().BeEmpty();
    }

    [Fact]
    public async Task Las_contrapartes_se_siguen_creando_con_la_Empresa_propia_ya_dada_de_alta()
    {
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(PropiaActiva());
        var repo = new EmpresaRepositorioFalso();
        var uow = new UnitOfWorkFalso();

        var cliente = await new CrearClienteCommandHandler(repo, uow, new CurrentUserServiceFalso(),
                new TransaccionDeComandoFalsa(), new BloqueoCarteraUsuarioFalso(),
                new DirectorioDestinosCarteraFalso(new DestinoCartera(true, "GestorCae", null, false)))
            .Handle(new CrearClienteCommand("Cliente empresarial, S.A.", "B12345674", false, null), default);
        var subcontrata = await new CrearSubcontrataCommandHandler(repo, new EscenarioImportacionCombinada.RelacionesRegistradas { Permitir = true },
                empresas, new AlcanceDatosServiceFalso(), uow)
            .Handle(new CrearSubcontrataCommand("Subcontrata, S.L.", "P1234567D", [], []), default);

        cliente.EsExitoso.Should().BeTrue();
        subcontrata.EsExitoso.Should().BeTrue();
        repo.Empresas.Should().HaveCount(2).And.OnlyContain(e => !e.EsPropia);
    }

    private static Task<Result<Guid>> CrearEmpresa(
        EmpresaRepositorioFalso repo, EmpresasQueryContextFalso empresas, UnitOfWorkFalso uow, ILimiteEmpresaPropia limite) =>
        new CrearEmpresaCommandHandler(repo, new EscenarioImportacionCombinada.RelacionesRegistradas { Permitir = true },
                empresas, new AlcanceDatosServiceFalso(), uow, limite)
            .Handle(new CrearEmpresaCommand("Nueva identidad", "B12345674", []), default);

    // ---------------------------------------------------------------- EjecutarImportacionCommand

    [Fact]
    public async Task EjecutarImportacion_crea_la_primera_Empresa_propia_y_omite_las_demas_con_un_solo_aviso()
    {
        var empresas = new EmpresasQueryContextFalso();
        var alertas = new AlertaOperativaFalsa();
        var repo = new EmpresaRepositorioFalso();
        var plan = PlanSimple(new EmpresaImportadaDto("Primera propia, S.L.", false, Fila: 2),
            new EmpresaImportadaDto("Segunda propia, S.L.", false, Fila: 3),
            new EmpresaImportadaDto("Tercera propia, S.L.", false, Fila: 4));

        var resultado = await ImportacionSimple(repo, empresas, LimitesDePrueba.Sobre(empresas, alertas)).Handle(
            new EjecutarImportacionCommand(plan), default);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.EmpresasCreadas.Should().Be(1);
        resultado.Valor.Omitidos.Should().HaveCount(2);
        repo.Empresas.Should().ContainSingle().Which.RazonSocial.Should().Be("Primera propia, S.L.");
        alertas.Alertas.Should().ContainSingle("un aviso por comando, no por fila");
    }

    private static PlanImportacionDto PlanSimple(params EmpresaImportadaDto[] filas) =>
        new(Guid.NewGuid(), [], filas, [], [], [], [], []);

    private static EjecutarImportacionCommandHandler ImportacionSimple(
        EmpresaRepositorioFalso repo, EmpresasQueryContextFalso empresas, ILimiteEmpresaPropia limite) =>
        new(repo, new CaeManager.Application.Tests.Trabajadores.TrabajadorRepositorioFalso(), new DocumentoRepositorioFalso(),
            new CaeManager.Application.Tests.Asignaciones.AsignacionRepositorioFalso(), new OperacionImportacionRepositorioFalso(),
            new CaeManager.Application.Tests.Reportes.AsignacionesQueryContextFalso(), new CaeManager.Application.Tests.Plantillas.CentrosQueryContextFalso(),
            new DocumentosQueryContextFalso(), empresas, new CaeManager.Application.Tests.TiposDocumento.TiposDocumentoQueryContextFalso(),
            new CaeManager.Application.Tests.Plantillas.TrabajadoresQueryContextFalso(), new AdministracionTotal(),
            new AltaAcreditacionesPlataformaServiceFalso(), new AlcanceDatosServiceFalso(), new SerializacionAltasAcreditacionEmpresaFalsa(), limite);

    private sealed class AdministracionTotal : IAutorizacionEscrituraEfectiva
    {
        public Task<bool> EsActoDeAdministradorAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<AutoridadCargaMasiva> ObtenerAutoridadCargaMasivaAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AutoridadCargaMasiva.Total);
    }

    // ---------------------------------------------------------------- EjecutarImportacionCombinadaCommand

    [Fact]
    public async Task EjecutarImportacionCombinada_con_la_Empresa_propia_omite_la_fila_nueva_y_avisa()
    {
        var escenario = new EscenarioImportacionCombinada();
        var propias = new EmpresasQueryContextFalso();
        propias.ListaEmpresas.Add(PropiaActiva());
        var alertas = new AlertaOperativaFalsa();
        escenario.Limite = LimitesDePrueba.Sobre(propias, alertas);

        var resultado = await escenario.EjecutarAsync(EscenarioImportacionCombinada.Plan(
            empresas: [new EmpresaCombinadaImportadaDto("Otra propia, S.L.", [], false, 2)]));

        resultado.EmpresasCreadas.Should().Be(0);
        resultado.Omitidos.Should().ContainSingle();
        escenario.EmpresaRepositorio.Empresas.Should().BeEmpty();
        alertas.Alertas.Should().ContainSingle();
    }

    [Fact]
    public async Task EjecutarImportacionCombinada_crea_la_primera_Empresa_propia_y_omite_las_demas_con_un_solo_aviso()
    {
        var escenario = new EscenarioImportacionCombinada();
        var alertas = new AlertaOperativaFalsa();
        escenario.Limite = LimitesDePrueba.Sobre(new EmpresasQueryContextFalso(), alertas);

        var resultado = await escenario.EjecutarAsync(EscenarioImportacionCombinada.Plan(empresas:
        [
            new EmpresaCombinadaImportadaDto("Primera propia, S.L.", [], false, 2),
            new EmpresaCombinadaImportadaDto("Segunda propia, S.L.", [], false, 3),
            new EmpresaCombinadaImportadaDto("Tercera propia, S.L.", [], false, 4),
        ]));

        resultado.EmpresasCreadas.Should().Be(1, "las filas sin guardar de la misma importación también cuentan");
        resultado.Omitidos.Should().HaveCount(2);
        escenario.EmpresaRepositorio.Empresas.Should().ContainSingle().Which.RazonSocial.Should().Be("Primera propia, S.L.");
        alertas.Alertas.Should().ContainSingle("un aviso por comando, no por fila");
    }

    [Fact]
    public async Task EjecutarImportacionCombinada_sin_Empresa_propia_crea_la_fila_nueva()
    {
        var escenario = new EscenarioImportacionCombinada();

        var resultado = await escenario.EjecutarAsync(EscenarioImportacionCombinada.Plan(
            empresas: [new EmpresaCombinadaImportadaDto("Otra propia, S.L.", [], false, 2)]));

        resultado.EmpresasCreadas.Should().Be(1);
    }

    // ---------------------------------------------------------------- Restaurar*Command

    public static TheoryData<string> Restauraciones => new() { "Empresa", "Cliente", "Subcontrata" };

    [Theory]
    [MemberData(nameof(Restauraciones))]
    public async Task Restaurar_una_Empresa_propia_con_otra_activa_se_rechaza(string comando)
    {
        var tenantId = Guid.NewGuid();
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(ConTenant(PropiaActiva(), tenantId));
        var eliminada = ConTenant(EliminadaParaRestaurar(comando, esPropia: true), tenantId);
        empresas.ListaEmpresas.Add(eliminada);
        var alertas = new AlertaOperativaFalsa();
        var uow = new UnitOfWorkFalso();

        var resultado = await Restaurar(comando, eliminada.Id, empresas, tenantId, uow,
            LimitesDePrueba.Sobre(empresas, alertas, tenantId));

        resultado.EsExitoso.Should().BeFalse();
        resultado.Error.Codigo.Should().Be(CodigoLimite);
        eliminada.EstaEliminado.Should().BeTrue();
        uow.VecesGuardado.Should().Be(0);
        alertas.Alertas.Should().ContainSingle();
    }

    [Theory]
    // Una contraparte con la Empresa propia activa.
    [InlineData("Empresa", false, true)]
    [InlineData("Cliente", false, true)]
    [InlineData("Subcontrata", false, true)]
    // La Empresa propia cuando no hay otra activa: la de la Papelera no se cuenta a sí misma.
    [InlineData("Empresa", true, false)]
    [InlineData("Cliente", true, false)]
    [InlineData("Subcontrata", true, false)]
    public async Task Restaurar_permitida_sin_superar_el_limite(string comando, bool esPropia, bool otraPropiaActiva)
    {
        var tenantId = Guid.NewGuid();
        var empresas = new EmpresasQueryContextFalso();
        if (otraPropiaActiva)
            empresas.ListaEmpresas.Add(ConTenant(PropiaActiva(), tenantId));
        var eliminada = ConTenant(EliminadaParaRestaurar(comando, esPropia), tenantId);
        empresas.ListaEmpresas.Add(eliminada);
        var alertas = new AlertaOperativaFalsa();
        var uow = new UnitOfWorkFalso();

        var resultado = await Restaurar(comando, eliminada.Id, empresas, tenantId, uow,
            LimitesDePrueba.Sobre(empresas, alertas, tenantId));

        resultado.EsExitoso.Should().BeTrue();
        eliminada.EstaEliminado.Should().BeFalse();
        alertas.Alertas.Should().BeEmpty();
    }

    private static Empresa EliminadaParaRestaurar(string comando, bool esPropia)
    {
        var empresa = comando switch
        {
            "Cliente" => Empresa.CrearComoCliente("Restaurable, S.A.", "B12345674", false, null, null),
            "Subcontrata" => Empresa.CrearComoSubcontrata("Restaurable, S.L.", "B12345674", "Básico"),
            _ => new Empresa("Restaurable, S.L.", "B12345674"),
        };
        typeof(Empresa).GetProperty(nameof(Empresa.EsPropia))!.SetValue(empresa, esPropia);
        empresa.MarcarComoEliminado(Guid.NewGuid());
        return empresa;
    }

    private static Empresa ConTenant(Empresa empresa, Guid tenantId)
    {
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(empresa, tenantId);
        return empresa;
    }

    private static async Task<Result> Restaurar(string comando, Guid id, EmpresasQueryContextFalso empresas, Guid tenantId,
        UnitOfWorkFalso uow, ILimiteEmpresaPropia limite)
    {
        var tenant = new LimitesDePrueba.TenantActualFijo(tenantId);
        var alcance = new AlcanceDatosServiceFalso();
        return comando switch
        {
            "Empresa" => await new RestaurarEmpresaCommandHandler(empresas, tenant, alcance, uow, limite)
                .Handle(new RestaurarEmpresaCommand(id), default),
            "Cliente" => await new RestaurarClienteCommandHandler(empresas, tenant, alcance, uow, limite)
                .Handle(new RestaurarClienteCommand(id), default),
            "Subcontrata" => await new RestaurarSubcontrataCommandHandler(empresas, tenant, alcance, uow, limite)
                .Handle(new RestaurarSubcontrataCommand(id), default),
            _ => throw new ArgumentOutOfRangeException(nameof(comando)),
        };
    }

    // ---------------------------------------------------------------- Registro

    [Fact]
    public void AddApplication_registra_el_limite_por_ambito()
    {
        var servicios = new ServiceCollection();
        servicios.AddApplication();

        servicios.Should().Contain(d => d.ServiceType == typeof(ILimiteEmpresaPropia)
                                        && d.ImplementationType == typeof(LimiteEmpresaPropia)
                                        && d.Lifetime == ServiceLifetime.Scoped);
    }
}
