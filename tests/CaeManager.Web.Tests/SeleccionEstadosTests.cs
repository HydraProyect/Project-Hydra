using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// La selección de la franja de estado tal como viaja en la URL: estados de código separados por coma
/// (<c>?estado=Vencido,Urgente,Proximo</c>). Es lo que leen las páginas para armar su consulta, así que un
/// valor mal separado o un desconocido que se cuele acabaría filtrando por algo que nadie marcó.
/// </summary>
public class SeleccionEstadosTests
{
    // ---------------------------------------------------------------- Separar

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    [InlineData(" , ,")]
    public void Separar_sin_ningun_valor_devuelve_la_lista_vacia(string? valor)
    {
        SeleccionEstados.Separar(valor).Should().BeEmpty();
    }

    [Fact]
    public void Separar_un_solo_valor_es_una_seleccion_de_un_elemento()
    {
        SeleccionEstados.Separar("Urgente").Should().Equal(["Urgente"],
            "un enlace anterior a la franja, con un solo estado, sigue siendo válido");
    }

    [Fact]
    public void Separar_descarta_los_huecos_y_recorta_los_espacios()
    {
        SeleccionEstados.Separar(" Vencido , ,Urgente,, Proximo ").Should().Equal("Vencido", "Urgente", "Proximo");
    }

    [Fact]
    public void Separar_quita_los_repetidos_y_conserva_el_orden_de_llegada()
    {
        SeleccionEstados.Separar("Urgente,Vencido,Urgente,Proximo,Vencido").Should().Equal("Urgente", "Vencido", "Proximo");
    }

    [Fact]
    public void Separar_distingue_mayusculas_porque_son_nombres_de_codigo()
    {
        SeleccionEstados.Separar("Vencido,vencido").Should().Equal("Vencido", "vencido");
    }

    // --------------------------------------------------------- Separar<TEnum>

    [Fact]
    public void Separar_como_enum_devuelve_los_estados_en_el_orden_de_llegada()
    {
        SeleccionEstados.Separar<EstadoDocumento>("Urgente, Proximo,Vencido")
            .Should().Equal(EstadoDocumento.Urgente, EstadoDocumento.Proximo, EstadoDocumento.Vencido);
    }

    [Fact]
    public void Separar_como_enum_descarta_lo_que_no_es_un_nombre_del_enum()
    {
        SeleccionEstados.Separar<EstadoDocumento>("Inventado,Vencido,abiertos,,Proximo")
            .Should().Equal(EstadoDocumento.Vencido, EstadoDocumento.Proximo);
    }

    /// <summary>
    /// <c>Enum.TryParse</c> acepta el número de un valor ("4") y también números que no son de ningún valor
    /// ("999"): la URL lleva nombres, así que ni uno ni otro filtran.
    /// </summary>
    [Fact]
    public void Separar_como_enum_no_acepta_numeros_aunque_correspondan_a_un_valor()
    {
        var numeroDeVencido = ((int)EstadoDocumento.Vencido).ToString(System.Globalization.CultureInfo.InvariantCulture);

        SeleccionEstados.Separar<EstadoDocumento>($"{numeroDeVencido},999,-1").Should().BeEmpty();
        SeleccionEstados.Separar<EstadoDocumento>($"{numeroDeVencido},Urgente").Should().Equal(EstadoDocumento.Urgente);
    }

    [Fact]
    public void Separar_como_enum_distingue_mayusculas()
    {
        SeleccionEstados.Separar<EstadoDocumento>("vencido,VENCIDO").Should().BeEmpty(
            "un nombre mal escrito es un valor desconocido: no filtra");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Inventado")]
    public void Separar_como_enum_sin_ningun_estado_conocido_devuelve_la_lista_vacia(string? valor)
    {
        SeleccionEstados.Separar<EstadoDocumento>(valor).Should().BeEmpty();
    }

    // ------------------------------------------------------------------- Unir

    [Fact]
    public void Unir_separa_con_coma_y_sin_espacios()
    {
        SeleccionEstados.Unir(["Vencido", "Urgente", "Proximo"]).Should().Be("Vencido,Urgente,Proximo");
    }

    [Fact]
    public void Unir_quita_los_repetidos_conservando_el_primero()
    {
        SeleccionEstados.Unir(["Urgente", "Vencido", "Urgente"]).Should().Be("Urgente,Vencido");
    }

    [Fact]
    public void Unir_sin_valores_devuelve_null_para_que_el_parametro_desaparezca_de_la_url()
    {
        SeleccionEstados.Unir([]).Should().BeNull();
    }

    [Fact]
    public void Unir_y_Separar_son_inversos()
    {
        string[] seleccion = ["Vencido", "Urgente", "Proximo"];

        SeleccionEstados.Separar(SeleccionEstados.Unir(seleccion)).Should().Equal(seleccion);
    }
}
