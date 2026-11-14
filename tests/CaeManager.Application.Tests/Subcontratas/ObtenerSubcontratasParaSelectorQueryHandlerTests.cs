using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using FluentAssertions;

namespace CaeManager.Application.Tests.Subcontratas;

/// <summary>
/// Hueco del inventario de alcance del 2026-11-14: el selector de Subcontratas devolvía el
/// catálogo entero del Tenant propietario sin mirar la Asignación de Cartera. Un Gestor CAE
/// veía en el alta de Trabajador y de Vehículo, y en la pastilla «Empresa» de /vehiculos, las
/// razones sociales de Subcontratas fuera de su cartera, que <c>CrearVehiculoCommand</c> y
/// <c>CrearTrabajadorCommand</c> le rechazan después.
///
/// <para>
/// <b>Lo que SÍ observa:</b> que el handler aplica la lista de
/// <c>IAlcanceDatosService.ObtenerSubcontrataIdsParaGestionAsync</c> tal como llega: <c>null</c>
/// no filtra (Administrador y Dirección CAE, sin restricción de cartera) y una lista estrecha,
/// también vacía. <b>Lo que NO observa:</b> qué lista resuelve cada rol a partir de su
/// Asignación de Cartera (eso es de <c>AlcanceDatosService</c>, en Infrastructure) ni RLS.
/// </para>
/// </summary>
public class ObtenerSubcontratasParaSelectorQueryHandlerTests
{
    private readonly EmpresasQueryContextFalso _empresas = new();

    private readonly Empresa _subcontrataEnCartera = Empresa.CrearComoSubcontrata("Aislamientos Nervión S.L.", "B12345674", "Estandar");
    private readonly Empresa _subcontrataAjena = Empresa.CrearComoSubcontrata("Montajes Guadalquivir S.L.", "B87654323", "Estandar");
    private readonly Empresa _empresaPropia = new("Ibertec Sevilla S.A.");

    public ObtenerSubcontratasParaSelectorQueryHandlerTests()
    {
        _empresas.ListaEmpresas.AddRange([_subcontrataEnCartera, _subcontrataAjena, _empresaPropia]);
    }

    [Fact]
    public async Task Administrador_o_Direccion_CAE_sin_restriccion_de_cartera_ven_todas_las_Subcontratas()
    {
        var resultado = await HandleAsync(new AlcanceDatosServiceFalso(tieneAccesoTotal: true));

        resultado.Select(s => s.RazonSocial).Should().Equal(
            [_subcontrataEnCartera.RazonSocial, _subcontrataAjena.RazonSocial],
            "sin restricción de cartera el selector sigue ofreciendo el catálogo de Subcontratas, ordenado, y nunca una Empresa propia");
    }

    [Fact]
    public async Task Gestor_CAE_con_Asignacion_de_Cartera_solo_ve_las_Subcontratas_de_su_cartera()
    {
        var resultado = await HandleAsync(GestorCaeConCartera(_subcontrataEnCartera.Id));

        resultado.Should().ContainSingle().Which.Id.Should().Be(_subcontrataEnCartera.Id);
    }

    [Fact]
    public async Task Gestor_CAE_cuya_cartera_no_alcanza_ninguna_Subcontrata_no_ve_ninguna()
    {
        var resultado = await HandleAsync(GestorCaeConCartera());

        resultado.Should().BeEmpty("una lista vacía de alcance no deja pasar nada, nunca equivale a «sin filtro»");
    }

    [Fact]
    public async Task El_alcance_de_gestion_decide_aunque_el_de_lectura_sea_mas_amplio()
    {
        // Un usuario de portal (rol Cliente) puede LEER la documentación de las Subcontratas de
        // su Cliente, pero el selector alimenta altas internas: manda la variante de gestión.
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false,
            subcontrataIdsVisibles: [_subcontrataEnCartera.Id, _subcontrataAjena.Id],
            subcontrataIdsParaGestion: []);

        var resultado = await HandleAsync(alcance);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task El_filtro_por_EmpresaId_se_combina_con_la_cartera_sin_ensancharla()
    {
        // Las dos Subcontratas prestan servicio vigente a la misma Empresa propia, pero solo una
        // está en la cartera: el vínculo con la Empresa no concede visibilidad.
        var ahora = DateTime.UtcNow;
        _empresas.ListaRelacionesEmpresariales.AddRange([
            RelacionEmpresarial.Crear(_subcontrataEnCartera.Id, _empresaPropia.Id, ahora),
            RelacionEmpresarial.Crear(_subcontrataAjena.Id, _empresaPropia.Id, ahora)
        ]);

        var resultado = await HandleAsync(GestorCaeConCartera(_subcontrataEnCartera.Id), _empresaPropia.Id);

        resultado.Should().ContainSingle().Which.Id.Should().Be(_subcontrataEnCartera.Id);
    }

    [Fact]
    public async Task Un_Id_de_Empresa_propia_en_la_cartera_no_la_cuela_como_Subcontrata()
    {
        var resultado = await HandleAsync(GestorCaeConCartera(_subcontrataEnCartera.Id, _empresaPropia.Id));

        resultado.Should().ContainSingle().Which.Id.Should().Be(_subcontrataEnCartera.Id);
    }

    private static AlcanceDatosServiceFalso GestorCaeConCartera(params Guid[] subcontrataIds) => new(
        tieneAccesoTotal: false,
        subcontrataIdsVisibles: subcontrataIds,
        subcontrataIdsParaGestion: subcontrataIds);

    private Task<IReadOnlyList<SubcontrataSelectorDto>> HandleAsync(AlcanceDatosServiceFalso alcance, Guid? empresaId = null) =>
        new ObtenerSubcontratasParaSelectorQueryHandler(_empresas, alcance)
            .Handle(new ObtenerSubcontratasParaSelectorQuery(empresaId), CancellationToken.None);
}
