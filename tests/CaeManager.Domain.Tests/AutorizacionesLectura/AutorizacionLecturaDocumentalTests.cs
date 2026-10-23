using CaeManager.Domain.AutorizacionesLectura;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.AutorizacionesLectura;

public class AutorizacionLecturaDocumentalTests
{
    private static readonly DateOnly Desde = new(2026, 3, 1);
    private static readonly DateOnly Hasta = new(2026, 3, 31);
    private static readonly DateTime Ahora = new(2026, 2, 20, 9, 0, 0, DateTimeKind.Utc);

    private static AutorizacionLecturaDocumental Autorizacion(DateOnly? hasta = null) =>
        AutorizacionLecturaDocumental.Conceder(Guid.NewGuid(), Guid.NewGuid(), true, Desde, hasta, Guid.NewGuid(), Ahora);

    [Fact]
    public void Conceder_conserva_lo_concedido_y_nace_sin_revocar()
    {
        var destinatario = Guid.NewGuid();
        var contrato = Guid.NewGuid();
        var usuario = Guid.NewGuid();

        var autorizacion = AutorizacionLecturaDocumental.Conceder(destinatario, contrato, true, Desde, Hasta, usuario, Ahora);

        autorizacion.TenantDestinatarioId.Should().Be(destinatario);
        autorizacion.ContratoAccesoId.Should().Be(contrato);
        autorizacion.IncluyeDocumentosEmpresa.Should().BeTrue();
        autorizacion.VigenciaDesde.Should().Be(Desde);
        autorizacion.VigenciaHasta.Should().Be(Hasta);
        autorizacion.ConcedidaPorUsuarioId.Should().Be(usuario);
        autorizacion.ConcedidaEnUtc.Should().Be(Ahora);
        autorizacion.EstaRevocada.Should().BeFalse();
        autorizacion.RevocadaEnUtc.Should().BeNull();
        autorizacion.RevocadaPorUsuarioId.Should().BeNull();
    }

    [Fact]
    public void Conceder_exige_destinatario_contrato_y_quien_concede()
    {
        FluentActions.Invoking(() => AutorizacionLecturaDocumental.Conceder(Guid.Empty, Guid.NewGuid(), false, Desde, null, Guid.NewGuid(), Ahora))
            .Should().Throw<ArgumentException>().WithParameterName("tenantDestinatarioId");
        FluentActions.Invoking(() => AutorizacionLecturaDocumental.Conceder(Guid.NewGuid(), Guid.Empty, false, Desde, null, Guid.NewGuid(), Ahora))
            .Should().Throw<ArgumentException>().WithParameterName("contratoAccesoId");
        FluentActions.Invoking(() => AutorizacionLecturaDocumental.Conceder(Guid.NewGuid(), Guid.NewGuid(), false, Desde, null, Guid.Empty, Ahora))
            .Should().Throw<ArgumentException>().WithParameterName("concedidaPorUsuarioId");
    }

    [Fact]
    public void La_vigencia_no_puede_terminar_antes_de_empezar_pero_si_el_mismo_dia()
    {
        FluentActions.Invoking(() => Autorizacion(Desde.AddDays(-1)))
            .Should().Throw<ArgumentException>().WithParameterName("vigenciaHasta");

        Autorizacion(Desde).VigenciaHasta.Should().Be(Desde, "una autorización de un solo día es válida");
    }

    [Theory]
    [InlineData(-1, false)] // el día antes de empezar
    [InlineData(0, true)]   // el primer día
    [InlineData(30, true)]  // el último día
    [InlineData(31, false)] // el día después del último
    public void VigenteEn_cuenta_el_primer_y_el_ultimo_dia_como_vigentes(int diasDesdeElInicio, bool vigente)
    {
        var autorizacion = Autorizacion(Hasta);
        var fecha = Desde.AddDays(diasDesdeElInicio);

        autorizacion.VigenteEn(fecha).Should().Be(vigente);
        CoincideConLaExpresion(autorizacion, fecha);
    }

    [Fact]
    public void VigenteEn_sin_fecha_de_fin_sigue_vigente_en_un_futuro_lejano()
    {
        var autorizacion = Autorizacion();
        var lejano = new DateOnly(2099, 12, 31);

        autorizacion.VigenteEn(lejano).Should().BeTrue();
        CoincideConLaExpresion(autorizacion, lejano);
    }

