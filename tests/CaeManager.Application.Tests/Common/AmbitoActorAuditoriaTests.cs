using CaeManager.Application.Common;
using FluentAssertions;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// La liberación del ámbito de actor (P41c). Se prueba aquí, en un método
/// síncrono, porque es el único sitio donde se puede <i>observar</i>: dentro de un
/// método <c>async</c> lo que se asigne a un <c>AsyncLocal</c> no llega al
/// llamador con o sin <c>using</c>, así que una comprobación puesta en un test de
/// filtro o de servicio de fondo pasaría aunque <c>Dispose</c> no hiciera nada.
/// </summary>
public class AmbitoActorAuditoriaTests
{
    [Fact]
    public void Sin_declarar_nada_no_hay_tipo_de_actor()
        => AmbitoActorAuditoria.TipoActorActual.Should().BeNull();

    [Fact]
    public void Al_liberar_el_ambito_vuelve_al_valor_anterior_y_los_anidados_se_apilan()
    {
        using (AmbitoActorAuditoria.EstablecerSistema())
        {
            AmbitoActorAuditoria.TipoActorActual.Should().Be(TipoActor.Sistema);

            using (AmbitoActorAuditoria.EstablecerIntegracionExterna())
                AmbitoActorAuditoria.TipoActorActual.Should().Be(TipoActor.IntegracionExterna);

            AmbitoActorAuditoria.TipoActorActual.Should().Be(TipoActor.Sistema,
                "liberar el interno restaura el externo, no lo borra");
        }

        AmbitoActorAuditoria.TipoActorActual.Should().BeNull("liberar el externo deja el ámbito como estaba");
    }

    [Fact]
    public void Persona_no_se_puede_declarar()
    {
        var metodos = typeof(AmbitoActorAuditoria).GetMethods()
            .Where(m => m.DeclaringType == typeof(AmbitoActorAuditoria) && m.Name.StartsWith("Establecer", StringComparison.Ordinal))
            .Select(m => m.Name)
            .ToList();

        metodos.Should().BeEquivalentTo(["EstablecerSistema", "EstablecerIntegracionExterna"],
            "Persona solo la produce la identidad de sesión resuelta: un método público que la declarase " +
            "haría que el registro afirmara lo que le dijeron en vez de lo que ocurrió");
    }
}
