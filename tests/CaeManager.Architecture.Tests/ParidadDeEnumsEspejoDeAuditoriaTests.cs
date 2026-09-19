using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Los enums espejo de la auditoría tienen los mismos nombres y los mismos
/// números</b> (P41c, seguimiento).
///
/// <para>
/// Domain no puede referenciar Application, así que la auditoría tiene dos
/// enums por cada eje —<c>TipoActor</c>/<c>TipoActorAuditoria</c> y
/// <c>TipoViaAcceso</c>/<c>TipoViaAccesoAuditoria</c>— unidos por un
/// <i>cast</i> numérico en el interceptor y en el servicio de acceso a
/// documentos sensibles. Un cast entre enums no comprueba nada: si alguien
/// inserta un valor en uno y no en el otro, compila, no falla ningún test que
/// use un solo valor y la auditoría empieza a escribir el actor equivocado. Hasta
/// este test, la única defensa era un comentario.
/// </para>
///
/// <para>
/// Se comparan el <b>conjunto de pares nombre→número</b>, no solo el número: un
/// intercambio de dos nombres con los mismos valores numéricos pasaría por alto
/// una comparación de recuentos. La persistencia guarda el <i>nombre</i>
/// (<c>HasConversion&lt;string&gt;</c>), así que el nombre es lo que sobrevive en
/// la fila; el número es lo que usa el cast. Los dos tienen que coincidir.
/// </para>
/// </summary>
public class ParidadDeEnumsEspejoDeAuditoriaTests
{
    [Fact]
    public void TipoActor_de_Application_y_de_Domain_coinciden_en_nombre_y_numero()
        => Pares<TipoActor>().Should().BeEquivalentTo(Pares<TipoActorAuditoria>(),
            "el interceptor y el servicio de acceso los unen con un cast numérico");

    [Fact]
    public void TipoViaAcceso_de_Application_y_de_Domain_coinciden_en_nombre_y_numero()
        => Pares<TipoViaAcceso>().Should().BeEquivalentTo(Pares<TipoViaAccesoAuditoria>(),
            "el interceptor y el servicio de acceso los unen con un cast numérico");

    /// <summary>
    /// Control positivo del instrumento: el comparador tiene que ver una
    /// diferencia real. Sin esto, un <c>Pares</c> que devolviera siempre lo mismo
    /// —o una comparación que no mirase los números— daría verde permanente.
    /// </summary>
    [Fact]
    public void El_comparador_detecta_una_divergencia_de_numero_y_una_de_nombre()
    {
        var referencia = new Dictionary<string, int> { ["A"] = 0, ["B"] = 1 };

        referencia.Should().NotBeEquivalentTo(new Dictionary<string, int> { ["A"] = 0, ["B"] = 2 },
            "mismo nombre con otro número es exactamente el fallo que el cast no ve");
        referencia.Should().NotBeEquivalentTo(new Dictionary<string, int> { ["A"] = 1, ["B"] = 0 },
            "dos nombres intercambiados conservan el recuento y los números, pero no el par");
        referencia.Should().NotBeEquivalentTo(new Dictionary<string, int> { ["A"] = 0, ["B"] = 1, ["C"] = 2 },
            "un valor añadido en un solo lado");
    }

    private static Dictionary<string, int> Pares<TEnum>() where TEnum : struct, Enum
        => Enum.GetValues<TEnum>().ToDictionary(v => v.ToString(), v => Convert.ToInt32(v));
}
