using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.Subcontratas.Commands.EditarSubcontrata;
using CaeManager.Application.Subcontratas.Commands.RestaurarSubcontrata;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Empresas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using FluentAssertions;
using Xunit;
using EmpresaRepositorioFalso = CaeManager.Application.Tests.Clientes.EmpresaRepositorioFalso;

namespace CaeManager.Application.Tests.Subcontratas;

/// <summary>
/// D5 abre las Subcontratas al Tenant TEC, pero no la Relación Empresarial de una Subcontrata con un Cliente empresarial:
/// EP-4 deja al Tenant TEC «sin Clientes empresariales propios fuera de un ContratoAcceso», y D3 (con DN-7) cierra sus
/// altas y restauraciones al Tenant TEC participante sin línea Outbound. Las dos vías que dependen de los datos que solo
/// el handler carga se cierran en él, con la misma regla y el mismo error que en Empresas
/// (<c>EditarEmpresaLimiteTenantTecTests</c>, <c>RestaurarEmpresaLimiteTenantTecTests</c>). El alta por datos
/// (<c>CrearSubcontrataCommand</c> con Clientes empresariales) se prueba en <c>SubcontratasAbiertasAlTenantTecTests</c>.
/// </summary>
public class SubcontrataLimiteTenantTecTests
{
    private static readonly Guid TenantActivo = Guid.NewGuid();

    // ───────────── EditarSubcontrataCommandHandler ─────────────

    private static Task<Result> Editar(
        Empresa subcontrata, EmpresaRepositorioFalso repo, UnitOfWorkFalso unidad, Guid? tenantActivo,
        IOutboundCerradoTenantTec limite, RelacionesEmpresarialesFijas relaciones, params Guid[] clienteIds) =>
        new EditarSubcontrataCommandHandler(repo, relaciones, new EmpresasQueryContextFalso(), guardDeCierre: null!,
                new AlcanceDatosServiceFalso(), unidad, new Configuracion.TenantActualFijo(tenantActivo), limite)
            .Handle(new EditarSubcontrataCommand(subcontrata.Id, "Andamios del Sur renombrada S.L.", "B12345674", clienteIds, [],
                subcontrata.Version), default);

    private static (Empresa Subcontrata, EmpresaRepositorioFalso Repo, UnitOfWorkFalso Unidad) SubcontrataEditable()
    {
        var subcontrata = Empresa.CrearComoSubcontrata(
            "Andamios del Sur S.L.", "B12345674", NivelServicioSubcontrata.Gestionada.ToString());
        var repo = new EmpresaRepositorioFalso();
        repo.Agregar(subcontrata);
        return (subcontrata, repo, new UnitOfWorkFalso());
    }

    [Fact]
    public async Task Editar_anadiendo_un_Cliente_empresarial_con_Outbound_cerrado_se_deniega()
    {
        var (subcontrata, repo, unidad) = SubcontrataEditable();

        // Solo el Tenant activo está cerrado: un handler que preguntara por otro Tenant vería la línea abierta.
        var limite = new OutboundCerradoTenantTecFijo(t => t == TenantActivo);

        var resultado = await Editar(subcontrata, repo, unidad, TenantActivo, limite,
            new RelacionesEmpresarialesFijas(), Guid.NewGuid());

        resultado.Error!.Codigo.Should().Be(ErroresLimiteTenantTec.OutboundCerrado.Codigo,
            "el mismo error que EditarEmpresaCommandHandler");
        unidad.VecesGuardado.Should().Be(0);
        limite.Preguntados.Should().Equal([TenantActivo], "el Tenant beneficiario de la edición es el Tenant activo");
    }

    [Fact]
    public async Task Editar_anadiendo_un_Cliente_empresarial_sin_Tenant_activo_se_deniega()
    {
        var (subcontrata, repo, unidad) = SubcontrataEditable();

        var resultado = await Editar(subcontrata, repo, unidad, null, OutboundCerradoTenantTecFijo.Abierto(),
            new RelacionesEmpresarialesFijas(), Guid.NewGuid());

        resultado.Error!.Codigo.Should().Be(ErroresLimiteTenantTec.OutboundCerrado.Codigo, "falla cerrado");
    }

