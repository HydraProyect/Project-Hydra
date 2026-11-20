using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.Empresas.Commands.RestaurarEmpresa;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Empresas;

/// <summary>
/// D3 (DN-4, DN-7): restaurar desde la Papelera una Empresa con Relaciones Empresariales vigentes con Clientes empresariales
/// devuelve esas relaciones a Outbound (eliminar la Empresa no las cierra), así que se deniega al Tenant TEC participante
/// sin línea Outbound vigente. La Empresa sin Clientes empresariales sigue abierta. Decide por el Tenant activo, que es el
/// Tenant propietario de la Empresa.
/// </summary>
public class RestaurarEmpresaLimiteTenantTecTests
{
    private static readonly Guid TenantActivo = Guid.NewGuid();

    [Fact]
    public async Task Con_Clientes_empresariales_vigentes_y_Outbound_cerrado_no_se_restaura()
    {
        var (empresa, uow, resultado) = await Restaurar(
            new RelacionesEmpresarialesFijas(Guid.NewGuid()), new OutboundCerradoTenantTecFijo(t => t == TenantActivo));

        resultado.Error.Should().Be(ErroresLimiteTenantTec.OutboundCerrado);
        empresa.EstaEliminado.Should().BeTrue();
        uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_Clientes_empresariales_vigentes_se_restaura_aunque_Outbound_este_cerrado()
    {
        var (empresa, uow, resultado) = await Restaurar(
            new RelacionesEmpresarialesFijas(), new OutboundCerradoTenantTecFijo(t => t == TenantActivo));

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        empresa.EstaEliminado.Should().BeFalse();
        uow.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Con_Clientes_empresariales_vigentes_y_Outbound_abierto_se_restaura()
    {
        var (empresa, uow, resultado) = await Restaurar(
            new RelacionesEmpresarialesFijas(Guid.NewGuid()), new OutboundCerradoTenantTecFijo(t => t != TenantActivo));

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        empresa.EstaEliminado.Should().BeFalse();
        uow.VecesGuardado.Should().Be(1);
    }

    private static async Task<(Empresa Empresa, UnitOfWorkFalso Uow, Result Resultado)> Restaurar(
        RelacionesEmpresarialesFijas relaciones, OutboundCerradoTenantTecFijo outbound)
    {
        var empresa = new Empresa("Restaurable, S.L.", "B12345674");
        typeof(Empresa).GetProperty(nameof(Empresa.EsPropia))!.SetValue(empresa, false);
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(empresa, TenantActivo);
        empresa.MarcarComoEliminado(Guid.NewGuid());

        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(empresa);
        var uow = new UnitOfWorkFalso();

        var resultado = await new RestaurarEmpresaCommandHandler(
                empresas, new LimitesDePrueba.TenantActualFijo(TenantActivo), new AlcanceDatosServiceFalso(), uow,
                LimitesDePrueba.Sobre(empresas, new AlertaOperativaFalsa(), TenantActivo), relaciones, outbound)
            .Handle(new RestaurarEmpresaCommand(empresa.Id), default);

        return (empresa, uow, resultado);
    }
}
