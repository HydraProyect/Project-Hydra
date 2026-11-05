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
            ModalidadSuscripcionAlta.Anual, desde ?? Desde, Ahora);

    private static readonly DateTime Ahora = Desde.AddYears(10);

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
    [InlineData("cus_con espacio")]
    [InlineData("cus\tTabulador")]
    [InlineData("cus-guion")]
    [InlineData("cus_ñ")]
    [InlineData("cus_X\n")]
    public void El_Pagador_TALVEG_solo_admite_el_alfabeto_de_los_Id_de_Stripe(string pagador)
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
            TramoTrabajadoresActivos.De1A10, ModalidadSuscripcionAlta.Mensual, Desde, Desde);
        var tramo = () => ProductoContratado.Contratar(LineaProductoContratado.OutboundGestionPropia, Pagador,
            (TramoTrabajadoresActivos)99, ModalidadSuscripcionAlta.Mensual, Desde, Desde);
        var modalidad = () => ProductoContratado.Contratar(LineaProductoContratado.OutboundGestionPropia, Pagador,
            TramoTrabajadoresActivos.De1A10, (ModalidadSuscripcionAlta)99, Desde, Desde);

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
        var finalizar = () => Contratar().Finalizar(fecha.AddDays(1), Desde.AddYears(1));
        var ahoraNoUtc = () => Contratar().Finalizar(Desde.AddDays(1), fecha.AddYears(1));
        var consultar = () => Contratar().EstaVigenteEn(fecha);

        contratar.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("vigenteDesdeUtc");
        finalizar.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("vigenteHastaUtc");
        ahoraNoUtc.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("ahoraUtc");
        consultar.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("instanteUtc");
    }

    [Fact]
    public void Finalizar_fija_el_fin_y_deja_el_estado_final()
    {
        var producto = Contratar();
        var hasta = Desde.AddMonths(12);

        producto.Finalizar(hasta, ahoraUtc: hasta);

        producto.Estado.Should().Be(EstadoProductoContratado.Finalizado);
        producto.VigenteHastaUtc.Should().Be(hasta);
    }

    [Fact]
    public void Finalizar_admite_un_fin_igual_al_inicio_pero_no_anterior()
    {
        var igual = Contratar();
        igual.Finalizar(Desde, ahoraUtc: Desde);
        igual.Estado.Should().Be(EstadoProductoContratado.Finalizado);

        var anterior = Contratar();
        var finalizar = () => anterior.Finalizar(Desde.AddTicks(-1), ahoraUtc: Desde);

        finalizar.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("vigenteHastaUtc");
        anterior.Estado.Should().Be(EstadoProductoContratado.Vigente, "un rechazo no cambia nada");
        anterior.VigenteHastaUtc.Should().BeNull();
    }

    [Fact]
    public void El_inicio_se_registra_cuando_ha_ocurrido_y_nunca_por_adelantado()
    {
        var ahora = Desde;
        Contratar(desde: ahora.AddDays(-1)).EstaVigenteEn(ahora).Should().BeTrue("inicio pasado");

        var simultaneo = ProductoContratado.Contratar(LineaProductoContratado.OutboundGestionPropia, Pagador,
            TramoTrabajadoresActivos.De1A10, ModalidadSuscripcionAlta.Mensual, ahora, ahora);
        simultaneo.Estado.Should().Be(EstadoProductoContratado.Vigente);
        simultaneo.EstaVigenteEn(ahora).Should().BeTrue("el inicio incluye su instante");

        var futuro = () => ProductoContratado.Contratar(LineaProductoContratado.OutboundGestionPropia, Pagador,
            TramoTrabajadoresActivos.De1A10, ModalidadSuscripcionAlta.Mensual, ahora.AddTicks(1), ahora);
        futuro.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("vigenteDesdeUtc");

        var ahoraNoUtc = () => ProductoContratado.Contratar(LineaProductoContratado.OutboundGestionPropia, Pagador,
            TramoTrabajadoresActivos.De1A10, ModalidadSuscripcionAlta.Mensual, ahora, DateTime.SpecifyKind(ahora, DateTimeKind.Unspecified));
        ahoraNoUtc.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("ahoraUtc");
    }

    [Fact]
    public void El_fin_se_registra_cuando_ha_ocurrido_y_nunca_por_adelantado()
    {
        var ahora = Desde.AddMonths(3);

        var futuro = Contratar();
        var adelantado = () => futuro.Finalizar(ahora.AddTicks(1), ahora);
        adelantado.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("vigenteHastaUtc");
        futuro.Estado.Should().Be(EstadoProductoContratado.Vigente, "sigue en vigor hasta que el fin ocurra");
        futuro.EstaVigenteEn(ahora).Should().BeTrue();

        var simultaneo = Contratar();
        simultaneo.Finalizar(ahora, ahora);
        simultaneo.Estado.Should().Be(EstadoProductoContratado.Finalizado);
        simultaneo.EstaVigenteEn(ahora).Should().BeFalse("el fin excluye su instante");

        var pasado = Contratar();
        pasado.Finalizar(ahora.AddDays(-1), ahora);
        pasado.Estado.Should().Be(EstadoProductoContratado.Finalizado);
        pasado.EstaVigenteEn(ahora.AddDays(-1).AddTicks(-1)).Should().BeTrue();
        pasado.EstaVigenteEn(ahora).Should().BeFalse("Finalizado y no vigente ahora coinciden");
    }

    [Fact]
    public void Un_Producto_Contratado_finalizado_no_se_vuelve_a_finalizar()
    {
        var producto = Contratar();
        var hasta = Desde.AddDays(30);
        producto.Finalizar(hasta, ahoraUtc: hasta);

        var otraVez = () => producto.Finalizar(hasta.AddDays(1), ahoraUtc: hasta.AddDays(1));

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
        producto.Finalizar(hasta, ahoraUtc: hasta);

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
