using CaeManager.Domain.ContratosAcceso;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.ContratosAcceso;

public class ContratoAccesoTests
{
    private static readonly DateOnly Inicio = new(2026, 3, 1);
    private static readonly DateOnly Fin = new(2026, 3, 31);

    private static ContratoAcceso Contrato(DateOnly? fin = null, string? notas = null) =>
        ContratoAcceso.Crear(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Inicio, fin, notas);

    [Fact]
    public void Crear_conserva_las_coordenadas_y_nace_abierto()
    {
        var cliente = Guid.NewGuid();
        var centro = Guid.NewGuid();
        var contratista = Guid.NewGuid();

        var contrato = ContratoAcceso.Crear(cliente, centro, contratista, Inicio, Fin, "Mantenimiento anual");

        contrato.ClienteEmpresarialId.Should().Be(cliente);
        contrato.CentroId.Should().Be(centro);
        contrato.EmpresaContratistaId.Should().Be(contratista);
        contrato.FechaInicio.Should().Be(Inicio);
        contrato.FechaFin.Should().Be(Fin);
        contrato.Notas.Should().Be("Mantenimiento anual");
        contrato.EstaAbierto.Should().BeTrue();
        contrato.FechaCierreReal.Should().BeNull();
    }

    [Fact]
    public void Crear_exige_las_tres_coordenadas()
    {
        var id = Guid.NewGuid();

        FluentActions.Invoking(() => ContratoAcceso.Crear(Guid.Empty, id, Guid.NewGuid(), Inicio, null, null))
            .Should().Throw<ArgumentException>().WithParameterName("clienteEmpresarialId");
        FluentActions.Invoking(() => ContratoAcceso.Crear(id, Guid.Empty, Guid.NewGuid(), Inicio, null, null))
            .Should().Throw<ArgumentException>().WithParameterName("centroId");
        FluentActions.Invoking(() => ContratoAcceso.Crear(id, Guid.NewGuid(), Guid.Empty, Inicio, null, null))
            .Should().Throw<ArgumentException>().WithParameterName("empresaContratistaId");
    }

    [Fact]
    public void La_Empresa_contratista_no_puede_ser_el_propio_Cliente_empresarial()
    {
        var cliente = Guid.NewGuid();

        FluentActions.Invoking(() => ContratoAcceso.Crear(cliente, Guid.NewGuid(), cliente, Inicio, null, null))
            .Should().Throw<ArgumentException>().WithParameterName("empresaContratistaId");
    }

    [Fact]
    public void El_fin_no_puede_ser_anterior_al_inicio_al_crear_ni_al_actualizar()
    {
        FluentActions.Invoking(() => Contrato(Inicio.AddDays(-1)))
            .Should().Throw<ArgumentException>().WithParameterName("fechaFin");

        var contrato = Contrato(Fin);
        FluentActions.Invoking(() => contrato.Actualizar(Inicio.AddDays(-1), null))
            .Should().Throw<ArgumentException>().WithParameterName("fechaFin");
        contrato.FechaFin.Should().Be(Fin, "un cambio rechazado no toca el contrato");

        // El mismo día de inicio sí vale: un contrato de un día.
        Contrato(Inicio).FechaFin.Should().Be(Inicio);
    }

    [Fact]
    public void Las_notas_no_pasan_del_maximo()
    {
        var limite = new string('a', ContratoAcceso.LongitudMaximaNotas);
        Contrato(notas: limite).Notas.Should().Be(limite);

        FluentActions.Invoking(() => Contrato(notas: limite + "a"))
            .Should().Throw<ArgumentException>().WithParameterName("notas");
        FluentActions.Invoking(() => Contrato().Actualizar(null, limite + "a"))
            .Should().Throw<ArgumentException>().WithParameterName("notas");
    }

    [Fact]
    public void Actualizar_cambia_el_fin_y_las_notas()
    {
        var contrato = Contrato(Fin, "Antes");

        contrato.Actualizar(null, "Después");

        contrato.FechaFin.Should().BeNull();
        contrato.Notas.Should().Be("Después");
    }

