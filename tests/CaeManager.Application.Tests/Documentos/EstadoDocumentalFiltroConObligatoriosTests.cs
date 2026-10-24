using CaeManager.Application.Documentos;
using CaeManager.Domain.Documentos;
using FluentAssertions;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// La clave de estado de los listados de Empresas y de Trabajadores (decisiones del 2026-10-23): primero lo que va en rojo
/// por un requisito obligatorio de Centro (Vencido, Pendiente), después el vencido que solo avisa y luego lo demás. El filtro y
/// el recuento de «Vencido» llevan las dos claves, porque las dos filas se rotulan «Vencido».
/// </summary>
public class EstadoDocumentalFiltroConObligatoriosTests
{
    [Fact]
    public void Lo_que_va_en_rojo_se_ordena_antes_que_el_vencido_que_solo_avisa_y_este_antes_que_lo_por_vencer()
    {
        var claves = new[]
        {
            EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.Vencido),
            EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.Faltante),
            EstadoDocumentalFiltro.ClaveVencidoSinBloqueo,
            EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.Urgente),
            EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.Proximo),
            EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.SinConfirmar),
            EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.Vigente),
            EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.SinCaducidad)
        };

        claves.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void El_filtro_Vencido_deja_pasar_tambien_el_vencido_que_solo_avisa()
    {
        EstadoDocumentalFiltro.ClavesDeOrden(nameof(EstadoDocumento.Vencido), incluirFaltantes: true)
            .Should().BeEquivalentTo([
                EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.Vencido),
                EstadoDocumentalFiltro.ClaveVencidoSinBloqueo]);

        EstadoDocumentalFiltro.ClavesDeOrden(nameof(EstadoDocumento.Faltante), incluirFaltantes: true)
            .Should().BeEquivalentTo([EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.Faltante)],
                "Pendiente es solo lo obligatorio que falta");
    }

    [Fact]
    public void El_recuento_de_Vencido_suma_el_obligatorio_y_el_que_solo_avisa()
    {
        var recuentos = EstadoDocumentalFiltro.RecuentosPorEstado(new Dictionary<int, int>
        {
            [EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.Vencido)] = 2,
            [EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.Faltante)] = 3,
            [EstadoDocumentalFiltro.ClaveVencidoSinBloqueo] = 5,
            [EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.Urgente)] = 7
        }, incluirFaltantes: true);

        recuentos[nameof(EstadoDocumento.Vencido)].Should().Be(7);
        recuentos[nameof(EstadoDocumento.Faltante)].Should().Be(3);
        recuentos[nameof(EstadoDocumento.Urgente)].Should().Be(7);
        recuentos[nameof(EstadoDocumento.Proximo)].Should().Be(0);
    }

    [Fact]
    public void Sin_requisitos_obligatorios_las_claves_de_los_demas_listados_no_cambian()
    {
        EstadoDocumentalFiltro.ClavesDeOrden(nameof(EstadoDocumento.Vencido))
            .Should().BeEquivalentTo([EstadoDocumentalFiltro.ClaveOrden(EstadoDocumento.Vencido)]);
        EstadoDocumentalFiltro.RecuentosPorEstado(new Dictionary<int, int> { [0] = 4, [2] = 9 })[nameof(EstadoDocumento.Vencido)]
            .Should().Be(4, "fuera de Empresas y Trabajadores la clave 2 es Próximo, no un vencido");
    }
}
