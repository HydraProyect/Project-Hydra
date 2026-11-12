using CaeManager.Web.Features.AltaAutonoma.Antiabuso;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.Web.Tests.AltaAutonoma;

/// <summary>
/// Alerta de consultas repetidas de un mismo CIF en el paso «Empresa» (A4): avisa en el registro al pasar del umbral, por
/// CIF, y el aviso no lleva el CIF ni nada de lo que se pueda volver a él. No bloquea: no devuelve nada a quien la llama.
/// </summary>
public class AlertaConsultasCifAltaTests
{
    private readonly RegistroCapturado _registro = new();

    [Fact]
    public void Hasta_el_umbral_no_avisa_y_la_siguiente_consulta_si()
    {
        using var alerta = new AlertaConsultasCifAlta(_registro);

        for (var i = 0; i < AlertaConsultasCifAlta.Umbral; i++)
            alerta.Registrar("B12345674");
        _registro.Entradas.Should().BeEmpty();

        alerta.Registrar("B12345674");
        alerta.Registrar("B12345674");

        _registro.Entradas.Should().HaveCount(2).And.OnlyContain(e =>
            e.Evento == AlertaConsultasCifAlta.EventoConsultasRepetidas && e.Nivel == LogLevel.Warning);
    }

    [Fact]
    public async Task Pasada_la_ventana_las_consultas_anteriores_dejan_de_contar()
    {
        using var alerta = new AlertaConsultasCifAlta(_registro, TimeSpan.FromMilliseconds(240));

        for (var i = 0; i < AlertaConsultasCifAlta.Umbral; i++)
            alerta.Registrar("B12345674");
        await Task.Delay(TimeSpan.FromSeconds(1));
        alerta.Registrar("B12345674");

        _registro.Entradas.Should().BeEmpty("las tres consultas de antes quedaron fuera de la ventana");
    }

    [Fact]
    public void La_ventana_por_defecto_es_de_un_dia()
    {
        AlertaConsultasCifAlta.VentanaPorDefecto.Should().Be(TimeSpan.FromDays(1));
    }

    [Fact]
    public void Cada_CIF_cuenta_por_separado()
    {
        using var alerta = new AlertaConsultasCifAlta(_registro);

        for (var i = 0; i < AlertaConsultasCifAlta.Umbral; i++)
        {
            alerta.Registrar("B12345674");
            alerta.Registrar("12345678Z");
        }

        _registro.Entradas.Should().BeEmpty();
    }

    [Fact]
    public void El_aviso_no_lleva_el_CIF_ni_su_clave()
    {
        using var alerta = new AlertaConsultasCifAlta(_registro);

        for (var i = 0; i <= AlertaConsultasCifAlta.Umbral; i++)
            alerta.Registrar("12345678Z");

        var aviso = _registro.Entradas.Should().ContainSingle().Subject;
        aviso.Texto.Should().NotContain("12345678");
        aviso.Valores.Select(v => v.Key).Should().BeEquivalentTo(["Umbral", "{OriginalFormat}"]);
    }

    /// <summary>Registro que guarda lo que se escribe, para comprobar evento, nivel, texto y valores.</summary>
    internal sealed class RegistroCapturado : ILogger<AlertaConsultasCifAlta>
    {
        public List<Entrada> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var valores = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            lock (Entradas)
                Entradas.Add(new Entrada(logLevel, eventId, formatter(state, exception), valores));
        }

        internal sealed record Entrada(LogLevel Nivel, EventId Evento, string Texto, IReadOnlyList<KeyValuePair<string, object?>> Valores);
    }
}