    [Fact]
    public async Task Control_editar_anadiendo_un_Cliente_empresarial_con_Outbound_abierto_la_guarda_no_decide()
    {
        var (subcontrata, repo, unidad) = SubcontrataEditable();

        // El Cliente empresarial no existe en el contexto falso: el fallo que llega es el de después de la guarda.
        // Solo el Tenant activo está abierto: un handler que preguntara por otro Tenant vería la línea cerrada.
        var limite = new OutboundCerradoTenantTecFijo(t => t != TenantActivo);

        var resultado = await Editar(subcontrata, repo, unidad, TenantActivo, limite,
            new RelacionesEmpresarialesFijas(), Guid.NewGuid());

        resultado.Error!.Codigo.Should().Be("Subcontrata.ClienteNoEncontrado");
        limite.Preguntados.Should().Equal([TenantActivo], "pregunta por el Tenant activo y por ningún otro");
    }

    [Fact]
    public async Task Control_editar_la_identidad_sin_anadir_Clientes_empresariales_sigue_abierto()
    {
        var (subcontrata, repo, unidad) = SubcontrataEditable();
        var limite = OutboundCerradoTenantTecFijo.Cerrado();

        var resultado = await Editar(subcontrata, repo, unidad, TenantActivo, limite, new RelacionesEmpresarialesFijas());

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        unidad.VecesGuardado.Should().Be(1);
        limite.Preguntados.Should().BeEmpty("sin altas de Clientes empresariales no hay nada que cerrar");
    }

    [Fact]
    public async Task Control_conservar_un_Cliente_empresarial_ya_vinculado_no_es_un_alta()
    {
        var (subcontrata, repo, unidad) = SubcontrataEditable();
        var vinculado = Guid.NewGuid();
        var limite = OutboundCerradoTenantTecFijo.Cerrado();

        var resultado = await Editar(subcontrata, repo, unidad, TenantActivo, limite,
            new RelacionesEmpresarialesFijas(vinculado), vinculado);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        limite.Preguntados.Should().BeEmpty("la guarda decide sobre el diff, no sobre el conjunto deseado");
    }

    // ───────────── RestaurarSubcontrataCommandHandler ─────────────

    [Fact]
    public async Task Restaurar_con_Clientes_empresariales_vigentes_y_Outbound_cerrado_no_se_restaura()
    {
        var (subcontrata, uow, resultado) = await Restaurar(
            new RelacionesEmpresarialesFijas(Guid.NewGuid()), new OutboundCerradoTenantTecFijo(t => t == TenantActivo));

        resultado.Error.Should().Be(ErroresLimiteTenantTec.OutboundCerrado, "el mismo error que RestaurarEmpresaCommandHandler");
        subcontrata.EstaEliminado.Should().BeTrue();
        uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Control_restaurar_sin_Clientes_empresariales_vigentes_aunque_Outbound_este_cerrado()
    {
        var (subcontrata, uow, resultado) = await Restaurar(
            new RelacionesEmpresarialesFijas(), new OutboundCerradoTenantTecFijo(t => t == TenantActivo));

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        subcontrata.EstaEliminado.Should().BeFalse();
        uow.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Control_restaurar_con_Clientes_empresariales_vigentes_y_Outbound_abierto()
    {
        var (subcontrata, uow, resultado) = await Restaurar(
            new RelacionesEmpresarialesFijas(Guid.NewGuid()), new OutboundCerradoTenantTecFijo(t => t != TenantActivo));

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        subcontrata.EstaEliminado.Should().BeFalse();
        uow.VecesGuardado.Should().Be(1);
    }

    private static async Task<(Empresa Subcontrata, UnitOfWorkFalso Uow, Result Resultado)> Restaurar(
        RelacionesEmpresarialesFijas relaciones, OutboundCerradoTenantTecFijo outbound)
    {
        var subcontrata = Empresa.CrearComoSubcontrata(
            "Restaurable S.L.", "B12345674", NivelServicioSubcontrata.Gestionada.ToString());
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(subcontrata, TenantActivo);
        subcontrata.MarcarComoEliminado(Guid.NewGuid());

        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(subcontrata);
        var uow = new UnitOfWorkFalso();

        var resultado = await new RestaurarSubcontrataCommandHandler(
                empresas, new LimitesDePrueba.TenantActualFijo(TenantActivo), new AlcanceDatosServiceFalso(), uow,
                LimitesDePrueba.Sobre(empresas, new AlertaOperativaFalsa(), TenantActivo), relaciones, outbound)
            .Handle(new RestaurarSubcontrataCommand(subcontrata.Id), default);

        return (subcontrata, uow, resultado);
    }
}
