using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.Empresas;
using CaeManager.Application.Empresas.Commands.EditarEmpresa;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Configuracion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Empresas;

/// <summary>
/// D3 (DN-4, DN-7) en <see cref="EditarEmpresaCommandHandler"/>: añadir un Cliente empresarial a una Empresa es un alta de
/// Outbound y se cierra al Tenant TEC participante sin línea Outbound; editar su identidad sin añadir Clientes empresariales
/// sigue abierto. La guarda va en el handler porque depende del diff de Clientes empresariales, que solo él calcula.
/// </summary>
public class EditarEmpresaLimiteTenantTecTests
{
    private readonly EmpresaRepositorioFalso _repo = new();
    private readonly UnitOfWorkFalso _unidad = new();
    private readonly Empresa _empresa = Empresa.CrearComoCliente("Empresa de prueba", "P1234567D", false, null, null);

    public EditarEmpresaLimiteTenantTecTests() => _repo.Agregar(_empresa);

    private Task<Result> Ejecutar(Guid? tenantActivo, IOutboundCerradoTenantTec limite, params Guid[] clienteIds) =>
        new EditarEmpresaCommandHandler(_repo, new RelacionesVacias(), new Documentos.EmpresasQueryContextFalso(), null!,
                new AlcanceDatosServiceFalso(), _unidad, new TenantActualFijo(tenantActivo), limite)
            .Handle(new EditarEmpresaCommand(_empresa.Id, "Empresa de prueba renombrada", "B12345674", clienteIds,
                Version: _empresa.Version), default);

    // Solo el Tenant activo está cerrado: un handler que preguntara por otro Tenant vería la línea abierta.
    private static OutboundCerradoTenantTecFijo CerradoSoloPara(Guid tenantId) => new(t => t == tenantId);

    // Solo el Tenant activo está abierto: un handler que preguntara por otro Tenant vería la línea cerrada.
    private static OutboundCerradoTenantTecFijo AbiertoSoloPara(Guid tenantId) => new(t => t != tenantId);

    [Fact]
    public async Task Anadir_un_Cliente_empresarial_con_Outbound_cerrado_se_deniega()
    {
        var tenantActivo = Guid.NewGuid();
        var limite = CerradoSoloPara(tenantActivo);

        var resultado = await Ejecutar(tenantActivo, limite, Guid.NewGuid());

        resultado.Error!.Codigo.Should().Be(ErroresLimiteTenantTec.OutboundCerrado.Codigo);
        _unidad.VecesGuardado.Should().Be(0);
        limite.Preguntados.Should().Equal([tenantActivo], "el Tenant beneficiario de la edición es el Tenant activo");
    }

    [Fact]
    public async Task Anadir_un_Cliente_empresarial_sin_Tenant_activo_se_deniega()
    {
        var resultado = await Ejecutar(null, OutboundCerradoTenantTecFijo.Abierto(), Guid.NewGuid());

        resultado.Error!.Codigo.Should().Be(ErroresLimiteTenantTec.OutboundCerrado.Codigo, "falla cerrado");
    }

    [Fact]
    public async Task Con_Outbound_abierto_la_guarda_no_decide()
    {
        // El Cliente empresarial no existe en el contexto falso: el fallo que llega es el de después de la guarda.
        var tenantActivo = Guid.NewGuid();
        var limite = AbiertoSoloPara(tenantActivo);

        var resultado = await Ejecutar(tenantActivo, limite, Guid.NewGuid());

        resultado.Error!.Codigo.Should().Be("Empresa.ClienteNoEncontrado");
        limite.Preguntados.Should().Equal([tenantActivo], "pregunta por el Tenant activo y por ningún otro");
    }

    [Fact]
    public async Task Editar_la_identidad_sin_anadir_Clientes_empresariales_sigue_abierto()
    {
        var limite = OutboundCerradoTenantTecFijo.Cerrado();

        var resultado = await Ejecutar(Guid.NewGuid(), limite);

        resultado.EsExitoso.Should().BeTrue();
        limite.Preguntados.Should().BeEmpty("sin altas de Clientes empresariales no hay nada que cerrar");
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
