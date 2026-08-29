using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Documentos;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// El contrato de vocabulario del eje de aceptación por un tercero
/// (decisiones del propietario, 2026-08-29). No comprueba estética: comprueba
/// las tres propiedades que el inventario de vocabulario encontró rotas y que
/// nada más vigila.
/// </summary>
public class EstadoAcreditacionUiTests
{
    private static readonly EstadoAcreditacion[] TodosLosEstados =
        Enum.GetValues<EstadoAcreditacion>();

    [Fact]
    public void Cada_valor_tiene_su_propio_texto_y_ninguno_se_repite()
    {
        // Un solo texto por valor, y textos distintos entre sí: si dos valores
        // comparten etiqueta, el usuario no puede distinguirlos en pantalla —
        // que es la mitad del problema que este vocabulario cierra.
        var textos = TodosLosEstados.Select(EstadoAcreditacionUi.Texto).ToList();

        textos.Should().OnlyHaveUniqueItems(
            "dos estados con la misma etiqueta son indistinguibles para quien los lee");
        textos.Should().NotContain("—",
            "el guion es la rama por defecto: si aparece, hay un valor del enum sin traducir");
    }

    [Fact]
    public void Ningun_texto_usa_validado_ni_sus_variantes()
    {
        // «Validado» queda reservado al eje de confianza técnica del archivo
        // (DecisionValidacionOficial, NivelConfianzaDocumental). Compartir la
        // palabra entre los dos ejes es exactamente lo que hacía que
        // «validación» significara cosas contrarias según la pantalla.
        foreach (var estado in TodosLosEstados)
        {
            EstadoAcreditacionUi.Texto(estado).Should().NotContainEquivalentOf("validad",
                $"«{estado}» pertenece al eje de aceptación por un tercero, que se dice «aceptada» — " +
                "«validado» está reservado al eje de confianza técnica del archivo");
        }
    }

    [Theory]
    [InlineData(EstadoAcreditacion.Subida, "enviada")]
    [InlineData(EstadoAcreditacion.Aceptada, "aceptada")]
    [InlineData(EstadoAcreditacion.Rechazada, "rechazada")]
    [InlineData(EstadoAcreditacion.NoRequerida, "no exigida")]
    public void Los_participios_concuerdan_en_femenino_con_la_acreditacion(
        EstadoAcreditacion estado, string esperado)
    {
        // El enum ya está en femenino (Subida, Aceptada, Rechazada,
        // NoRequerida); la interfaz lo renderizaba en masculino. Decisión del
        // propietario: gana el femenino, que concuerda con el concepto que la
        // etiqueta nombra.
        EstadoAcreditacionUi.Texto(estado).Should().Be(esperado);
    }

    [Fact]
    public void El_texto_capitalizado_solo_cambia_la_inicial()
    {
        // Se deriva del texto único en vez de mantener una segunda tabla: si
        // divergieran, volveríamos a tener dos vocabularios para un valor.
        foreach (var estado in TodosLosEstados)
        {
            var texto = EstadoAcreditacionUi.Texto(estado);
            var capitalizado = EstadoAcreditacionUi.TextoCapitalizado(estado);

            capitalizado.Should().BeEquivalentTo(texto,
                $"«{estado}» debe ser la misma etiqueta, solo con otra inicial");
            capitalizado[0].Should().Be(char.ToUpperInvariant(texto[0]));
        }
    }
}
