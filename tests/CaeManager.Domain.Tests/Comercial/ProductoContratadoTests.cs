using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Comercial;

public class ProductoContratadoTests
{
    private const string Pagador = "cus_PagadorDePrueba";
    private static readonly DateTime Desde = new(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);

    private static ProductoContratado Contratar(string pagador = Pagador, DateTime? desde = null) =>
        ProductoContratado.Contratar(
            LineaProductoContratado.OutboundGestionPropia, pagador, TramoTrabajadoresActivos.De11A40,
            ModalidadSuscripcionAlta.Anual, desde ?? Desde);

    [Fact]
    public void Contratar_deja_lo_contratado_vigente_y_sin_fecha_de_fin()
    {
        var producto = Contratar();

        producto.Linea.Should().Be(LineaProductoContratado.OutboundGestionPropia);
        producto.PagadorStripeCustomerId.Should().Be(Pagador);
        producto.Tramo.Should().Be(TramoTrabajadoresActivos.De11A40);
        producto.Modalidad.Should().Be(ModalidadSuscripcionAlta.Anual);
        producto.VigenteDesdeUtc.Should().Be(Desde);
        producto.VigenteHastaUtc.Should().BeNull();
        producto.Estado.Should().Be(EstadoProductoContratado.Vigente);
        producto.TenantId.Should().Be(Guid.Empty, "el Tenant beneficiario lo sella el interceptor al guardar, nunca el dominio");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" cus_conEspacio")]
    [InlineData("cus_conEspacio ")]
    public void El_Pagador_TALVEG_no_admite_vacio_ni_espacios_en_los_extremos(string pagador)
    {
        var contratar = () => Contratar(pagador);

        contratar.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("pagadorStripeCustomerId");
    }

    [Fact]
    public void El_Pagador_TALVEG_no_pasa_de_la_longitud_de_la_columna()
    {
        Contratar(new string('c', ProductoContratado.LongitudMaximaPagadorStripeCustomerId))
            .PagadorStripeCustomerId.Should().HaveLength(ProductoContratado.LongitudMaximaPagadorStripeCustomerId, "control: el máximo cabe");

        var contratar = () => Contratar(new string('c', ProductoContratado.LongitudMaximaPagadorStripeCustomerId + 1));

        contratar.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("pagadorStripeCustomerId");
    }

    [Fact]
    public void Rechaza_valores_fuera_de_las_enumeraciones()
    {
        var linea = () => ProductoContratado.Contratar((LineaProductoContratado)99, Pagador,
            TramoTrabajadoresActivos.De1A10, ModalidadSuscripcionAlta.Mensual, Desde);
        var tramo = () => ProductoContratado.Contratar(LineaProductoContratado.OutboundGestionPropia, Pagador,
            (TramoTrabajadoresActivos)99, ModalidadSuscripcionAlta.Mensual, Desde);
        var modalidad = () => ProductoContratado.Contratar(LineaProductoContratado.OutboundGestionPropia, Pagador,
            TramoTrabajadoresActivos.De1A10, (ModalidadSuscripcionAlta)99, Desde);

        linea.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("linea");
        tramo.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("tramo");
        modalidad.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("modalidad");
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Las_fechas_de_vigencia_van_en_UTC(DateTimeKind tipo)
    {
        var fecha = DateTime.SpecifyKind(Desde, tipo);

        var contratar = () => Contratar(desde: fecha);
        var finalizar = () => Contratar().Finalizar(fecha.AddDays(1));
        var consultar = () => Contratar().EstaVigenteEn(fecha);

        contratar.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("vigenteDesdeUtc");
        finalizar.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("vigenteHastaUtc");
        consultar.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("instanteUtc");
    }

    [Fact]
    public void Finalizar_fija_el_fin_y_deja_el_estado_final()
    {
        var producto = Contratar();
        var hasta = Desde.AddMonths(12);

        producto.Finalizar(hasta);

        producto.Estado.Should().Be(EstadoProductoContratado.Finalizado);
        producto.VigenteHastaUtc.Should().Be(hasta);
    }

    [Fact]
    public void Finalizar_admite_un_fin_igual_al_inicio_pero_no_anterior()
    {
        var igual = Contratar();
        igual.Finalizar(Desde);
        igual.Estado.Should().Be(EstadoProductoContratado.Finalizado);

        var anterior = Contratar();
        var finalizar = () => anterior.Finalizar(Desde.AddTicks(-1));

        finalizar.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("vigenteHastaUtc");
        anterior.Estado.Should().Be(EstadoProductoContratado.Vigente, "un rechazo no cambia nada");
        anterior.VigenteHastaUtc.Should().BeNull();
    }

    [Fact]
    public void Un_Producto_Contratado_finalizado_no_se_vuelve_a_finalizar()
    {
        var producto = Contratar();
        var hasta = Desde.AddDays(30);
        producto.Finalizar(hasta);

        var otraVez = () => producto.Finalizar(hasta.AddDays(1));

        otraVez.Should().Throw<TransicionProductoContratadoNoPermitidaException>()
            .Which.Desde.Should().Be(EstadoProductoContratado.Finalizado);
        producto.VigenteHastaUtc.Should().Be(hasta, "el fin ya fijado no se mueve");
    }

    [Fact]
    public void La_vigencia_incluye_el_inicio_y_excluye_el_fin()
    {
        var producto = Contratar();

        producto.EstaVigenteEn(Desde.AddTicks(-1)).Should().BeFalse();
        producto.EstaVigenteEn(Desde).Should().BeTrue();
        producto.EstaVigenteEn(Desde.AddYears(5)).Should().BeTrue("vigente y sin fecha de fin");

        var hasta = Desde.AddMonths(1);
        producto.Finalizar(hasta);

        producto.EstaVigenteEn(hasta.AddTicks(-1)).Should().BeTrue("antes del fin seguía en vigor");
        producto.EstaVigenteEn(hasta).Should().BeFalse();
    }

    [Fact]
    public void Lo_contratado_no_tiene_setters_publicos()
    {
        typeof(ProductoContratado).GetProperties()
            .Where(p => p.DeclaringType == typeof(ProductoContratado))
            .Should().OnlyContain(p => p.SetMethod == null || !p.SetMethod.IsPublic,
                "línea, Pagador TALVEG, tramo, modalidad y vigencia solo cambian por el comportamiento del dominio");
    }
}