    [Fact]
    public void Una_autorizacion_revocada_no_esta_vigente_ni_dentro_de_su_periodo()
    {
        var autorizacion = Autorizacion(Hasta);

        autorizacion.Revocar(Guid.NewGuid(), Ahora);

        autorizacion.VigenteEn(Desde).Should().BeFalse();
        CoincideConLaExpresion(autorizacion, Desde);
    }

    [Fact]
    public void Revocar_anota_quien_y_cuando_y_una_segunda_vez_lanza_sin_tocarla()
    {
        var autorizacion = Autorizacion();
        var quien = Guid.NewGuid();

        autorizacion.Revocar(quien, Ahora);

        autorizacion.EstaRevocada.Should().BeTrue();
        autorizacion.RevocadaPorUsuarioId.Should().Be(quien);
        autorizacion.RevocadaEnUtc.Should().Be(Ahora);

        FluentActions.Invoking(() => autorizacion.Revocar(Guid.NewGuid(), Ahora.AddHours(1)))
            .Should().Throw<InvalidOperationException>("la revocación es definitiva");
        autorizacion.RevocadaPorUsuarioId.Should().Be(quien);
        autorizacion.RevocadaEnUtc.Should().Be(Ahora);
    }

    [Fact]
    public void Revocar_exige_autor()
    {
        FluentActions.Invoking(() => Autorizacion().Revocar(Guid.Empty, Ahora))
            .Should().Throw<ArgumentException>().WithParameterName("revocadaPorUsuarioId");
    }

    [Fact]
    public void AsignarTrabajador_crea_la_fila_de_la_autorizacion_y_renueva_su_version()
    {
        var autorizacion = Autorizacion();
        var version = autorizacion.Version;
        var trabajador = Guid.NewGuid();

        var fila = autorizacion.AsignarTrabajador(trabajador);

        fila.AutorizacionId.Should().Be(autorizacion.Id);
        fila.TrabajadorId.Should().Be(trabajador);
        autorizacion.Version.Should().NotBe(version, "el cambio vive en otra tabla y tiene que entrar en el token de concurrencia");

        FluentActions.Invoking(() => autorizacion.AsignarTrabajador(Guid.Empty))
            .Should().Throw<ArgumentException>().WithParameterName("trabajadorId");
    }

    [Fact]
    public void Una_autorizacion_revocada_no_admite_asignar_ni_retirar_Trabajadores()
    {
        var autorizacion = Autorizacion();
        var fila = autorizacion.AsignarTrabajador(Guid.NewGuid());
        autorizacion.Revocar(Guid.NewGuid(), Ahora);
        var version = autorizacion.Version;

        FluentActions.Invoking(() => autorizacion.AsignarTrabajador(Guid.NewGuid())).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => autorizacion.RetirarTrabajador(fila)).Should().Throw<InvalidOperationException>();
        autorizacion.Version.Should().Be(version, "un cambio rechazado no toca la autorización");
    }

    [Fact]
    public void RetirarTrabajador_solo_acepta_filas_de_la_propia_autorizacion()
    {
        var autorizacion = Autorizacion();
        var ajena = Autorizacion().AsignarTrabajador(Guid.NewGuid());
        var propia = autorizacion.AsignarTrabajador(Guid.NewGuid());
        var version = autorizacion.Version;

        FluentActions.Invoking(() => autorizacion.RetirarTrabajador(ajena))
            .Should().Throw<ArgumentException>().WithParameterName("trabajador");

        autorizacion.RetirarTrabajador(propia);
        autorizacion.Version.Should().NotBe(version);
    }

    /// <summary>La expresión que traducen las consultas y el método en memoria son el mismo criterio.</summary>
    private static void CoincideConLaExpresion(AutorizacionLecturaDocumental autorizacion, DateOnly fecha) =>
        AutorizacionLecturaDocumental.EstaVigenteEn(fecha).Compile()(autorizacion).Should().Be(autorizacion.VigenteEn(fecha),
            "EstaVigenteEn y VigenteEn tienen que decir lo mismo el {0}", fecha);
}
