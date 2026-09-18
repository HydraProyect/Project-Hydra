using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Empresas;

public class EmpresaTests
{
    private const string CifValido = "B12345674";

    [Fact]
    public void Crea_una_empresa_sin_cif()
    {
        var empresa = new Empresa("Limpiezas del Norte S.L.");

        empresa.RazonSocial.Should().Be("Limpiezas del Norte S.L.");
        empresa.Cif.Should().BeNull();
        empresa.EstaEliminado.Should().BeFalse();
    }

    [Fact]
    public void Crea_una_empresa_con_cif_valido()
    {
        var empresa = new Empresa("Limpiezas del Norte S.L.", CifValido);

        empresa.Cif.Should().Be(CifValido);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void No_permite_crear_una_empresa_sin_razon_social(string razonSocialInvalida)
    {
        var accion = () => new Empresa(razonSocialInvalida, CifValido);

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("B12345670")] // formato de CIF, dígito de control incorrecto
    [InlineData("77189989A")] // formato de DNI, letra de control incorrecta — la buena es B
    [InlineData("X1234567A")] // formato de NIE, letra de control incorrecta
    [InlineData("AAA123456")] // número de soporte de TIE: no es una identificación fiscal
    [InlineData("123456789")] // pasaporte extranjero: fuera del régimen español
    public void No_permite_un_cif_invalido_si_se_proporciona(string cifInvalido)
    {
        var accion = () => new Empresa("Limpiezas del Norte S.L.", cifInvalido);

        accion.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Un autónomo es una Empresa cuya identificación fiscal es su DNI o su
    /// NIE: exigirle un NIF de persona jurídica le impedía darse de alta.
    /// </summary>
    [Theory]
    [InlineData("77189989B")] // DNI
    [InlineData("12345678Z")] // DNI
    [InlineData("X1234567L")] // NIE — extranjero residente
    [InlineData("Y2345678Z")] // NIE
    public void Crea_una_empresa_de_un_autonomo_identificada_con_su_dni_o_nie(string documento)
    {
        var empresa = new Empresa("Marta Ruiz Salas", documento);

        empresa.Cif.Should().Be(documento);
    }

    [Fact]
    public void Un_autonomo_tambien_puede_ser_la_contraparte_cliente()
    {
        var empresa = Empresa.CrearComoCliente(
            "Marta Ruiz Salas", "77189989B", esCritico: false, notas: null, ejecutivoUsuarioId: null);

        empresa.Cif.Should().Be("77189989B");
    }

    [Fact]
    public void Un_autonomo_tambien_puede_ser_la_contraparte_subcontratista()
    {
        var empresa = Empresa.CrearComoSubcontrata("Marta Ruiz Salas", "X1234567L", nivelServicio: "Gestionada");

        empresa.Cif.Should().Be("X1234567L");
    }

    [Fact]
    public void Normaliza_a_mayusculas_el_dni_de_un_autonomo()
    {
        var empresa = new Empresa("Marta Ruiz Salas", "  77189989b  ");

        empresa.Cif.Should().Be("77189989B");
    }

    [Fact]
    public void Actualizar_permite_quitar_el_cif()
    {
        var empresa = new Empresa("Limpiezas del Norte S.L.", CifValido);

        empresa.Actualizar("Limpiezas del Norte S.L.", cif: null);

        empresa.Cif.Should().BeNull();
    }

    [Fact]
    public void Actualizar_normaliza_el_cif_a_mayusculas()
    {
        var empresa = new Empresa("Limpiezas del Norte S.L.");

        empresa.Actualizar("Limpiezas del Norte S.L.", cif: CifValido.ToLowerInvariant());

        empresa.Cif.Should().Be(CifValido);
    }

    [Fact]
    public void Crea_una_empresa_con_cnae_convenio_y_actividad_anexo_i()
    {
        var empresa = new Empresa(
            "Limpiezas del Norte S.L.", CifValido, cnae: "4321",
            convenioAplicable: "Convenio Estatal de la Industria, las Nuevas Tecnologías y los Servicios del Sector del Metal",
            esActividadAnexoI: true);

        empresa.Cnae.Should().Be("4321");
        empresa.ConvenioAplicable.Should().Be("Convenio Estatal de la Industria, las Nuevas Tecnologías y los Servicios del Sector del Metal");
        empresa.EsActividadAnexoI.Should().BeTrue();
    }

    [Fact]
    public void Cnae_y_convenio_aplicable_son_opcionales()
    {
        var empresa = new Empresa("Limpiezas del Norte S.L.");

        empresa.Cnae.Should().BeNull();
        empresa.ConvenioAplicable.Should().BeNull();
        empresa.EsActividadAnexoI.Should().BeFalse();
    }

    [Fact]
    public void Rechaza_un_cnae_que_supera_la_longitud_maxima()
    {
        var cnaeDemasiadoLargo = new string('4', Empresa.LongitudMaximaCnae + 1);

        var accion = () => new Empresa("Limpiezas del Norte S.L.", cnae: cnaeDemasiadoLargo);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Actualizar_cambia_cnae_convenio_y_actividad_anexo_i()
    {
        var empresa = new Empresa("Limpiezas del Norte S.L.");

        empresa.Actualizar("Limpiezas del Norte S.L.", cif: null, cnae: "4321", convenioAplicable: "Convenio del Metal", esActividadAnexoI: true);

        empresa.Cnae.Should().Be("4321");
        empresa.ConvenioAplicable.Should().Be("Convenio del Metal");
        empresa.EsActividadAnexoI.Should().BeTrue();
    }
}
