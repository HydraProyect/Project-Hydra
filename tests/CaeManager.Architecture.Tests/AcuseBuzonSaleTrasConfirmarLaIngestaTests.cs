using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// El acuse no se deshace: si saliera antes de confirmar la ingesta, un guardado fallido devolvería el evento a la cola y el
/// reintento lo repetiría. Y fuera del ámbito del evento no tendría el Tenant del buzón. Solo se puede medir sobre el texto
/// de <c>IngestaWebhookHostedService</c>, que abre esos ámbitos y no tiene costura para sustituirlos en una prueba.
/// </summary>
public class AcuseBuzonSaleTrasConfirmarLaIngestaTests
{
    [Fact]
    public void El_servicio_hospedado_envia_el_acuse_despues_de_guardar_y_dentro_del_ambito_del_Tenant()
    {
        var texto = File.ReadAllText(Path.Combine(
            BuscarRaizDelRepositorio(), "src", "CaeManager.Infrastructure", "Integraciones", "IngestaWebhookHostedService.cs"));

        var ambito = texto.IndexOf("using var _ = AmbitoTenantExplicito.Establecer(tenantId);", texto.IndexOf("ambitoFactory.CreateScope();", StringComparison.Ordinal), StringComparison.Ordinal);
        var ingesta = texto.IndexOf("ingesta.ProcesarAsync(evento, stoppingToken)", StringComparison.Ordinal);
        var guardado = texto.IndexOf("SaveChangesAsync(stoppingToken)", ingesta, StringComparison.Ordinal);
        var acuse = texto.IndexOf("GetRequiredService<AcuseAutomaticoBuzonService>()", StringComparison.Ordinal);

        ingesta.Should().BeGreaterThan(ambito).And.BeGreaterThan(0);
        guardado.Should().BeGreaterThan(ingesta, "la ingesta se confirma después de procesar el evento");
        acuse.Should().BeGreaterThan(guardado, "un acuse enviado antes de confirmar se repetiría al reintentar el evento");
        texto.IndexOf("ambito.ServiceProvider.GetRequiredService<AcuseAutomaticoBuzonService>()", StringComparison.Ordinal)
            .Should().BeGreaterThan(0, "el acuse usa el mismo ámbito, y por tanto el mismo Tenant, que la ingesta del evento");
    }

    private static string BuscarRaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        actual.Should().NotBeNull("los tests tienen que correr dentro del repositorio");
        return actual!.FullName;
    }
}
