using CaeManager.Domain.Configuracion;
using FluentAssertions;

namespace CaeManager.Domain.Tests.Configuracion;

public class PreferenciaDashboardUsuarioTests
{
    private static readonly Guid Usuario = Guid.NewGuid();

    [Fact]
    public void Rechaza_un_usuario_vacio()
    {
        var crear = () => new PreferenciaDashboardUsuario(Guid.Empty, ["doc.tasa-cumplimiento"]);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_una_seleccion_vacia()
    {
        var crear = () => new PreferenciaDashboardUsuario(Usuario, []);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Guarda_los_codigos_seleccionados()
    {
        var preferencia = new PreferenciaDashboardUsuario(Usuario, ["doc.tasa-cumplimiento", "eval.puntuacion-media"]);

        preferencia.UsuarioId.Should().Be(Usuario);
        preferencia.CodigosKpiSeleccionados.Should().BeEquivalentTo(["doc.tasa-cumplimiento", "eval.puntuacion-media"]);
    }

    [Fact]
    public void Actualizar_la_seleccion_reemplaza_la_lista_anterior()
    {
        var preferencia = new PreferenciaDashboardUsuario(Usuario, ["doc.tasa-cumplimiento"]);

        preferencia.ActualizarSeleccion(["inc.total-abiertas", "ia.confianza-media"]);

        preferencia.CodigosKpiSeleccionados.Should().BeEquivalentTo(["inc.total-abiertas", "ia.confianza-media"]);
    }

    [Fact]
    public void Actualizar_la_seleccion_rechaza_una_lista_vacia()
    {
        var preferencia = new PreferenciaDashboardUsuario(Usuario, ["doc.tasa-cumplimiento"]);

        var actualizar = () => preferencia.ActualizarSeleccion([]);

        actualizar.Should().Throw<ArgumentException>();
        preferencia.CodigosKpiSeleccionados.Should().BeEquivalentTo(
            ["doc.tasa-cumplimiento"], "la seleccion anterior debe sobrevivir a un intento invalido");
    }
}
