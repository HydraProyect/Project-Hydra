using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Integraciones;

public class ConexionPlataformaCaeTests
{
    private static readonly DateTime Ahora = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void El_conector_por_API_de_una_plataforma_nace_apagado_y_es_independiente_de_la_extension()
    {
        var proveedor = new ProveedorPlataformaCae("plataforma-x", "Plataforma X");

        proveedor.Activo.Should().BeTrue();
        proveedor.ConectorApiActivo.Should().BeFalse();

        proveedor.ActivarConectorApi();
        proveedor.Desactivar();

        proveedor.ConectorApiActivo.Should().BeTrue("el kill switch de la extensión no cambia esta columna");
        proveedor.DesactivarConectorApi();
        proveedor.ConectorApiActivo.Should().BeFalse();
    }

    [Fact]
    public void Rotar_la_credencial_sustituye_el_secreto_y_la_fecha()
    {
        var credencial = new CredencialConexionPlataformaCae(Guid.NewGuid(), "uno", Ahora);

        credencial.Rotar("dos", Ahora.AddDays(1));

        credencial.SecretoApi.Should().Be("dos");
        credencial.ActualizadaEnUtc.Should().Be(Ahora.AddDays(1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Una_credencial_sin_secreto_no_existe(string secreto)
    {
        var crear = () => new CredencialConexionPlataformaCae(Guid.NewGuid(), secreto, Ahora);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Un_uso_se_cierra_una_sola_vez()
    {
        var uso = UsoConexionPlataformaCae.Iniciar(
            Guid.NewGuid(), OperacionConectorPlataformaCae.SubirDocumento, null, Guid.NewGuid(), null, Ahora);
        uso.Resultado.Should().Be(ResultadoUsoConector.EnCurso);

        uso.FinalizarConExito(Ahora.AddSeconds(1));

        uso.Resultado.Should().Be(ResultadoUsoConector.Exito);
        var cerrarOtraVez = () => uso.FinalizarConFallo(MotivoUsoConector.Inesperado, Ahora.AddSeconds(2));
        cerrarOtraVez.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Un_uso_denegado_nace_cerrado_y_con_motivo()
    {
        var uso = UsoConexionPlataformaCae.Denegar(
            Guid.NewGuid(), OperacionConectorPlataformaCae.LeerEstadoDocumento, null, Guid.NewGuid(), null,
            MotivoUsoConector.AddOnInactivo, Ahora);

        uso.Resultado.Should().Be(ResultadoUsoConector.Denegado);
        uso.Motivo.Should().Be(MotivoUsoConector.AddOnInactivo);
        uso.FinalizadoEnUtc.Should().Be(Ahora);
    }
}
