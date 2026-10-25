using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Clientes.Commands.EditarCliente;
using CaeManager.Application.Common;
using CaeManager.Application.Clientes;
using CaeManager.Application.Empresas;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Commands.EditarEmpresa;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacionCombinada;
using CaeManager.Application.Subcontratas.Commands.CrearSubcontrata;
using CaeManager.Application.Subcontratas.Commands.EditarSubcontrata;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Operaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Application.Tests.Empresas;

public class DuplicadosCifEmpresaTests
{
    public static TheoryData<string> Comandos => new()
    {
        "CrearCliente", "EditarCliente", "CrearEmpresa", "EditarEmpresa", "CrearSubcontrata", "EditarSubcontrata"
    };

    [Theory]
    [MemberData(nameof(Comandos))]
    public async Task Cif_de_baja_se_rechaza_con_instruccion_de_restaurar(string comando)
    {
        var (repo, propia) = Preparar();
        var baja = new Empresa("Empresa dada de baja", "B12345674");
        baja.MarcarComoEliminado(Guid.NewGuid());
        repo.Agregar(baja);
        var uow = new UnitOfWorkFalso();

        var resultado = await Ejecutar(comando, repo, propia, uow, " b12345674 ");

        resultado.Error.Codigo.Should().Be("Empresa.CifDeBaja");
        resultado.Error.Mensaje.Should().Contain("Restáurala");
        baja.EstaEliminado.Should().BeTrue();
        uow.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Comandos))]
    public async Task Mayusculas_y_espacios_se_comparan_como_el_valor_que_se_persistira(string comando)
    {
        var (repo, propia) = Preparar();
        repo.Agregar(new Empresa("Otra empresa", "B12345674"));
        var uow = new UnitOfWorkFalso();

        var resultado = await Ejecutar(comando, repo, propia, uow, " b12345674 ");

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().EndWith(".CifDuplicado");
        uow.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Comandos))]
    public async Task Cif_de_otro_Tenant_propietario_no_cuenta(string comando)
    {
        var (repo, propia) = Preparar();
        var ajena = new Empresa("Empresa ajena", "B12345674");
        typeof(EntidadConTenant).GetProperty(nameof(Empresa.TenantId))!.SetValue(ajena, Guid.NewGuid());
        ajena.MarcarComoEliminado(Guid.NewGuid());
        repo.Agregar(ajena);
        var uow = new UnitOfWorkFalso();

        var resultado = await Ejecutar(comando, repo, propia, uow, "b12345674");

        resultado.EsExitoso.Should().BeTrue();
        uow.VecesGuardado.Should().Be(1);
    }

    [Theory]
    [InlineData("EditarCliente")]
    [InlineData("EditarEmpresa")]
    [InlineData("EditarSubcontrata")]
    public async Task Editar_con_su_propio_Cif_no_es_duplicado(string comando)
    {
        var (repo, propia) = Preparar();
        var resultado = await Ejecutar(comando, repo, propia, new UnitOfWorkFalso(), " p1234567d ");
        resultado.EsExitoso.Should().BeTrue();
    }

    private static (EmpresaRepositorioFalso, Empresa) Preparar()
    {
        var repo = new EmpresaRepositorioFalso();
        var propia = Empresa.CrearComoCliente("Identidad original", "P1234567D", false, null, null);
        repo.Agregar(propia);
        return (repo, propia);
    }

    private static async Task<Result> Ejecutar(string comando, EmpresaRepositorioFalso repo, Empresa empresa,
        UnitOfWorkFalso uow, string cif)
    {
        var contexto = new CaeManager.Application.Tests.Documentos.EmpresasQueryContextFalso();
        var alcance = new AlcanceDatosServiceFalso();
        var relaciones = new RelacionesVacias();
        return comando switch
        {
            "CrearCliente" => await new CrearClienteCommandHandler(repo, uow, new CurrentUserServiceFalso(),
                new TransaccionDeComandoFalsa(), new BloqueoCarteraUsuarioFalso(),
                new DirectorioDestinosCarteraFalso(new DestinoCartera(true, "GestorCae", null, false)))
                .Handle(new CrearClienteCommand("Nueva identidad", cif, false, null), default),
            "EditarCliente" => await new EditarClienteCommandHandler(repo, alcance, uow)
                .Handle(new EditarClienteCommand(empresa.Id, "Nueva identidad", cif, false, null, empresa.Version), default),
            "CrearEmpresa" => await new CrearEmpresaCommandHandler(repo, relaciones, contexto, alcance, uow)
                .Handle(new CrearEmpresaCommand("Nueva identidad", cif, []), default),
            "EditarEmpresa" => await new EditarEmpresaCommandHandler(repo, relaciones, contexto, null!, alcance, uow)
                .Handle(new EditarEmpresaCommand(empresa.Id, "Nueva identidad", cif, [], Version: empresa.Version), default),
            "CrearSubcontrata" => await new CrearSubcontrataCommandHandler(repo, relaciones, contexto, alcance, uow)
                .Handle(new CrearSubcontrataCommand("Nueva identidad", cif, [], []), default),
            "EditarSubcontrata" => await new EditarSubcontrataCommandHandler(repo, relaciones, contexto, null!, alcance, uow)
                .Handle(new EditarSubcontrataCommand(empresa.Id, "Nueva identidad", cif, [], [], empresa.Version), default),
            _ => throw new ArgumentOutOfRangeException(nameof(comando))
        };
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Importacion_rechaza_bajas_del_Tenant_propietario_y_no_las_ajenas(bool otroTenant, bool reemplazar)
    {
        var repo = new EmpresaRepositorioFalso();
        var baja = Empresa.CrearComoCliente("Baja", "B12345674", false, null, null);
        baja.MarcarComoEliminado(Guid.NewGuid());
        if (otroTenant)
            typeof(EntidadConTenant).GetProperty(nameof(Empresa.TenantId))!.SetValue(baja, Guid.NewGuid());
        repo.Agregar(baja);
        var uow = new UnitOfWorkFalso();
        var resultado = await Importar(repo, uow, reemplazar);
        resultado.EsExitoso.Should().Be(otroTenant);
        if (!otroTenant)
        {
            resultado.Error.Codigo.Should().Be("Empresa.CifDeBaja");
            resultado.Error.Mensaje.Should().Contain("B12345674");
        }
        else
            resultado.Valor.ClientesCreados.Should().Be(1);
        uow.VecesGuardado.Should().Be(otroTenant ? 1 : 0);
        baja.EstaEliminado.Should().BeTrue();
    }

    [Fact]
    public async Task Importacion_fusiona_Cif_activo_con_mayusculas_distintas_y_espacios()
    {
        var repo = new EmpresaRepositorioFalso();
        repo.Agregar(Empresa.CrearComoCliente("Existente", "B12345674", false, null, null));
        var resultado = await Importar(repo, new UnitOfWorkFalso(), false);
        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.ClientesCreados.Should().Be(0);
        repo.Empresas.Should().ContainSingle();
    }

    private static Task<Result<ResultadoImportacionCombinadaDto>> Importar(EmpresaRepositorioFalso repo, UnitOfWorkFalso uow, bool reemplazar)
    {
        var contexto = new CaeManager.Application.Tests.Documentos.EmpresasQueryContextFalso();
        contexto.ListaEmpresas.AddRange(repo.Empresas.Where(e => e.TenantId == repo.TenantPropietarioId && !e.EstaEliminado));
        var handler = new EjecutarImportacionCombinadaCommandHandler(repo, new RelacionesVacias(),
            new CaeManager.Application.Tests.Centros.CentroRepositorioFalso(),
            new CaeManager.Application.Tests.Trabajadores.TrabajadorRepositorioFalso(),
            new CaeManager.Application.Tests.Plantillas.CentrosQueryContextFalso(), contexto,
            new CaeManager.Application.Tests.Plantillas.TrabajadoresQueryContextFalso(), uow, new AdministracionPermitida());
        return handler.Handle(new EjecutarImportacionCombinadaCommand(
            new PlanImportacionCombinadaDto([new("Importada", " b12345674 ", false, false)], [], [], [], [], []), reemplazar), default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Carrera_se_traduce_al_mismo_error_de_dominio(bool baja)
    {
        var repo = new EmpresaRepositorioFalso();
        var existente = new Empresa("Ganadora", "B12345674");
        if (baja)
            existente.MarcarComoEliminado(Guid.NewGuid());
        repo.Agregar(existente);
        var behavior = new DuplicadosCifEmpresaBehavior<CrearEmpresaCommand, Result<Guid>>(Proveedor(repo));
        var resultado = await behavior.Handle(new CrearEmpresaCommand("Nueva", "B12345674", []),
            _ => throw new CifEmpresaDuplicadoException("B12345674", new InvalidOperationException()), default);
        resultado.Error.Codigo.Should().Be(baja ? "Empresa.CifDeBaja" : "Empresa.CifDuplicado");
    }

    [Fact]
    public async Task Carrera_en_un_comando_de_Cliente_devuelve_el_mismo_codigo_que_su_comprobacion_previa()
    {
        var repo = new EmpresaRepositorioFalso();
        repo.Agregar(new Empresa("Ganadora", "B12345674"));
        var behavior = new DuplicadosCifEmpresaBehavior<CrearClienteCommand, Result<Guid>>(Proveedor(repo));
        var resultado = await behavior.Handle(new CrearClienteCommand("Nueva", "B12345674", false, null),
            _ => throw new CifEmpresaDuplicadoException("B12345674", new InvalidOperationException()), default);
        resultado.Error.Codigo.Should().Be("Cliente.CifDuplicado");
    }

    [Fact]
    public async Task Sin_choque_el_behavior_no_resuelve_el_repositorio()
    {
        // MediatR construye el behavior para toda petición; un contenedor sin
        // IEmpresaRepository (los mínimos de integración) no debe romperse.
        var behavior = new DuplicadosCifEmpresaBehavior<CrearEmpresaCommand, Result<Guid>>(
            new ServiceCollection().BuildServiceProvider());
        var resultado = await behavior.Handle(new CrearEmpresaCommand("Nueva", "B12345674", []),
            _ => Task.FromResult(Result.Exito(Guid.Empty)), default);
        resultado.EsExitoso.Should().BeTrue();
    }

    private static IServiceProvider Proveedor(IEmpresaRepository repo) =>
        new ServiceCollection().AddSingleton(repo).BuildServiceProvider();

    private sealed class AdministracionPermitida : IAutorizacionEscrituraEfectiva
    {
        public Task<bool> EsActoDeAdministradorAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class RelacionesVacias : IRelacionEmpresarialRepository
    {
        public void Agregar(RelacionEmpresarial relacion) => throw new NotImplementedException();
        public Task<ContrapartesVigentes> ObtenerContrapartesVigentesAsync(Guid proveedoraId, CancellationToken cancellationToken = default) => Task.FromResult(ContrapartesVigentes.Vacias);
        public Task<bool> AgregarSiNoVigenteAsync(Guid proveedoraId, Guid clienteId, DateTime ahora, Guid? enmarcadaEnId = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> CerrarVigenteAsync(Guid proveedoraId, Guid clienteId, DateTime ahora, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RelacionEmpresarial?> ObtenerVigentePorParAsync(Guid proveedoraId, Guid clienteId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Guid?> ObtenerCandidatoUnicoParaEnmarcarAsync(IReadOnlyCollection<Guid> empresaIdsCandidatas, Guid clienteId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> CreariaUnCicloAsync(Guid relacionId, Guid propuestaEnmarcadaEnId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
