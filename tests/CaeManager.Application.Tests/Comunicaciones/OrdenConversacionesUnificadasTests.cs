using CaeManager.Application.Comunicaciones.VistaUnificada;
using FluentAssertions;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// El orden de la fusión en memoria de la vista unificada (D-6 b, § 4.3, M4) tiene que coincidir con el de PostgreSQL:
/// fecha DESC y, a igualdad, <c>uuid</c> DESC comparando los bytes en el orden en que se escriben.
/// </summary>
public class OrdenConversacionesUnificadasTests
{
    private static readonly Guid Menor = Guid.Parse("00000001-0000-0000-0000-000000000000");
    private static readonly Guid Mayor = Guid.Parse("00000002-0000-0000-0000-000000000000");

    [Fact]
    public void Compara_como_uuid_por_bytes_en_orden_de_escritura()
    {
        OrdenConversacionesUnificadas.CompararComoUuid(Menor, Mayor).Should().BeNegative();
        OrdenConversacionesUnificadas.CompararComoUuid(Mayor, Menor).Should().BePositive();
        OrdenConversacionesUnificadas.CompararComoUuid(Mayor, Mayor).Should().Be(0);

        // El último grupo pesa menos que el primero en el orden de uuid.
        var a = Guid.Parse("10000000-0000-0000-0000-ffffffffffff");
        var b = Guid.Parse("20000000-0000-0000-0000-000000000000");
        OrdenConversacionesUnificadas.CompararComoUuid(a, b).Should().BeNegative();
    }

    [Fact]
    public void Coincide_con_el_orden_del_texto_hexadecimal_que_es_el_orden_de_bytes_del_uuid()
    {
        var aleatorio = new Random(20261120);
        var bytes = new byte[16];
        for (var i = 0; i < 2_000; i++)
        {
            aleatorio.NextBytes(bytes);
            var a = new Guid(bytes, bigEndian: true);
            aleatorio.NextBytes(bytes);
            var b = new Guid(bytes, bigEndian: true);

            Math.Sign(OrdenConversacionesUnificadas.CompararComoUuid(a, b))
                .Should().Be(Math.Sign(string.CompareOrdinal(a.ToString("N"), b.ToString("N"))), $"{a} frente a {b}");
        }
    }

    [Fact]
    public void A_igual_fecha_el_uuid_mayor_va_primero_y_a_fecha_distinta_manda_la_fecha()
    {
        var fecha = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);

        OrdenConversacionesUnificadas.Comparar(fecha, Mayor, fecha, Menor).Should().BeNegative();
        OrdenConversacionesUnificadas.Comparar(fecha, Menor, fecha, Mayor).Should().BePositive();
        OrdenConversacionesUnificadas.Comparar(fecha.AddSeconds(1), Menor, fecha, Mayor).Should().BeNegative();
        OrdenConversacionesUnificadas.Comparar(fecha, Mayor, fecha, Mayor).Should().Be(0);
    }

    [Fact]
    public void Trunca_a_microsegundos_como_timestamp_de_PostgreSQL()
    {
        var conTicks = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567);

        var truncada = OrdenConversacionesUnificadas.TruncarAMicrosegundos(conTicks);

        truncada.Ticks.Should().Be(conTicks.Ticks - 7);
        truncada.Kind.Should().Be(DateTimeKind.Utc);
        OrdenConversacionesUnificadas.TruncarAMicrosegundos(truncada).Should().Be(truncada);
    }
}
