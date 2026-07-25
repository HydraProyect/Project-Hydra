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
        var cache = ExtraccionIaCache.Crear(HashDeEjemplo(), "{\"tipoDetectado\":\"Póliza\"}");

        cache.HashSha256.Should().Be(HashDeEjemplo());
        cache.ExtraccionJson.Should().Be("{\"tipoDetectado\":\"Póliza\"}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("demasiado-corto")]
    public void Rechaza_un_hash_que_no_tenga_la_longitud_de_sha256(string hash)
    {
        var accion = () => ExtraccionIaCache.Crear(hash, "{}");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_json_vacio()
    {
        var accion = () => ExtraccionIaCache.Crear(HashDeEjemplo(), "   ");

        accion.Should().Throw<ArgumentException>();
    }
}