    [Theory]
    [InlineData(-1, false)] // el día antes del inicio
    [InlineData(0, true)]   // el día de inicio
    [InlineData(30, true)]  // el día de fin
    [InlineData(31, false)] // el día después del fin
    public void VigenteEn_cuenta_el_inicio_y_el_fin_como_vigentes(int diasDesdeInicio, bool vigente)
    {
        var contrato = Contrato(Fin);
        var fecha = Inicio.AddDays(diasDesdeInicio);

        contrato.VigenteEn(fecha).Should().Be(vigente);
        CoincideConLaExpresion(contrato, fecha);
    }

    [Fact]
    public void VigenteEn_con_fin_indefinido_sigue_vigente_en_un_futuro_lejano()
    {
        var contrato = Contrato();
        var lejano = new DateOnly(2099, 12, 31);

        contrato.VigenteEn(lejano).Should().BeTrue();
        contrato.VigenteEn(Inicio.AddDays(-1)).Should().BeFalse();
        CoincideConLaExpresion(contrato, lejano);
    }

    [Fact]
    public void VigenteEn_cuenta_el_dia_de_cierre_y_no_el_siguiente_aunque_el_fin_contratado_no_haya_llegado()
    {
        var contrato = Contrato(Fin);
        var cierre = Inicio.AddDays(10);

        contrato.Cerrar(cierre);

        contrato.VigenteEn(cierre).Should().BeTrue();
        contrato.VigenteEn(cierre.AddDays(1)).Should().BeFalse();
        contrato.VigenteEn(Fin).Should().BeFalse();
        CoincideConLaExpresion(contrato, cierre);
        CoincideConLaExpresion(contrato, cierre.AddDays(1));
    }

    [Fact]
    public void Un_contrato_eliminado_no_esta_vigente()
    {
        var contrato = Contrato();

        contrato.MarcarComoEliminado(Guid.NewGuid());

        contrato.VigenteEn(Inicio).Should().BeFalse();
        CoincideConLaExpresion(contrato, Inicio);
    }

    [Fact]
    public void Cerrar_dos_veces_lanza()
    {
        var contrato = Contrato();
        contrato.Cerrar(Inicio.AddDays(5));

        FluentActions.Invoking(() => contrato.Cerrar(Inicio.AddDays(6)))
            .Should().Throw<InvalidOperationException>();
        contrato.FechaCierreReal.Should().Be(Inicio.AddDays(5));
    }

    [Fact]
    public void Cerrar_antes_del_inicio_lanza()
    {
        var contrato = Contrato();

        FluentActions.Invoking(() => contrato.Cerrar(Inicio.AddDays(-1)))
            .Should().Throw<ArgumentException>().WithParameterName("fechaCierre");
        contrato.EstaAbierto.Should().BeTrue();

        // El mismo día de inicio sí vale.
        contrato.Cerrar(Inicio);
        contrato.FechaCierreReal.Should().Be(Inicio);
    }

    [Fact]
    public void Reabrir_deshace_el_cierre_y_devuelve_la_vigencia_del_fin_contratado()
    {
        var contrato = Contrato(Fin);
        contrato.Cerrar(Inicio.AddDays(3));

        contrato.Reabrir();

        contrato.EstaAbierto.Should().BeTrue();
        contrato.FechaCierreReal.Should().BeNull();
        contrato.VigenteEn(Fin).Should().BeTrue();
    }

    [Fact]
    public void Reabrir_un_contrato_abierto_lanza()
    {
        FluentActions.Invoking(() => Contrato().Reabrir()).Should().Throw<InvalidOperationException>();
    }

    /// <summary>La expresión que traducen las consultas y el método en memoria son el mismo criterio.</summary>
    private static void CoincideConLaExpresion(ContratoAcceso contrato, DateOnly fecha) =>
        ContratoAcceso.EstaVigenteEn(fecha).Compile()(contrato).Should().Be(contrato.VigenteEn(fecha),
            "EstaVigenteEn y VigenteEn tienen que decir lo mismo el {0}", fecha);
}
