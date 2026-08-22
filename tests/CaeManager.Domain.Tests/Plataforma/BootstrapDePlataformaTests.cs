using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Plataforma;

/// <summary>
/// Las invariantes que A2 introduce, probadas <b>en su nivel</b>.
///
/// <para>
/// Existen porque la batería de integración estaba prestándole evidencia al
/// dominio sin que se notara. Que "no se puede arrancar dos veces" está probado
/// allí, pero por el camino de la autorización: la segunda tentativa se deniega
/// antes y <c>Consumir()</c> nunca llega a invocarse por segunda vez. Con esa
/// sola prueba, alguien podría borrar la guarda interna del agregado y la
/// integración seguiría verde, tapada por la otra barrera.
/// </para>
///
/// <para>
/// Son dos afirmaciones distintas y necesitan pruebas distintas:
/// </para>
/// <code>
/// el sistema no permite arrancar dos veces      → autorización (integración)
/// Consumir() es intrínsecamente irreversible    → el agregado (aquí)
/// </code>
/// </summary>
public class BootstrapDePlataformaTests
{
    [Fact]
    public void Consumir_dos_veces_no_es_silencioso()
    {
        var estado = EstadoBootstrapPlataforma.Designar(Guid.NewGuid(), DateTime.UtcNow);
        estado.Consumir(DateTime.UtcNow);

        var segundoConsumo = () => estado.Consumir(DateTime.UtcNow);

        segundoConsumo.Should().Throw<InvalidOperationException>(
            "consumido es consumido: si esta guarda desapareciera, revocar la concesión fundacional " +
            "reabriría el bootstrap y tendríamos una autoridad de emergencia permanente escondida " +
            "tras la ausencia de una fila");

        estado.Consumido.Should().BeTrue();
    }

    [Fact]
    public void No_se_designa_una_raiz_vacia()
    {
        var designar = () => EstadoBootstrapPlataforma.Designar(Guid.Empty, DateTime.UtcNow);

        designar.Should().Throw<ArgumentException>(
            "una raíz vacía dejaría el bootstrap designado y sin dueño: PuedeArrancar compararía " +
            "contra Guid.Empty y nadie podría satisfacerlo, pero el estado parecería inicializado");
    }

    [Fact]
    public void No_hay_concesion_fundacional_sin_beneficiario()
    {
        var crear = () => ConcesionPrivilegio.RaizDeBootstrap(Guid.Empty, DateTime.UtcNow, vigenciaHasta: null);

        crear.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Los cuatro rasgos de la fundacional van juntos y ninguno es parámetro. Si
    /// alguno pudiera variar, reconocerla dejaría de ser inequívoco — y ese fue
    /// justamente el motivo de introducir <see cref="OrigenConcesion"/>: por su
    /// forma no se distingue, porque <c>Global()</c> obliga a
    /// <c>AdminPlataforma</c> y toda concesión global futura tendrá el mismo
    /// aspecto.
    /// </summary>
    [Fact]
    public void La_concesion_fundacional_es_AdminPlataforma_global_y_marcada_como_bootstrap()
    {
        var raiz = Guid.NewGuid();
        var ahora = DateTime.UtcNow;

        var concesion = ConcesionPrivilegio.RaizDeBootstrap(raiz, ahora, vigenciaHasta: null);

        concesion.Capacidad.Should().Be(CapacidadPrivilegio.AdminPlataforma);
        concesion.EsAlcanceGlobal.Should().BeTrue();
        concesion.Origen.Should().Be(OrigenConcesion.BootstrapPlataforma);
        concesion.UsuarioPlataformaId.Should().Be(raiz);
        concesion.ConcedidaPorUsuarioId.Should().Be(raiz, "el acto fundacional es de la raíz sobre sí misma");
    }
}
