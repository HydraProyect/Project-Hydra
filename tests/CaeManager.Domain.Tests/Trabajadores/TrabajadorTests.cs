using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Trabajadores;

public class TrabajadorTests
{
    [Fact]
    public void Crea_un_trabajador_de_empresa_valido_y_normaliza_el_dni_a_mayusculas()
    {
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Alvaro", "Sanchez Martin", "77189989b");

        trabajador.Dni.Should().Be("77189989B");
        trabajador.NombreCompleto.Should().Be("Alvaro Sanchez Martin");
        trabajador.EmpresaId.Should().NotBeNull();
        trabajador.SubcontrataId.Should().BeNull();
        trabajador.EsDeSubcontrata.Should().BeFalse();
    }

    [Fact]
    public void Crea_un_trabajador_de_subcontrata_valido()
    {
        var trabajador = Trabajador.DeSubcontrata(Guid.NewGuid(), "Alvaro", "Sanchez Martin", "77189989b");

        trabajador.SubcontrataId.Should().NotBeNull();
        trabajador.EmpresaId.Should().BeNull();
        trabajador.EsDeSubcontrata.Should().BeTrue();
    }

    [Theory]
    [InlineData("77189989A")] // formato de DNI, dígito de control incorrecto
    [InlineData("X1234567A")] // formato de NIE, dígito de control incorrecto
    [InlineData("ABCD")] // demasiado corto para ser un documento real
    public void Rechaza_un_documento_con_digito_de_control_invalido_o_demasiado_corto(string dniInvalido)
    {
        var accion = () => Trabajador.DeEmpresa(Guid.NewGuid(), "Alvaro", "Sanchez Martin", dniInvalido);

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("X1234567L")] // NIE válido — extranjero residente
    [InlineData("AAA123456")] // número de soporte TIE
    [InlineData("123456789")] // pasaporte extranjero numérico — sin formato español, se acepta
    public void Acepta_documentos_de_identidad_no_espanoles_o_sin_formato_de_dni(string documentoValido)
    {
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Alvaro", "Sanchez Martin", documentoValido);

        trabajador.Dni.Should().Be(documentoValido);
    }

    [Fact]
    public void Requiere_una_empresa_valida()
    {
        var accion = () => Trabajador.DeEmpresa(Guid.Empty, "Alvaro", "Sanchez Martin", "77189989B");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Requiere_una_subcontrata_valida()
    {
        var accion = () => Trabajador.DeSubcontrata(Guid.Empty, "Alvaro", "Sanchez Martin", "77189989B");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Crea_un_trabajador_con_puesto_y_lo_recorta()
    {
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Alvaro", "Sanchez Martin", "77189989B", puesto: "  Soldador  ");

        trabajador.Puesto.Should().Be("Soldador");
    }

    [Fact]
    public void El_puesto_es_opcional()
    {
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Alvaro", "Sanchez Martin", "77189989B");

        trabajador.Puesto.Should().BeNull();
    }

    [Fact]
    public void Rechaza_un_puesto_que_supera_la_longitud_maxima()
    {
        var puestoDemasiadoLargo = new string('A', Trabajador.LongitudMaximaPuesto + 1);

        var accion = () => Trabajador.DeEmpresa(Guid.NewGuid(), "Alvaro", "Sanchez Martin", "77189989B", puesto: puestoDemasiadoLargo);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Actualizar_cambia_el_puesto()
    {
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Alvaro", "Sanchez Martin", "77189989B", puesto: "Soldador");

        trabajador.Actualizar("Alvaro", "Sanchez Martin", null, null, null, null, puesto: "Administrativo");

        trabajador.Puesto.Should().Be("Administrativo");
    }

    [Fact]
    public void Anonimizar_borra_el_puesto()
    {
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Alvaro", "Sanchez Martin", "77189989B", puesto: "Soldador");

        trabajador.Anonimizar(DateTime.UtcNow);

        trabajador.Puesto.Should().BeNull();
    }
}
