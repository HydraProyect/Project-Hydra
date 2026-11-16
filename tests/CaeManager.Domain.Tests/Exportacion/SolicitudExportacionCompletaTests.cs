using CaeManager.Domain.Exportacion;
using FluentAssertions;

namespace CaeManager.Domain.Tests.Exportacion;

public class SolicitudExportacionCompletaTests
{
    private static readonly DateTime Ahora = new(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);

    private static SolicitudExportacionCompleta Nueva() => new(Guid.NewGuid(), "protegida", Ahora);

    [Fact]
    public void Lista_caduca_a_los_siete_dias_y_ya_no_guarda_la_contrasena()
    {
        var solicitud = Nueva();
        solicitud.MarcarEnCurso(Ahora);
        solicitud.MarcarLista("t/s.zip", 42, Ahora.AddHours(1));

        solicitud.Estado.Should().Be(EstadoSolicitudExportacionCompleta.Lista);
        solicitud.ContrasenaProtegida.Should().BeNull();
        solicitud.CaducaEnUtc.Should().Be(Ahora.AddHours(1).AddDays(7));
        solicitud.EstaDisponibleParaDescarga(Ahora.AddHours(1).AddDays(7).AddTicks(-1)).Should().BeTrue();
        solicitud.EstaDisponibleParaDescarga(Ahora.AddHours(1).AddDays(7)).Should().BeFalse();
    }

    [Fact]
    public void Fallida_no_guarda_la_contrasena_ni_se_descarga()
    {
        var solicitud = Nueva();
        solicitud.MarcarFallida("GeneracionFallida", Ahora);

        solicitud.ContrasenaProtegida.Should().BeNull();
        solicitud.EstaDisponibleParaDescarga(Ahora).Should().BeFalse();
    }

    [Fact]
    public void Caducada_suelta_el_fichero_y_solo_tras_su_vigencia()
    {
        var solicitud = Nueva();
        solicitud.MarcarEnCurso(Ahora);
        solicitud.MarcarLista("t/s.zip", 42, Ahora);

        var antes = () => solicitud.MarcarCaducada(Ahora.AddDays(6));
        antes.Should().Throw<InvalidOperationException>();

        solicitud.MarcarCaducada(Ahora.AddDays(7));
        solicitud.Estado.Should().Be(EstadoSolicitudExportacionCompleta.Caducada);
        solicitud.UbicacionFichero.Should().BeNull();
    }

    [Fact]
    public void Sustituida_caduca_antes_de_su_vigencia_y_suelta_el_fichero()
    {
        var solicitud = Nueva();
        solicitud.MarcarEnCurso(Ahora);
        solicitud.MarcarLista("t/s.zip", 42, Ahora);

        solicitud.CaducarPorSustitucion(Ahora.AddDays(1));

        solicitud.Estado.Should().Be(EstadoSolicitudExportacionCompleta.Caducada);
        solicitud.UbicacionFichero.Should().BeNull();
        solicitud.FinalizadaEnUtc.Should().Be(Ahora.AddDays(1));
        solicitud.EstaDisponibleParaDescarga(Ahora.AddDays(1)).Should().BeFalse();
    }

    [Fact]
    public void Una_terminal_no_admite_otra_transicion()
    {
        // Las transiciones no repiten estado: es lo que hace del estado un token de concurrencia sin ABA.
        var fallida = Nueva();
        fallida.MarcarFallida("Estancada", Ahora);
        ((Action)(() => fallida.MarcarEnCurso(Ahora))).Should().Throw<InvalidOperationException>();
        ((Action)(() => fallida.MarcarFallida("x", Ahora))).Should().Throw<InvalidOperationException>();

        var caducada = Nueva();
        caducada.MarcarEnCurso(Ahora);
        caducada.MarcarLista("t/s.zip", 42, Ahora);
        caducada.CaducarPorSustitucion(Ahora);
        ((Action)(() => caducada.MarcarLista("t/s.zip", 42, Ahora))).Should().Throw<InvalidOperationException>();
        ((Action)(() => caducada.CaducarPorSustitucion(Ahora))).Should().Throw<InvalidOperationException>();
        ((Action)(() => caducada.MarcarFallida("x", Ahora))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Solo_se_sustituye_una_lista()
    {
        var pendiente = Nueva();
        var sustituirPendiente = () => pendiente.CaducarPorSustitucion(Ahora);
        sustituirPendiente.Should().Throw<InvalidOperationException>();

        pendiente.MarcarEnCurso(Ahora);
        var sustituirEnCurso = () => pendiente.CaducarPorSustitucion(Ahora);
        sustituirEnCurso.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void No_se_salta_estados()
    {
        var solicitud = Nueva();
        var lista = () => solicitud.MarcarLista("t/s.zip", 42, Ahora);
        lista.Should().Throw<InvalidOperationException>("solo se marca lista una solicitud en curso");

        solicitud.MarcarEnCurso(Ahora);
        solicitud.MarcarLista("t/s.zip", 42, Ahora);
        var fallar = () => solicitud.MarcarFallida("x", Ahora);
        fallar.Should().Throw<InvalidOperationException>("una exportación lista ya no falla");
    }
}
