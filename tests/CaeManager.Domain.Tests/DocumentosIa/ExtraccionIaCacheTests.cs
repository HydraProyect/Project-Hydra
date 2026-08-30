using CaeManager.Domain.DocumentosIa;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.DocumentosIa;

public class ExtraccionIaCacheTests
{
    private static string HashDeEjemplo() => new('a', ExtraccionIaCache.LongitudHash);

    [Fact]
    public void Crea_una_entrada_de_cache_valida()
    {
        var cache = ExtraccionIaCache.Crear(HashDeEjemplo(), "Póliza de seguro", "{\"tipoDetectado\":\"Póliza\"}");

        cache.HashSha256.Should().Be(HashDeEjemplo());
        cache.ExtraccionJson.Should().Be("{\"tipoDetectado\":\"Póliza\"}");
        cache.TipoEsperado.Should().Be("póliza de seguro", "el tipo se guarda normalizado, que es como se busca");
        cache.VersionPipeline.Should().Be(ExtraccionIaCache.VersionPipelineActual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("demasiado-corto")]
    public void Rechaza_un_hash_que_no_tenga_la_longitud_de_sha256(string hash)
    {
        var accion = () => ExtraccionIaCache.Crear(hash, "Póliza", "{}");

        accion.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Misma normalización al escribir que al leer: si divergieran, la caché no
    /// acertaría nunca y el fallo sería mudo — todo seguiría funcionando, solo
    /// que pagando cada extracción dos veces.
    /// </summary>
    [Theory]
    [InlineData("Apto médico", "apto médico")]
    [InlineData("  APTO   MÉDICO  ", "apto médico")]
    [InlineData("Apto	Médico", "apto médico")]
    public void Normaliza_el_tipo_esperado_para_que_la_clave_sea_estable(string entrada, string esperado)
    {
        ExtraccionIaCache.Crear(HashDeEjemplo(), entrada, "{}").TipoEsperado.Should().Be(esperado);
    }

    [Fact]
    public void Rechaza_un_json_vacio()
    {
        var accion = () => ExtraccionIaCache.Crear(HashDeEjemplo(), "Póliza", "   ");

        accion.Should().Throw<ArgumentException>();
    }
}
