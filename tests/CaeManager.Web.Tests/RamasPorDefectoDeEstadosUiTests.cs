using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Centros;
using CaeManager.Web.Features.Documentos;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Los traductores de estado a color/etiqueta tenían ramas por defecto que
/// degradaban a favorable: un valor sin traducir se rotulaba «No aplica» /
/// «Vigente» con tono neutro o de éxito. Un estado nuevo que alguien olvidara
/// añadir no rompía nada — simplemente se pintaba como si no hubiera trabajo
/// que hacer, que es la peor forma de fallar en una herramienta de
/// cumplimiento: enseña calma donde debería enseñar alarma.
///
/// <para>
/// Este ratchet cierra las dos mitades del riesgo: que todo valor declarado
/// tenga su rama explícita (si mañana se añade uno y se olvida la UI, aquí se
/// pone rojo) y que lo desconocido se muestre como desconocido y en rojo, no
/// como sano.
/// </para>
/// </summary>
public class RamasPorDefectoDeEstadosUiTests
{
    private const string TextoDesconocido = "Estado desconocido";

    public static TheoryData<EstadoDocumento> EstadosDocumento =>
        [.. Enum.GetValues<EstadoDocumento>()];

    public static TheoryData<EstadoCentro> EstadosCentro =>
        [.. Enum.GetValues<EstadoCentro>()];

    [Theory]
    [MemberData(nameof(EstadosDocumento))]
    public void Cada_estado_de_documento_declarado_tiene_su_propia_rama(EstadoDocumento estado)
    {
        EstadoDocumentoUi.Texto(estado).Should().NotBe(TextoDesconocido,
            $"«{estado}» existe en el dominio: si cae en la rama por defecto de la UI, " +
            "el usuario ve un estado que la aplicación no sabe explicar");
    }

    [Theory]
    [MemberData(nameof(EstadosCentro))]
    public void Cada_estado_de_centro_declarado_tiene_su_propia_rama(EstadoCentro estado)
    {
        EstadoCentroUi.Texto(estado).Should().NotBe(TextoDesconocido,
            $"«{estado}» existe en el dominio: si cae en la rama por defecto de la UI, " +
            "el usuario ve un estado que la aplicación no sabe explicar");
    }

    [Fact]
    public void Un_estado_de_documento_desconocido_no_se_pinta_como_sano()
    {
        var desconocido = (EstadoDocumento)9999;

        EstadoDocumentoUi.Texto(desconocido).Should().Be(TextoDesconocido);
        EstadoDocumentoUi.Tono(desconocido).Should().Be(TonoBadge.Peligro,
            "un valor que la UI no sabe traducir nunca puede degradar a un tono tranquilizador");
    }

    [Fact]
    public void Un_estado_de_centro_desconocido_no_se_pinta_como_sano()
    {
        var desconocido = (EstadoCentro)9999;

        EstadoCentroUi.Texto(desconocido).Should().Be(TextoDesconocido);
        EstadoCentroUi.Tono(desconocido).Should().Be(TonoBadge.Peligro,
            "un valor que la UI no sabe traducir nunca puede degradar a un tono tranquilizador");
    }
}
