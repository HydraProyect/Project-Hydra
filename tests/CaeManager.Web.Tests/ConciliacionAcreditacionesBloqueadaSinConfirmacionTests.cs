using CaeManager.Infrastructure.Documentos;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// D-4 sobre la #2005 (decisión del 2026-11-15): la conciliación de la regla A-1
/// (<see cref="ConciliacionAcreditacionesEmpresaHostedService"/>) es un proceso sin persona que crea Pendientes en
/// plataforma, y esos Pendientes bloquean el acceso. Sigue apagada por defecto y no se puede encender mientras no exista
/// la confirmación de un Gestor CAE. Este test congela las dos cosas; el incremento que construya la confirmación es el
/// único que debe cambiarlo.
/// </summary>
public class ConciliacionAcreditacionesBloqueadaSinConfirmacionTests
{
    [Fact]
    public void La_confirmacion_de_un_Gestor_CAE_todavia_no_existe()
    {
        ConciliacionAcreditacionesEmpresaOptions.ConfirmacionDeGestorCaeDisponible.Should().BeFalse(
            "solo lo pasa a verdadero el cambio que construya la confirmación de los Pendientes que crea la conciliación");
    }

    [Fact]
    public void Sin_configuracion_esta_apagada_y_no_se_registra()
    {
        var opciones = Enlazar(new Dictionary<string, string?>());

        opciones.EjecutarAlArrancar.Should().BeFalse("apagada por defecto en todos los entornos");
        ConciliacionAcreditacionesEmpresaOptions.DebeRegistrarse(opciones).Should().BeFalse();
    }

    [Fact]
    public void Encenderla_sin_la_confirmacion_detiene_el_arranque()
    {
        var opciones = Enlazar(new Dictionary<string, string?>
        {
            [$"{ConciliacionAcreditacionesEmpresaOptions.SeccionConfiguracion}:{nameof(ConciliacionAcreditacionesEmpresaOptions.EjecutarAlArrancar)}"] = "true",
        });
        opciones.EjecutarAlArrancar.Should().BeTrue("control: la variable sí llega a la opción");

        var registrar = () => ConciliacionAcreditacionesEmpresaOptions.DebeRegistrarse(opciones);

        registrar.Should().Throw<InvalidOperationException>()
            .WithMessage("*Gestor CAE*", "el arranque dice por qué no deja encenderla");
    }

    private static ConciliacionAcreditacionesEmpresaOptions Enlazar(Dictionary<string, string?> valores)
    {
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
        var opciones = new ConciliacionAcreditacionesEmpresaOptions();
        configuracion.GetSection(ConciliacionAcreditacionesEmpresaOptions.SeccionConfiguracion).Bind(opciones);
        return opciones;
    }
}
