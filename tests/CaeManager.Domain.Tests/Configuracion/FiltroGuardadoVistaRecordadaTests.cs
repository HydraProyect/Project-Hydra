using CaeManager.Domain.Configuracion;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Configuracion;

/// <summary>
/// La vista recordada es la fila reservada de <see cref="FiltroGuardado"/>: nace
/// solo por su fábrica, es la única que cambia de valores y su nombre no se puede
/// usar para un filtro con nombre.
/// </summary>
public class FiltroGuardadoVistaRecordadaTests
{
    [Fact]
    public void La_fabrica_crea_la_fila_reservada_del_usuario_en_esa_pantalla()
    {
        var usuarioId = Guid.NewGuid();

        var vista = FiltroGuardado.CrearVistaRecordada(usuarioId, "Clientes", "{\"estado\":\"Critico\"}");

        vista.UsuarioId.Should().Be(usuarioId);
        vista.Pantalla.Should().Be("Clientes");
        vista.Nombre.Should().Be(FiltroGuardado.NombreVistaRecordada);
        vista.ValoresJson.Should().Be("{\"estado\":\"Critico\"}");
        vista.EsVistaRecordada.Should().BeTrue();
    }

    [Fact]
    public void Un_filtro_con_nombre_no_es_la_vista_recordada()
    {
        new FiltroGuardado(Guid.NewGuid(), "Clientes", "Críticos", "{}").EsVistaRecordada.Should().BeFalse();
    }

    [Fact]
    public void El_nombre_reservado_cabe_en_la_columna_y_no_es_un_nombre_corriente()
    {
        FiltroGuardado.NombreVistaRecordada.Length.Should().BeLessThanOrEqualTo(100, "la columna Nombre admite 100 caracteres");
        FiltroGuardado.NombreVistaRecordada.Should().StartWith("__").And.EndWith("__");
        FiltroGuardado.NombreVistaRecordada.Trim().Should().Be(FiltroGuardado.NombreVistaRecordada,
            "el constructor recorta el nombre: con espacios de borde la fila guardada no casaría con la constante");
    }

    [Theory]
    [InlineData("__vista_recordada__")]
    [InlineData("  __vista_recordada__  ")]
    [InlineData("__VISTA_RECORDADA__")]
    [InlineData("\t__Vista_Recordada__\n")]
    public void El_constructor_publico_rechaza_el_nombre_reservado(string nombre)
    {
        FiltroGuardado.EsNombreReservado(nombre).Should().BeTrue();

        var accion = () => new FiltroGuardado(Guid.NewGuid(), "Clientes", nombre, "{}");

        accion.Should().Throw<ArgumentException>().WithParameterName("nombre");
    }

    [Theory]
    [InlineData("Vista recordada")]
    [InlineData("vista_recordada")]
    [InlineData("__vista_recordada__ 2")]
    [InlineData("")]
    [InlineData(null)]
    public void Un_nombre_que_solo_se_parece_no_esta_reservado(string? nombre)
    {
        FiltroGuardado.EsNombreReservado(nombre).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void La_fabrica_exige_usuario_pantalla_y_valores(string vacio)
    {
        var sinUsuario = () => FiltroGuardado.CrearVistaRecordada(Guid.Empty, "Clientes", "{}");
        var sinPantalla = () => FiltroGuardado.CrearVistaRecordada(Guid.NewGuid(), vacio, "{}");
        var sinValores = () => FiltroGuardado.CrearVistaRecordada(Guid.NewGuid(), "Clientes", vacio);

        sinUsuario.Should().Throw<ArgumentException>();
        sinPantalla.Should().Throw<ArgumentException>();
        sinValores.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Recordar_sustituye_los_valores_y_no_toca_la_clave()
    {
        var usuarioId = Guid.NewGuid();
        var vista = FiltroGuardado.CrearVistaRecordada(usuarioId, "Clientes", "{\"estado\":\"Critico\"}");
        var id = vista.Id;

        vista.RecordarVista("{\"estado\":\"AlDia\"}");

        vista.ValoresJson.Should().Be("{\"estado\":\"AlDia\"}");
        vista.Id.Should().Be(id);
        vista.UsuarioId.Should().Be(usuarioId);
        vista.Pantalla.Should().Be("Clientes");
        vista.EsVistaRecordada.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Recordar_una_vista_vacia_falla_y_conserva_la_anterior(string vacio)
    {
        var vista = FiltroGuardado.CrearVistaRecordada(Guid.NewGuid(), "Clientes", "{\"estado\":\"Critico\"}");

        var accion = () => vista.RecordarVista(vacio);

        accion.Should().Throw<ArgumentException>();
        vista.ValoresJson.Should().Be("{\"estado\":\"Critico\"}");
    }

    [Fact]
    public void Un_filtro_con_nombre_no_cambia_de_valores()
    {
        var filtro = new FiltroGuardado(Guid.NewGuid(), "Clientes", "Críticos", "{\"estado\":\"Critico\"}");

        var accion = () => filtro.RecordarVista("{\"estado\":\"AlDia\"}");

        accion.Should().Throw<InvalidOperationException>();
        filtro.ValoresJson.Should().Be("{\"estado\":\"Critico\"}");
    }
}
