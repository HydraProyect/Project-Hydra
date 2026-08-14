using CaeManager.Domain.VigilanciaNormativa;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.VigilanciaNormativa;

public class AvisoRevisionNormativaTests
{
    private static AvisoRevisionNormativa CrearAviso() => new(
        "BOE-A-2026-17626",
        new DateOnly(2026, 8, 13),
        "Real Decreto 171/2004, de coordinación de actividades empresariales.",
        "https://www.boe.es/diario_boe/txt.php?id=BOE-A-2026-17626",
        "RD 171/2004",
        DateTime.UtcNow);

    [Fact]
    public void Crea_un_aviso_valido_sin_revisar()
    {
        var aviso = CrearAviso();

        aviso.IdentificadorBoe.Should().Be("BOE-A-2026-17626");
        aviso.NormaVigilada.Should().Be("RD 171/2004");
        aviso.Revisado.Should().BeFalse();
        aviso.RevisadoEnUtc.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void No_permite_crear_un_aviso_sin_identificador_boe(string identificadorInvalido)
    {
        var accion = () => new AvisoRevisionNormativa(
            identificadorInvalido, new DateOnly(2026, 8, 13), "Título", "https://boe.es/x", "LPRL", DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_permite_crear_un_aviso_sin_titulo()
    {
        var accion = () => new AvisoRevisionNormativa(
            "BOE-A-2026-1", new DateOnly(2026, 8, 13), "", "https://boe.es/x", "LPRL", DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarcarRevisado_registra_quien_y_cuando()
    {
        var aviso = CrearAviso();
        var usuarioId = Guid.NewGuid();
        var ahora = DateTime.UtcNow;

        aviso.MarcarRevisado(usuarioId, "No afecta a ningún formato de la biblioteca.", ahora);

        aviso.Revisado.Should().BeTrue();
        aviso.RevisadoPorUsuarioId.Should().Be(usuarioId);
        aviso.RevisadoEnUtc.Should().Be(ahora);
        aviso.NotasRevision.Should().Be("No afecta a ningún formato de la biblioteca.");
    }

    [Fact]
    public void MarcarRevisado_es_idempotente()
    {
        var aviso = CrearAviso();
        var primerUsuario = Guid.NewGuid();
        var primeraFecha = DateTime.UtcNow;
        aviso.MarcarRevisado(primerUsuario, "Primera revisión.", primeraFecha);

        aviso.MarcarRevisado(Guid.NewGuid(), "Segunda revisión.", DateTime.UtcNow.AddMinutes(5));

        aviso.RevisadoPorUsuarioId.Should().Be(primerUsuario);
        aviso.RevisadoEnUtc.Should().Be(primeraFecha);
        aviso.NotasRevision.Should().Be("Primera revisión.");
    }

    [Fact]
    public void MarcarRevisado_permite_notas_vacias()
    {
        var aviso = CrearAviso();

        aviso.MarcarRevisado(Guid.NewGuid(), null, DateTime.UtcNow);

        aviso.Revisado.Should().BeTrue();
        aviso.NotasRevision.Should().BeNull();
    }
}
