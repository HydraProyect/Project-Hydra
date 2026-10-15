using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Trabajadores;

public class FichaTrabajadorTests
{
    private static Trabajador Crear() => Trabajador.DeEmpresa(Guid.NewGuid(), "Paula", "Campos", "12345678Z");

    private static void Rellenar(Trabajador t, string? afiliacion = "41 1234567 89", string? nacionalidad = " Colombiana ") =>
        t.ActualizarFicha(afiliacion, " Electricista ", new(1988, 3, 14), nacionalidad,
            " Plantilla ", new(2023, 5, 2), null, " Sí, desde mayo de 2023 ",
            " Residencia y trabajo por cuenta ajena ", new(2027, 2, 18), " Se identifica con NIE ");

    [Fact]
    public void Guarda_todos_los_datos_de_la_maqueta_sin_cambiar_identidad_ni_contacto()
    {
        var t = Crear();
        Rellenar(t);
        t.NumeroAfiliacionSeguridadSocial.Should().Be("41123456789");
        t.Puesto.Should().Be("Electricista");
        t.FechaNacimiento.Should().Be(new DateOnly(1988, 3, 14));
        t.Nacionalidad.Should().Be("Colombiana");
        t.TipoVinculo.Should().Be("Plantilla");
        t.FechaAltaEmpresa.Should().Be(new DateOnly(2023, 5, 2));
        t.FechaBajaEmpresa.Should().BeNull();
        t.FiguraEnRnt.Should().Be("Sí, desde mayo de 2023");
        t.TipoPermisoTrabajo.Should().Be("Residencia y trabajo por cuenta ajena");
        t.CaducidadPermisoTrabajo.Should().Be(new DateOnly(2027, 2, 18));
        t.MotivoPermisoTrabajo.Should().Be("Se identifica con NIE");
        t.Nombre.Should().Be("Paula"); t.Dni.Should().Be("12345678Z");
    }

    // Solo se verifica formato, sin consulta ni cálculo del control de la TGSS.
    [Theory]
    [InlineData("41 1234567 89", "41123456789")]
    [InlineData("01/12345678-90", "011234567890")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void La_afiliacion_admite_la_maqueta_y_doce_cifras_con_separadores_y_conserva_ceros(string? entrada, string? esperado)
    {
        var t = Crear(); Rellenar(t, entrada);
        t.NumeroAfiliacionSeguridadSocial.Should().Be(esperado);
    }

    [Theory]
    [InlineData("1234567890")]
    [InlineData("1234567890123")]
    [InlineData("4112345678A")]
    [InlineData("４１１２３４５６７８９")]
    [InlineData("41.1234567.89")]
    public void Rechaza_afiliacion_mal_formada_sin_mutar_la_ficha(string entrada)
    {
        var t = Crear(); Rellenar(t);
        var editar = () => Rellenar(t, entrada, "Otra nacionalidad");
        editar.Should().Throw<ArgumentException>();
        t.NumeroAfiliacionSeguridadSocial.Should().Be("41123456789");
        t.Nacionalidad.Should().Be("Colombiana");
    }

    [Fact]
    public void Los_campos_son_opcionales_y_se_pueden_vaciar()
    {
        var t = Crear(); Rellenar(t);
        t.ActualizarFicha(" ", " ", null, " ", " ", null, null, " ", " ", null, " ");
        ComprobarVacia(t);
    }

    [Fact]
    public void Una_baja_no_puede_ser_anterior_al_alta_y_no_muta_otros_campos()
    {
        var t = Crear(); Rellenar(t);
        var editar = () => t.ActualizarFicha(null, "Otro puesto", null, null, null,
            new(2023, 5, 2), new(2023, 5, 1), null, null, null, null);
        editar.Should().Throw<ArgumentException>();
        t.Puesto.Should().Be("Electricista");
    }

    [Fact]
    public void Los_limites_de_texto_se_validan_antes_de_mutar()
    {
        var t = Crear(); Rellenar(t);
        var editar = () => Rellenar(t, nacionalidad: new string('x', Trabajador.LongitudMaximaCampoFicha + 1));
        editar.Should().Throw<ArgumentException>();
        t.Nacionalidad.Should().Be("Colombiana");
    }

    [Theory]
    [InlineData("puesto")]
    [InlineData("nacionalidad")]
    [InlineData("vinculo")]
    [InlineData("rnt")]
    [InlineData("permiso")]
    [InlineData("motivo")]
    public void Cada_campo_de_texto_admite_su_limite_y_rechaza_una_cifra_mas(string campo)
    {
        var t = Crear();
        void Editar(string texto) => t.ActualizarFicha(null, campo == "puesto" ? texto : null, null,
            campo == "nacionalidad" ? texto : null, campo == "vinculo" ? texto : null, null, null,
            campo == "rnt" ? texto : null, campo == "permiso" ? texto : null, null, campo == "motivo" ? texto : null);
        var limite = campo == "puesto" ? Trabajador.LongitudMaximaPuesto : Trabajador.LongitudMaximaCampoFicha;
        var valido = () => Editar(new string('x', limite));
        valido.Should().NotThrow();
        var invalido = () => Editar(new string('x', limite + 1));
        invalido.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Anonimizar_suprime_la_ficha_y_no_permite_reintroducirla()
    {
        var t = Crear(); Rellenar(t);
        t.Anonimizar(DateTime.UtcNow);
        ComprobarVacia(t);
        var editar = () => Rellenar(t);
        editar.Should().Throw<InvalidOperationException>();
        ComprobarVacia(t);
    }

    private static void ComprobarVacia(Trabajador t)
    {
        t.NumeroAfiliacionSeguridadSocial.Should().BeNull();
        t.Puesto.Should().BeNull(); t.FechaNacimiento.Should().BeNull();
        t.Nacionalidad.Should().BeNull(); t.TipoVinculo.Should().BeNull();
        t.FechaAltaEmpresa.Should().BeNull(); t.FechaBajaEmpresa.Should().BeNull();
        t.FiguraEnRnt.Should().BeNull(); t.TipoPermisoTrabajo.Should().BeNull();
        t.CaducidadPermisoTrabajo.Should().BeNull(); t.MotivoPermisoTrabajo.Should().BeNull();
    }
}
