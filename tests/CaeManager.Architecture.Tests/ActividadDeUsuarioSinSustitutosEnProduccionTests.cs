using CaeManager.Web.Services;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>ActividadUsuarioService.RegistrarYEvaluarAsync</c> es <c>virtual</c> para
/// que los tests de componente de Inicio puedan sustituirlo: la implementación
/// real necesita un <c>UserManager</c> que escriba de verdad, y montar ese
/// aparato solo para decidir si aparece el resumen «qué llegó sin ver» haría
/// que el andamio tapara lo que el caso mide.
///
/// Pero abrir el método quita una garantía que antes daba el compilador: que
/// toda invocación ejecuta el registro y la evaluación reales. Basta con que
/// alguien registre una subclase en el contenedor para que la escritura de
/// <c>UltimaActividadUtc</c> deje de ocurrir, o para que el corte de ausencia
/// se calcule de otra forma — sin que nada falle, porque el tipo declarado en
/// DI seguiría siendo el mismo. Lo señaló la revisión de Codex sobre la rama de
/// Inicio Gen 2 (2026-09-12).
///
/// Este trinquete devuelve esa garantía donde importa: en producción no hay
/// ninguna subclase. En los tests, que es para lo que se abrió, sí las hay, y
/// por eso la comprobación mira solo el ensamblado de la aplicación web.
/// </summary>
public class ActividadDeUsuarioSinSustitutosEnProduccionTests
{
    [Fact]
    public void Ningun_tipo_de_la_aplicacion_web_sustituye_el_registro_de_actividad()
    {
        var web = typeof(ActividadUsuarioService).Assembly;

        var sustitutos = web.GetTypes()
            .Where(t => t != typeof(ActividadUsuarioService) && typeof(ActividadUsuarioService).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToList();

        sustitutos.Should().BeEmpty(
            "el método es virtual solo para los tests de componente: una subclase en producción podría dejar de " +
            "escribir UltimaActividadUtc o mover el corte de ausencia sin que nada fallara, porque el tipo " +
            "registrado en el contenedor seguiría siendo el mismo");
    }
}
